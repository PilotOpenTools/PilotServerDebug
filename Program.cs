using System.Diagnostics;
using System.Text;
using Ascon.Pilot.ServerExtensions.SDK;
using Debug;

Console.OutputEncoding = Encoding.UTF8;

try
{
    var options = Options.Parse(args);
    if (options.Help)
    {
        Options.PrintHelp();
        return 0;
    }
    return await Runner.RunAsync(options);
}
catch (UsageException ex)
{
    Out.Error(ex.Message);
    Out.Line("Справка: debug --help");
    return 2;
}
finally
{
    // Под F5 окно консоли закрывается вместе с процессом - не даём потерять отчёт об изменениях
    if (Debugger.IsAttached && !Console.IsInputRedirected)
    {
        Out.Line("");
        Out.Line("Нажмите любую клавишу, чтобы закрыть окно…");
        Console.ReadKey(true);
    }
}

namespace Debug
{
    internal static class Runner
    {
        public static async Task<int> RunAsync(Options options)
        {
            var c = options.CasePath is not null ? DebugCase.Load(options.CasePath) : new DebugCase { BaseDir = Environment.CurrentDirectory };
            options.ApplyTo(c);
            if (options.CasePath is not null)
                Out.Info($"Кейс: {Path.GetFullPath(options.CasePath)}");
            if (options.CaseFoundUp)
                Out.Warn("Кейс найден автоматически в текущей или родительской папке: проверьте server и module - на этот сервер уйдёт пароль, эта сборка будет выполнена.");

            if (string.IsNullOrWhiteSpace(c.Server))
                throw new UsageException("Не задан сервер: \"server\" в кейсе или --server http://сервер:порт/база.");
            if (c.SourceId is null)
                throw new UsageException("Не задан объект-источник: \"sourceId\" в кейсе или --source <guid> (Id объекта в Pilot).");

            if (options.Wait)
                Out.WaitForDebugger();

            var modulePath = ModuleLoader.Resolve(c.Module, c.BaseDir ?? Environment.CurrentDirectory);
            var activity = ModuleLoader.Load(modulePath, c.Activity);

            var login = c.Login;
            if (string.IsNullOrWhiteSpace(login))
                login = Out.Ask("Логин Pilot: ");
            switch (WriteGuard.Decide(c.Contour, c.Apply, options.Yes, options.YesProd))
            {
                case WriteDecision.AskYesNo when !Out.Confirm($"Изменения модуля будут записаны на {c.Server} от имени {login}. Продолжить?"):
                    Out.Warn("Запись отменена. Запуск без вопроса - ключ --yes, просмотр без записи - без --apply.");
                    return 1;
                case WriteDecision.AskProdWord:
                    if (options.Yes)
                        Out.Warn("Контур - ПРОД: --yes здесь не действует. Без вопроса на прод пишет только --yes-prod.");
                    if (!WriteGuard.IsProdWord(Out.Ask($"ПРОД: изменения модуля будут записаны на {c.Server} от имени {login}. Для подтверждения введите слово {WriteGuard.ProdWord}: ")))
                    {
                        Out.Warn("Запись на прод отменена. Просмотр без записи - без --apply.");
                        return 1;
                    }
                    break;
                case WriteDecision.Proceed when WriteGuard.IsProd(c.Contour):
                    Out.Warn($"Контур - ПРОД: запись на {c.Server} без вопроса (--yes-prod).");
                    break;
            }
            var password = Environment.GetEnvironmentVariable("DEBUG_PASSWORD") ?? Out.AskSecret($"Пароль {login} на {c.Server}: ");

            Out.Info($"Подключение к {c.Server} как {login}…");
            using var connection = PilotConnection.Open(c.Server, login!, password, c.LicenseType);
            var backend = connection.Backend;

            var source = backend.GetObject(c.SourceId.Value)
                         ?? throw new UsageException($"Объект {c.SourceId} не найден или у {login} нет прав на него.");
            var types = backend.GetTypes();
            var sourceType = types.TryGetValue(source.TypeId, out var st) ? st : null;
            Out.Info($"Источник: {source.Id} · тип {sourceType?.Title} ({sourceType?.Name})");
            if (c.SourceTypes.Count > 0 && sourceType is not null && !c.SourceTypes.Contains(sourceType.Name))
                Out.Warn($"Тип источника «{sourceType.Name}» не входит в sourceTypes сценария ({string.Join(", ", c.SourceTypes)}) - на сервере событие для него не сработало бы.");

            var parameters = c.ParamsForActivity();
            var context = new DebugActivityContext(parameters, c.SourceTypes);
            context.Register<IAutomationNotifier>(new DebugNotifier());
            context.Register<IAutomationFileProvider>(new DebugFileProvider(connection));
            var ev = new DebugEventContext(source, backend.CurrentPerson());
            var modifier = connection.CreateModifier();
            var live = c.Apply ? new LiveApplier(connection, modifier) : null;
            if (live is not null)
                Out.Warn($"Режим записи: изменения модуля сразу уходят в {c.Server} от имени {login}.");

            Out.Title($"RunAsync «{activity.Name}»{(c.When is null ? "" : $" · событие {c.When}")}");
            foreach (var (key, value) in parameters)
                Out.Line($"    params.{key} = {Newtonsoft.Json.JsonConvert.SerializeObject(value)} ({value?.GetType().Name})");

            var sw = Stopwatch.StartNew();
            try
            {
                if (live is not null)
                    await activity.RunAsync(live.Modifier, live.Backend, context, ev);
                else
                    await activity.RunAsync(modifier, backend, context, ev);
            }
            catch (Exception ex)
            {
                sw.Stop();
                Out.Error($"Активность упала через {sw.ElapsedMilliseconds} мс:");
                Out.Line(ex.ToString());
                if (live is not null && modifier.AnyChanges())
                    Out.Warn("Изменения после последней записи не отправлены (сервер при ошибке активности их тоже не применил бы).");
                return 1;
            }
            sw.Stop();
            Out.Info($"Выполнено за {sw.ElapsedMilliseconds} мс.");

            foreach (var (key, value) in context.Errors)
                Out.Warn($"Errors[{key}] = {value}");

            if (live is not null)
            {
                live.Flush();
                if (live.Applied == 0 && live.Failed == 0)
                    Out.Info("Модуль не внёс изменений.");
                else
                    Out.Info($"Записано на сервер изменений: {live.Applied}{(live.Failed > 0 ? $", отклонено пакетов: {live.Failed}" : "")}.");
                return live.Failed > 0 ? 1 : 0;
            }

            if (ChangeReport.Print(modifier, backend) > 0)
                Out.Info("Режим просмотра: на сервер ничего не записано. Записать - --apply.");
            return 0;
        }
    }

