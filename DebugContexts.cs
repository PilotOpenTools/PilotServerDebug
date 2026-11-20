using Ascon.Pilot.DataClasses;
using Ascon.Pilot.DataModifier;
using Ascon.Pilot.ServerExtensions.SDK;

namespace Debug;

/// <summary>IServerActivityContext как у ServerActivityAdapter в Pilot-Server: params и sourceTypes из сценария.</summary>
internal sealed class DebugActivityContext : IServerActivityContext
{
    private readonly Dictionary<Type, object> _services = new();

    public DebugActivityContext(Dictionary<string, object> @params, List<string> sourceTypes)
    {
        Params = @params;
        SourceTypes = sourceTypes;
    }

    public Dictionary<string, object> Params { get; }
    public List<string> SourceTypes { get; }
    public TargetType? Target => null;
    public RelationType? RelationType => null;
    public RelationFilterByChangeKind? RelationFilterByChangeKind => null;
    public Dictionary<string, string> Errors { get; } = new();

    public void Register<T>(T service) where T : class => _services[typeof(T)] = service;

    public T GetService<T>() where T : class
    {
        if (_services.TryGetValue(typeof(T), out var service))
            return (T)service;
        Out.Warn($"GetService<{typeof(T).Name}>(): в debug такого сервиса нет - вернул null (на сервере он может быть).");
        return null!;
    }
}

internal sealed class DebugEventContext : IAutomationEventContext
{
    public DebugEventContext(INObject source, INPerson person)
    {
        Source = source;
        InitiatingPerson = person;
    }

    public INObject Source { get; }

    /// <summary>Изменения, вызвавшие событие, в кейсе не воспроизводятся.</summary>
    public IEnumerable<INChange> Changes => Array.Empty<INChange>();

    public INPerson InitiatingPerson { get; }
    public Guid ChangesetIdentity { get; } = Guid.NewGuid();
    public DateTime EventDate { get; } = DateTime.UtcNow;
}

/// <summary>Уведомления не отправляются - только печатаются.</summary>
internal sealed class DebugNotifier : IAutomationNotifier
{
    public void Notify(int personId, INObject obj, string message, string notificationName) =>
        Out.Info($"[уведомление не отправлено] person={personId} obj={obj?.Id} «{notificationName}»: {message}");
}

/// <summary>Файлы читаются с сервера через файловый архив клиента.</summary>
internal sealed class DebugFileProvider(PilotConnection connection) : IAutomationFileProvider
{
    public bool Exists(Guid fileId)
    {
        try { return connection.Files.GetFileChunk(fileId, 0, 1)?.Length > 0; }
        catch { return false; }
    }

    // Интерфейс SDK 26.13 возвращает пару (bool, Guid); смысл второго значения не документирован - отдаём исходный id (проверить вживую).
    public Task<(bool, Guid)> ExistsAsync(Guid fileId) => Task.FromResult((Exists(fileId), fileId));

    public Task<Stream> OpenReadAsync(Guid fileId) =>
        Task.FromResult<Stream>(new MemoryStream(connection.Download(fileId), writable: false));
}
