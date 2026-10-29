using System.Text.Json.Nodes;
using SQLite;

namespace Tunapp.Core;

public sealed class CachedFireDocument
{
    [PrimaryKey] public string Path { get; set; } = "";
    public string Fields { get; set; } = "{}";
    public string? UpdateTime { get; set; }
    public FireDocument Document() => new(Path, JsonNode.Parse(Fields)!.AsObject(), UpdateTime);
    public static CachedFireDocument From(FireDocument value) => new() { Path = value.Path, Fields = value.Fields.ToJsonString(), UpdateTime = value.UpdateTime };
}
public sealed class PendingFireWrite
{
    [PrimaryKey, AutoIncrement] public long Sequence { get; set; }
    [Indexed(Unique = true)] public string OperationId { get; set; } = "";
    public string FarmId { get; set; } = "";
    [Indexed] public string Path { get; set; } = "";
    public string Fields { get; set; } = "{}";
    public string Kind { get; set; } = "";
    public bool Create { get; set; }
    public string? ExpectedUpdateTime { get; set; }
    public bool Attempted { get; set; }
    public string? Error { get; set; }
    public string? ErrorKind { get; set; }
    public FireDocument Document() => new(Path, JsonNode.Parse(Fields)!.AsObject(), ExpectedUpdateTime);
}
public sealed class CloudDatabase : IAsyncDisposable
{
    static CloudDatabase() => SQLitePCL.Batteries_V2.Init();
    private sealed class Meta { [PrimaryKey] public string Key { get; set; } = ""; public string Value { get; set; } = ""; }
    private readonly SQLiteAsyncConnection connection;
    private CloudDatabase(string path) => connection = new(path, SQLiteOpenFlags.ReadWrite | SQLiteOpenFlags.Create | SQLiteOpenFlags.FullMutex);
    public static async Task<CloudDatabase> OpenAsync(string path, string uid)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!);
        var db = new CloudDatabase(path);
        try
        {
            await db.connection.CreateTableAsync<Meta>();
            await db.connection.CreateTableAsync<CachedFireDocument>();
            await db.connection.CreateTableAsync<PendingFireWrite>();
            await db.connection.ExecuteScalarAsync<string>("PRAGMA journal_mode=WAL");
            await db.connection.ExecuteAsync("PRAGMA synchronous=FULL");
            var owner = await db.connection.FindAsync<Meta>("account");
            if (owner is not null && owner.Value != uid) throw new UnauthorizedAccessException("La base local pertenece a otra cuenta.");
            await db.connection.InsertOrReplaceAsync(new Meta { Key = "account", Value = uid });
            return db;
        }
        catch { await db.connection.CloseAsync(); throw; }
    }
    public Task<List<CachedFireDocument>> DocumentsAsync() => connection.Table<CachedFireDocument>().ToListAsync();
    public Task<List<PendingFireWrite>> PendingAsync() => connection.Table<PendingFireWrite>().OrderBy(x => x.Sequence).ToListAsync();
    public Task QueueAsync(PendingFireWrite write, IReadOnlyList<FireDocument>? extraDocuments = null) => connection.RunInTransactionAsync(tx =>
    {
        var existing = tx.Table<PendingFireWrite>().FirstOrDefault(x => x.Path == write.Path);
        if (existing is not null)
        {
            if (existing.Attempted) throw new InvalidOperationException("Hay un envío pendiente de comprobar para este registro. Sincroniza o revisa el conflicto antes de editarlo.");
            existing.Fields = write.Fields; existing.Error = null; existing.ErrorKind = null;
            tx.Update(existing); // Conservar la identidad y precondición de la creación/edición original.
        }
        else tx.Insert(write);
        tx.InsertOrReplace(CachedFireDocument.From(write.Document()));
        if (extraDocuments is not null) foreach (var document in extraDocuments) tx.InsertOrReplace(CachedFireDocument.From(document));
    });
    public Task MarkAttemptedAsync(long sequence) => connection.ExecuteAsync("UPDATE PendingFireWrite SET Attempted = 1 WHERE Sequence = ?", sequence);
    public Task BlockAsync(long sequence, string kind, string error) => connection.ExecuteAsync("UPDATE PendingFireWrite SET ErrorKind = ?, Error = ? WHERE Sequence = ?", kind, error, sequence);
    public Task RetryAsync(long sequence) => connection.ExecuteAsync("UPDATE PendingFireWrite SET ErrorKind = NULL, Error = NULL WHERE Sequence = ?", sequence);
    public Task AcknowledgeAsync(PendingFireWrite write, FireDocument remote) => connection.RunInTransactionAsync(tx =>
    {
        tx.Delete<PendingFireWrite>(write.Sequence);
        tx.InsertOrReplace(CachedFireDocument.From(remote));
    });
    public Task MergeAsync(IEnumerable<FireDocument> documents, string prefix, bool deleteMissing = true) => connection.RunInTransactionAsync(tx =>
    {
        var remote = documents.ToDictionary(x => x.Path);
        var pending = tx.Table<PendingFireWrite>().ToList();
        bool Protected(string path) => pending.Any(x => x.Path == path || (x.Kind == "farm.create" && (path == $"farms/{x.FarmId}" || path.StartsWith($"farms/{x.FarmId}/") || path.EndsWith($"/farms/{x.FarmId}"))));
        foreach (var document in remote.Values)
            if (!Protected(document.Path)) tx.InsertOrReplace(CachedFireDocument.From(document));
        if (deleteMissing)
            foreach (var cached in tx.Table<CachedFireDocument>().ToList().Where(x => x.Path.StartsWith(prefix, StringComparison.Ordinal)))
                if (!remote.ContainsKey(cached.Path) && !Protected(cached.Path)) tx.Delete(cached);
    });
    public Task RevokeAsync(string farmId, string uid) => connection.RunInTransactionAsync(tx =>
    {
        foreach (var document in tx.Table<CachedFireDocument>().ToList().Where(x => x.Path == $"farms/{farmId}" || x.Path.StartsWith($"farms/{farmId}/") || x.Path == $"users/{uid}/farms/{farmId}")) tx.Delete(document);
        foreach (var pending in tx.Table<PendingFireWrite>().Where(x => x.FarmId == farmId).ToList())
        { pending.ErrorKind = "permission"; pending.Error = "No se pudo verificar el acceso a esta explotación. Los cambios se conservan pendientes."; tx.Update(pending); }
    });
    public Task ResolveAsync(PendingFireWrite pending, FireDocument? remote, bool keepLocal) => connection.RunInTransactionAsync(tx =>
    {
        if (!keepLocal)
        {
            tx.Delete<PendingFireWrite>(pending.Sequence);
            if (remote is null) tx.Delete<CachedFireDocument>(pending.Path);
            else tx.InsertOrReplace(CachedFireDocument.From(remote));
            return;
        }
        // Solo conflictos confirmados; una petición ambigua conserva su identificador para buscar el recibo.
        if (pending.ErrorKind != "conflict" || remote is null) throw new InvalidOperationException("Actualiza antes de resolver este envío.");
        var fields = pending.Document().Fields;
        fields["createdAt"] = remote.Fields["createdAt"]?.DeepClone();
        fields["createdBy"] = remote.Fields["createdBy"]?.DeepClone();
        pending.OperationId = Guid.NewGuid().ToString("N"); pending.Fields = fields.ToJsonString();
        pending.ExpectedUpdateTime = remote.UpdateTime; pending.Create = false; pending.Attempted = false; pending.Error = null; pending.ErrorKind = null;
        tx.Update(pending); tx.InsertOrReplace(CachedFireDocument.From(pending.Document()));
    });
    public async ValueTask DisposeAsync() => await connection.CloseAsync();
}
