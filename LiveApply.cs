using System.Reflection;
using System.Runtime.ExceptionServices;
using Ascon.Pilot.DataModifier;

namespace Debug;

/// <summary>
/// Режим apply: изменения модуля уходят на сервер сразу, как только код пошёл дальше, - перед следующим
/// обращением модуля к modifier/backend и в конце RunAsync. Так цепочка изменений, где следующий шаг читает
/// результат предыдущего (версии файлов, атрибуты), видит на сервере уже записанное. Подтверждения не спрашивает.
/// </summary>
internal sealed class LiveApplier
{
    private readonly PilotConnection _connection;
    private readonly Modifier _modifier;
    private bool _flushing;

    public LiveApplier(PilotConnection connection, Modifier modifier)
    {
        _connection = connection;
        _modifier = modifier;
        Backend = FlushingProxy<IModifierBackend>.Create(connection.Backend, this);
        Modifier = FlushingProxy<IModifierBase>.Create(modifier, this);
    }

    /// <summary>Отдаётся модулю вместо настоящих modifier/backend.</summary>
    public IModifierBase Modifier { get; }
    public IModifierBackend Backend { get; }

    public int Applied { get; private set; }
    public int Failed { get; private set; }

    public void Flush()
    {
        if (_flushing || !_modifier.AnyChanges())
            return;
        _flushing = true;
        try
        {
            var count = ChangeReport.Print(_modifier, _connection.Backend);
            string? error;
            try
            {
                error = _connection.ApplyAsync(_modifier).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }
            if (error is null)
            {
                Applied += count;
                Out.Info($"/ записано на сервер ({count}).");
            }
            else
            {
                Failed++;
                Out.Error("Сервер не принял изменения: " + error);
            }
            if (_modifier.AnyChanges())
                _modifier.ClearChanges();
        }
        finally
        {
            _flushing = false;
        }
    }

    /// <summary>Перед любым вызовом, кроме служебных, записывает накопленные изменения.</summary>
    public class FlushingProxy<T> : DispatchProxy where T : class
    {
        private static readonly HashSet<string> Passive = new() { "AnyChanges", "ClearChanges", "get_Changes", "get_Backend" };

        private T _target = null!;
        private LiveApplier _applier = null!;

        public static T Create(T target, LiveApplier applier)
        {
            var proxy = Create<T, FlushingProxy<T>>();
            var self = (FlushingProxy<T>)(object)proxy;
            self._target = target;
            self._applier = applier;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (!Passive.Contains(method!.Name))
                _applier.Flush();
            if (method.Name == "get_Backend")
                return _applier.Backend;
            try
            {
                return method.Invoke(_target, args);
            }
            catch (TargetInvocationException ex) when (ex.InnerException is not null)
            {
                ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
                throw;
            }
        }
    }
}
