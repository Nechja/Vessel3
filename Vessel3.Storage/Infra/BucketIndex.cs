using System.Diagnostics;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Vessel3.Storage;

internal enum VersionKind { Put = 0, DeleteMarker = 1 }
internal readonly record struct ObjectLockStatus(Retention? Retention, bool LegalHoldOn);

internal sealed partial class BucketIndex(string dbPath) : IBucketIndex
{
    private SqliteReaderPool readers = new($"Data Source={dbPath};Mode=ReadOnly;Pooling=False");
    private SqliteConnection? writeConn;
    private SqliteTransaction? currentTx;
    private BucketIndexTxScope? currentTxScope;

    internal HotIndexCache HotCache { get; } = new();

    public void Open()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        writeConn = new SqliteConnection($"Data Source={dbPath};Mode=ReadWriteCreate;Pooling=False");
        writeConn.Open();
        BucketIndexSchema.Initialize(writeConn);
    }

    public void ReplaceFromSnapshot(string snapshotFilePath)
    {
        HotCache.Clear();
        var oldReaders = Interlocked.Exchange(ref readers, new SqliteReaderPool($"Data Source={dbPath};Mode=ReadOnly;Pooling=False"));
        oldReaders.Dispose();
        writeConn?.Dispose();
        writeConn = null;

        if (File.Exists(dbPath + "-wal")) File.Delete(dbPath + "-wal");
        if (File.Exists(dbPath + "-shm")) File.Delete(dbPath + "-shm");
        File.Copy(snapshotFilePath, dbPath, overwrite: true);

        Open();
        HotCache.Clear();
    }

    public void Dispose()
    {
        HotCache.Clear();
        readers.Dispose();
        writeConn?.Dispose();
        writeConn = null;
    }

    public BucketIndexTxScope BeginTransaction()
    {
        var tx = writeConn!.BeginTransaction();
        currentTx = tx;
        var scope = new BucketIndexTxScope(this, tx);
        currentTxScope = scope;
        return scope;
    }

    internal void ClearTransaction(BucketIndexTxScope scope)
    {
        if (ReferenceEquals(currentTxScope, scope))
        {
            currentTxScope = null;
            currentTx = null;
        }
    }

    private void ApplyCacheAction(Action action)
    {
        if (currentTxScope is not null)
        {
            currentTxScope.AddPostCommitAction(action);
        }
        else
        {
            action();
        }
    }

    private SqliteCommand WriteCmd()
    {
        var c = writeConn!.CreateCommand();
        if (currentTx is not null) c.Transaction = currentTx;
        return c;
    }

    internal ReadHandle ReadCmd()
    {
        var start = Stopwatch.GetTimestamp();
        var conn = readers.Rent();
        RequestTrace.Since(Stage.ReadLock, start);
        return new ReadHandle(readers, conn.CreateCommand(), conn);
    }

    private static string SerializeMetadata(IReadOnlyDictionary<string, string> metadata) =>
        metadata.Count is 0 ? "{}"
            : JsonSerializer.Serialize(
                new Dictionary<string, string>(metadata),
                VersionEventContext.Default.DictionaryStringString);

    private static IReadOnlyDictionary<string, string> DeserializeMetadata(string json) =>
        string.IsNullOrEmpty(json) || json is "{}"
            ? []
            : JsonSerializer.Deserialize(json, VersionEventContext.Default.DictionaryStringString)
                ?? [];

    private static string SerializeParts(IReadOnlyList<MultipartPart>? parts) =>
        parts is null || parts.Count is 0 ? ""
            : JsonSerializer.Serialize(
                [.. parts],
                VersionEventContext.Default.ListMultipartPart);

    private static IReadOnlyList<MultipartPart>? DeserializeParts(string json) =>
        string.IsNullOrEmpty(json)
            ? null
            : JsonSerializer.Deserialize(json, VersionEventContext.Default.ListMultipartPart);
}