    internal sealed class Options
    {
        public string? CasePath { get; private set; }
        public string? Server { get; private set; }
        public string? Login { get; private set; }
        public string? Module { get; private set; }
        public string? Activity { get; private set; }
        public Guid? Source { get; private set; }
        public int? License { get; private set; }
        public bool Apply { get; private set; }
        public bool Yes { get; private set; }
        /// <summary>Подтверждение записи на прод без вопроса; --yes на прод не действует.</summary>
        public bool YesProd { get; private set; }
        /// <summary>Кейс не указан явно, а найден в текущей или родительской папке.</summary>
        public bool CaseFoundUp { get; private set; }
        public bool Wait { get; private set; }
        public bool Help { get; private set; }
        public Dictionary<string, string> Params { get; } = new();

        public static Options Parse(string[] args)
        {
            var o = new Options();
            for (var i = 0; i < args.Length; i++)
            {
                var a = args[i];
                string Next() => i + 1 < args.Length ? args[++i] : throw new UsageException($"После {a} нужно значение.");
                switch (a)
                {
                    case "-h" or "--help" or "/?": o.Help = true; break;
                    case "--case": o.CasePath = Next(); break;
                    case "--server": o.Server = Next(); break;
                    case "--login": o.Login = Next(); break;
                    case "--module": o.Module = Next(); break;
                    case "--activity": o.Activity = Next(); break;
                    case "--license": o.License = int.Parse(Next()); break;
                    case "--apply": o.Apply = true; break;
                    case "--yes" or "-y": o.Yes = true; break;
                    case "--yes-prod": o.YesProd = true; break;
                    case "--wait": o.Wait = true; break;
                    case "--source":
                        var s = Next();
                        o.Source = Guid.TryParse(s.Trim('{', '}'), out var g) ? g : throw new UsageException($"--source: «{s}» не Guid.");
                        break;
                    case "--param":
                        var p = Next();
                        var eq = p.IndexOf('=');
                        if (eq <= 0) throw new UsageException("--param ожидает имя=значение.");
                        o.Params[p[..eq]] = p[(eq + 1)..];
                        break;
                    default:
                        if (a.StartsWith('-')) throw new UsageException($"Неизвестный ключ {a}.");
                        o.CasePath = a;
                        break;
                }
            }
            if (o.CasePath is null)
            {
                o.CasePath = FindUp("debug.json");
                o.CaseFoundUp = o.CasePath is not null;
            }
            return o;
        }

