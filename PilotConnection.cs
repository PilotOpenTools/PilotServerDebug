using Ascon.Pilot.Common;
using Ascon.Pilot.Common.DataProtection;
using Ascon.Pilot.DataClasses;
using Ascon.Pilot.DataModifier;
using Ascon.Pilot.Server.Api;
using Ascon.Pilot.Server.Api.Contracts;
using Ascon.Pilot.Transport;

namespace Debug;

/// <summary>
/// Подключение разработчика к базе как обычного клиента (то же, что PilotReportGateway.Session в админке).
/// Права - разработчика, а не сервера: объект, которого он не видит, модуль тоже не увидит.
/// </summary>
internal sealed class PilotConnection : IDisposable
{
    private static readonly TimeSpan ConfirmTimeout = TimeSpan.FromSeconds(30);

    private readonly HttpPilotClient _client;
    private readonly string _storageDir = Path.Combine(Path.GetTempPath(), "debug", Guid.NewGuid().ToString("N"));

    private PilotConnection(HttpPilotClient client, ChangeCallback callback, IServerApi api, IFileArchiveApi files, IMessagesApi? messages)
    {
        _client = client;
        Callback = callback;
        Api = api;
        Files = files;
        Directory.CreateDirectory(_storageDir);
        Storage = new FileSystemStorageProvider(_storageDir);
        Backend = new Backend(api, messages, new ChangesetUploader(files, Storage, null), null);
    }

    public IServerApi Api { get; }
    public IFileArchiveApi Files { get; }
    public Backend Backend { get; }
    public FileSystemStorageProvider Storage { get; }
    public ChangeCallback Callback { get; }

    public static PilotConnection Open(string databaseUrl, string login, string password, int licenseType)
    {
        var uri = new Uri(databaseUrl.TrimEnd('/'));
        var database = Uri.UnescapeDataString(uri.AbsolutePath.Trim('/'));
        if (database.Length == 0)
            throw new UsageException($"В адресе «{databaseUrl}» нет базы: нужно http://сервер:порт/база.");

        var credentials = ConnectionCredentials.GetConnectionCredentials(databaseUrl.TrimEnd('/'), login, password.ConvertToSecureString());
        var client = new HttpPilotClient(credentials.GetConnectionString(), credentials.GetConnectionProxy());
        try
        {
            client.Connect(false);
            var callback = new ChangeCallback();
            var api = client.GetServerApi(callback);
            var files = client.GetFileArchiveApi();
            IMessagesApi? messages = null;
            try { messages = client.GetMessagesApi(new NullMessageCallback()); } catch { /* сообщения не обязательны */ }
            client.GetAuthenticationApi().Login(database, credentials.Username, credentials.ProtectedPassword, false, licenseType);
            return new PilotConnection(client, callback, api, files, messages);
        }
        catch
        {
            try { client.Disconnect(); } catch { /* уже закрыто */ }
            try { client.Dispose(); } catch { /* уже закрыто */ }
            throw;
        }
    }

    public Modifier CreateModifier() => new(Backend, ChangesetDataSource.Native, MergeChangePolicy.Allow, false);

    /// <summary>Применение и ожидание подтверждения сервера (как ApplyAndConfirmAsync в админке). null - применено.</summary>
    public async Task<string?> ApplyAsync(Modifier modifier)
    {
        var confirmation = Callback.Expect();
        modifier.Apply(null);
        if (await Task.WhenAny(confirmation, Task.Delay(ConfirmTimeout)) != confirmation)
            return $"сервер не подтвердил изменение за {ConfirmTimeout.TotalSeconds} с - проверьте объект в клиенте";
        return await confirmation;
    }

    public byte[] Download(Guid bodyId)
    {
        const int chunkSize = 1024 * 1024;
        using var ms = new MemoryStream();
        long offset = 0;
        while (true)
        {
            var chunk = Files.GetFileChunk(bodyId, offset, chunkSize);
            if (chunk is null || chunk.Length == 0) break;
            ms.Write(chunk, 0, chunk.Length);
            offset += chunk.Length;
            if (chunk.Length < chunkSize) break;
        }
        return ms.ToArray();
    }

    public void Dispose()
    {
        try { _client.Disconnect(); } catch { /* уже закрыто */ }
        try { _client.Dispose(); } catch { /* уже закрыто */ }
        try { Directory.Delete(_storageDir, true); } catch { /* временные файлы */ }
    }

    private sealed class NullMessageCallback : IMessageCallback
    {
        public void NotifyMessageCreated(NotifiableDMessage message) { }
        public void NotifyTypingMessage(Guid chatId, int personId) { }
        public void NotifyOnline(int personId) { }
        public void NotifyOffline(int personId) { }
        public void CreateNotification(DNotification notification) { }
        public void UpdateLastMessageDate(DateTime maxDate) { }
    }

    /// <summary>Колбэк сервера: нужен только результат ChangeAsync.</summary>
    internal sealed class ChangeCallback : IServerCallback
    {
        private TaskCompletionSource<string?>? _pending;

        public Task<string?> Expect()
        {
            var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending = tcs;
            return tcs.Task;
        }

        public void NotifyChangeAsyncCompleted(DChangeset changeset) => _pending?.TrySetResult(null);

        public void NotifyChangeAsyncError(Guid identity, ProtoExceptionInfo exception)
        {
            string text;
            try { text = ProtoExceptionInfo.Deserialize(exception)?.Message ?? "неизвестная ошибка"; }
            catch { text = exception?.JsonData ?? "неизвестная ошибка"; }
            _pending?.TrySetResult(text);
        }

        public void NotifyChangeset(DChangeset changeset) { }
        public void NotifyOrganisationUnitChangeset(OrganisationUnitChangeset changeset) { }
        public void NotifyPersonChangeset(PersonChangeset changeset) { }
        public void NotifyDMetadataChangeset(DMetadataChangeset changeset) { }
        public void NotifySearchResult(DSearchResult searchResult) { }
        public void NotifyGeometrySearchResult(DGeometrySearchResult searchResult) { }
        public void NotifyDNotificationChangeset(DNotificationChangeset changeset) { }
        public void NotifyCommandResult(Guid requestId, byte[] data, ServerCommandResult result) { }
        public void NotifyCustomNotification(string name, byte[] data) { }
        public void NotifyAccessChangeset(Guid objectId) { }
        public void NotifySettingsChangeset(string settingKey) { }
    }
}
