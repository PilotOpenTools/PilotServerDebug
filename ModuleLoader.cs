using System.ComponentModel.Composition.Hosting;
using System.Reflection;
using System.Runtime.Loader;
using Ascon.Pilot.ServerExtensions.SDK;

namespace Debug;

/// <summary>
/// Загрузка модуля по пути с диска (а не из памяти, как в ServerHost) - чтобы отладчик сопоставил .pdb
/// и точки останова в коде модуля срабатывали. Ascon.Pilot.* берутся у debug (как у сервера - у сервера),
/// собственные зависимости модуля - из его папки.
/// </summary>
internal static class ModuleLoader
{
    /// <summary>
    /// Путь к модулю: как задан, а если такого файла нет - самый свежий *.servext.dll с тем же именем
    /// (или любой, если имя не задано) в bin\Debug, bin\Release или bin папки кейса (обычно это папка проекта модуля).
    /// </summary>
    public static string Resolve(string? module, string baseDir)
    {
        if (!string.IsNullOrWhiteSpace(module))
        {
            var direct = Path.IsPathRooted(module) ? module : Path.Combine(baseDir, module);
            if (File.Exists(direct))
            {
                Out.Info($"Модуль: {Path.GetFullPath(direct)}");
                return Path.GetFullPath(direct);
            }
        }

        var pattern = string.IsNullOrWhiteSpace(module) ? "*.servext.dll" : Path.GetFileName(module);
        var bin = Path.Combine(baseDir, "bin");
        var found = Directory.Exists(bin)
            ? new DirectoryInfo(bin).EnumerateFiles(pattern, SearchOption.AllDirectories)
                .Where(f => !f.FullName.Contains(Path.DirectorySeparatorChar + "ref" + Path.DirectorySeparatorChar))
                .OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault()
            : null;
        if (found is not null)
        {
            Out.Info($"Модуль: {found.FullName} (собран {found.LastWriteTime:dd.MM HH:mm:ss})");
            return found.FullName;
        }
        throw new UsageException(string.IsNullOrWhiteSpace(module)
            ? $"Не задан модуль и в {bin} нет *.servext.dll: соберите проект модуля или укажите --module."
            : $"Не найден модуль {module} (искал и в {bin}). Соберите проект модуля или укажите --module.");
    }

    public static IServerActivity Load(string dllPath, string? activityName)
    {
        var full = Path.GetFullPath(dllPath);
        if (!File.Exists(full))
            throw new UsageException($"Не найден модуль {full}. Соберите проект модуля или укажите --module.");

        var dir = Path.GetDirectoryName(full)!;
        AssemblyLoadContext.Default.Resolving += (ctx, name) =>
        {
            var candidate = Path.Combine(dir, name.Name + ".dll");
            return File.Exists(candidate) ? ctx.LoadFromAssemblyPath(candidate) : null;
        };

        var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(full);
        var activities = Discover(assembly);
        if (activities.Count == 0)
            throw new UsageException($"В {Path.GetFileName(full)} нет классов IServerActivity с [Export(typeof(IServerActivity))].");

        Out.Info("Активности модуля: " + string.Join(", ", activities.Select(a => a.Name)));
        if (string.IsNullOrWhiteSpace(activityName))
        {
            if (activities.Count > 1)
                throw new UsageException("В модуле несколько активностей - укажите нужную: --activity <имя> или \"activity\" в кейсе.");
            return activities[0];
        }
        return activities.FirstOrDefault(a => a.Name == activityName)
               ?? throw new UsageException($"Активности «{activityName}» в модуле нет.");
    }

    /// <summary>Как Pilot: MEF-экспорты IServerActivity. Если MEF не справился (импорты сервера) - конструктор без параметров.</summary>
    private static List<IServerActivity> Discover(Assembly assembly)
    {
        try
        {
            using var catalog = new AssemblyCatalog(assembly);
            using var container = new CompositionContainer(catalog);
            return container.GetExportedValues<IServerActivity>().ToList();
        }
        catch (Exception ex)
        {
            Out.Warn($"MEF не собрал активности ({ex.GetBaseException().Message}) - создаю их напрямую.");
        }

        Type[] types;
        try { types = assembly.GetTypes(); }
        catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t is not null).ToArray()!; }

        return types
            .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(IServerActivity).IsAssignableFrom(t) && t.GetConstructor(Type.EmptyTypes) is not null)
            .Select(t => (IServerActivity)Activator.CreateInstance(t)!)
            .ToList();
    }
}