        /// <summary>Файл в текущей папке или выше: VS запускает из папки проекта или из bin\Debug\…</summary>
        private static string? FindUp(string fileName)
        {
            for (var dir = new DirectoryInfo(Environment.CurrentDirectory); dir is not null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, fileName);
                if (File.Exists(candidate))
                    return candidate;
            }
            return null;
        }

        public void ApplyTo(DebugCase c)
        {
            if (Server is not null)
            {
                var contour = WriteGuard.EffectiveContour(c.Contour, c.Server, Server);
                if (contour != c.Contour)
                    Out.Warn($"--server отличается от server в кейсе: контур «{c.Contour}» не подтверждён, запись потребует подтверждения как для прода.");
                c.Contour = contour;
                c.Server = Server;
            }
            if (Login is not null) c.Login = Login;
            if (Module is not null)
            {
                c.Module = Path.GetFullPath(Module);
            }
            if (Activity is not null) c.Activity = Activity;
            if (Source is not null) c.SourceId = Source;
            if (License is not null) c.LicenseType = License.Value;
            if (Apply) c.Apply = true;
            foreach (var (key, value) in Params)
                c.Params[key] = System.Text.Json.JsonSerializer.SerializeToElement(value);
        }

        public static void PrintHelp() => Out.Line("""
            debug - запуск серверного модуля Pilot на своей машине под отладчиком.

              debug [кейс.json] [ключи]

            Кейс (debug.json) выдаёт админка: «Серверные модули» / «Отладить локально».
            Без кейса берётся debug.json из текущей папки, если есть.

              --module <путь>     *.servext.dll модуля (по умолчанию - свежий в bin рядом с кейсом)
              --source <guid>     объект-источник события (ev.Source)
              --activity <имя>    активность, если в модуле их несколько
              --param имя=знач    добавить/заменить params активности
              --server <url>      http://сервер:порт/база
              --login <логин>     пароль - DEBUG_PASSWORD или запрос при запуске
              --license <n>       тип лицензии (по умолчанию 103)
              --apply             записывать изменения на сервер сразу по ходу выполнения (перед запуском спросит подтверждение)
              --yes, -y           не спрашивать подтверждение записи (на прод не действует)
              --yes-prod          то же для контура prod: без этого ключа на прод пишется только после ввода слова prod
              --wait              ждать подключения отладчика (Debug / Attach to Process / debug.exe)

            Без --apply изменения только печатаются - на сервер ничего не пишется.
            """);
    }

    internal static class Out
    {
        public static void Line(string text) => Console.WriteLine(text);
        public static void Info(string text) => Write(ConsoleColor.Gray, text);
        public static void Warn(string text) => Write(ConsoleColor.Yellow, "! " + text);
        public static void Error(string text) => Write(ConsoleColor.Red, "X " + text);
        public static void Title(string text) => Write(ConsoleColor.Cyan, "── " + text);

        public static string Ask(string prompt)
        {
            Console.Write(prompt);
            return Console.ReadLine()?.Trim() ?? "";
        }

        /// <summary>Пауза до подключения отладчика: Enter - продолжить без него.</summary>
        public static void WaitForDebugger()
        {
            Info($"Жду отладчик: Visual Studio / Debug / Attach to Process / debug.exe (PID {Environment.ProcessId}). Enter - продолжить без него.");
            while (!System.Diagnostics.Debugger.IsAttached)
            {
                if (!Console.IsInputRedirected && Console.KeyAvailable && Console.ReadKey(intercept: true).Key == ConsoleKey.Enter)
                    return;
                Thread.Sleep(200);
            }
            Info("Отладчик подключён.");
        }

        public static bool Confirm(string question) =>
            Ask(question + " [y/N] ").Equals("y", StringComparison.OrdinalIgnoreCase);

        public static string AskSecret(string prompt)
        {
            Console.Write(prompt);
            if (Console.IsInputRedirected)
                return Console.ReadLine() ?? "";
            var sb = new StringBuilder();
            while (true)
            {
                var key = Console.ReadKey(intercept: true);
                if (key.Key == ConsoleKey.Enter) break;
                if (key.Key == ConsoleKey.Backspace) { if (sb.Length > 0) sb.Length--; continue; }
                if (!char.IsControl(key.KeyChar)) sb.Append(key.KeyChar);
            }
            Console.WriteLine();
            return sb.ToString();
        }

        private static void Write(ConsoleColor color, string text)
        {
            var old = Console.ForegroundColor;
            Console.ForegroundColor = color;
            Console.WriteLine(text);
            Console.ForegroundColor = old;
        }
    }
}
