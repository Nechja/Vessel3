using System.Globalization;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Vessel3.Primitives;
using Vessel3.Storage;

namespace Vessel3.Protocols.Oci;

public sealed record ContainerRepoCatalogOptions(string Root);

internal sealed class SqliteContainerRepoCatalog : IContainerRepoCatalog
{
    private readonly string dbPath;
    private readonly string uploadsDir;
    private readonly TimeProvider clock;
    private readonly Lock gate = new();
    private SqliteConnection? conn;
    private bool disposed;

    public SqliteContainerRepoCatalog(ContainerRepoCatalogOptions options, TimeProvider? clock = null)
    {
        this.clock = clock ?? TimeProvider.System;
        dbPath = Path.Combine(options.Root, "catalog.db");
        uploadsDir = Path.Combine(options.Root, "uploads");
        InitializeDatabase();
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            conn?.Dispose();
            conn = null;
        }
    }

    public Result<ContainerRepo> GetOrCreateRepo(string repoName, string? ownerId = null)
    {
        var cleanName = NormalizeRepoName(repoName);
        if (string.IsNullOrWhiteSpace(cleanName))
            return new InvalidPathError("Repository name cannot be empty");

        lock (gate)
        {
            EnsureOpen();
            using var selectCmd = conn!.CreateCommand();
            selectCmd.CommandText = "SELECT id, name, created_at, owner_id FROM repos WHERE name = @name;";
            selectCmd.Parameters.AddWithValue("@name", cleanName);

            using (var reader = selectCmd.ExecuteReader())
            {
                if (reader.Read())
                {
                    return new ContainerRepo(
                        reader.GetString(1),
                        DateTimeOffset.Parse(reader.GetString(2), CultureInfo.InvariantCulture),
                        reader.IsDBNull(3) ? null : reader.GetString(3));
                }
            }

            var id = "repo_" + Ulid.NewUlid().ToString();
            var now = clock.GetUtcNow();

            using var insertCmd = conn.CreateCommand();
            insertCmd.CommandText = """
                INSERT INTO repos (id, name, created_at, owner_id)
                VALUES (@id, @name, @created, @owner);
                """;
            insertCmd.Parameters.AddWithValue("@id", id);
            insertCmd.Parameters.AddWithValue("@name", cleanName);
            insertCmd.Parameters.AddWithValue("@created", now.ToString("O", CultureInfo.InvariantCulture));
            insertCmd.Parameters.AddWithValue("@owner", (object?)ownerId ?? DBNull.Value);
            insertCmd.ExecuteNonQuery();

            return new ContainerRepo(cleanName, now, ownerId);
        }
    }

    public Result<ContainerRepo?> GetRepo(string repoName)
    {
        var cleanName = NormalizeRepoName(repoName);
        lock (gate)
        {
            EnsureOpen();
            using var cmd = conn!.CreateCommand();
            cmd.CommandText = "SELECT name, created_at, owner_id FROM repos WHERE name = @name;";
            cmd.Parameters.AddWithValue("@name", cleanName);

            using var reader = cmd.ExecuteReader();
            return !reader.Read()
                ? new Result<ContainerRepo?>.Success(null)
                : new Result<ContainerRepo?>.Success(new ContainerRepo(
                    reader.GetString(0),
                    DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture),
                    reader.IsDBNull(2) ? null : reader.GetString(2)));
        }
    }

    public Result<IReadOnlyList<string>> ListRepos(int limit = 100, string? last = null)
    {
        var clampedLimit = Math.Clamp(limit, 1, 1000);
        lock (gate)
        {
            EnsureOpen();
            using var cmd = conn!.CreateCommand();
            if (string.IsNullOrEmpty(last))
            {
                cmd.CommandText = "SELECT name FROM repos ORDER BY name ASC LIMIT @limit;";
            }
            else
            {
                cmd.CommandText = "SELECT name FROM repos WHERE name > @last ORDER BY name ASC LIMIT @limit;";
                cmd.Parameters.AddWithValue("@last", last);
            }
            cmd.Parameters.AddWithValue("@limit", clampedLimit);

            var list = new List<string>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                list.Add(reader.GetString(0));
            }
            return list;
        }
    }

    public Result<IReadOnlyList<string>> ListTags(string repoName, int limit = 100, string? last = null)
    {
        var cleanName = NormalizeRepoName(repoName);
        var clampedLimit = Math.Clamp(limit, 1, 1000);

        lock (gate)
        {
            EnsureOpen();
            var repoId = GetRepoId(cleanName);
            if (repoId is null)
                return new NoSuchContainerRepoError(cleanName);

            using var cmd = conn!.CreateCommand();
            if (string.IsNullOrEmpty(last))
            {
                cmd.CommandText = "SELECT name FROM tags WHERE repo_id = @repoId ORDER BY name ASC LIMIT @limit;";
            }
            else
            {
                cmd.CommandText = "SELECT name FROM tags WHERE repo_id = @repoId AND name > @last ORDER BY name ASC LIMIT @limit;";
                cmd.Parameters.AddWithValue("@last", last);
            }
            cmd.Parameters.AddWithValue("@repoId", repoId);
            cmd.Parameters.AddWithValue("@limit", clampedLimit);

            var list = new List<string>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                list.Add(reader.GetString(0));
            }
            return list;
        }
    }

    public Result<ContainerManifest> GetManifest(string repoName, string reference)
    {
        var cleanName = NormalizeRepoName(repoName);
        lock (gate)
        {
            EnsureOpen();
            var repoId = GetRepoId(cleanName);
            if (repoId is null)
                return new NoSuchContainerRepoError(cleanName);

            string targetDigest;
            if (reference.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
            {
                targetDigest = reference.ToLowerInvariant();
            }
            else
            {
                using var tagCmd = conn!.CreateCommand();
                tagCmd.CommandText = "SELECT manifest_digest FROM tags WHERE repo_id = @repoId AND name = @name;";
                tagCmd.Parameters.AddWithValue("@repoId", repoId);
                tagCmd.Parameters.AddWithValue("@name", reference);
                var obj = tagCmd.ExecuteScalar();
                if (obj is not string tagDigest)
                    return new NoSuchManifestError(cleanName, reference);
                targetDigest = tagDigest;
            }

            using var manifestCmd = conn!.CreateCommand();
            manifestCmd.CommandText = "SELECT digest, media_type, size, content, created_at FROM manifests WHERE repo_id = @repoId AND digest = @digest;";
            manifestCmd.Parameters.AddWithValue("@repoId", repoId);
            manifestCmd.Parameters.AddWithValue("@digest", targetDigest);

            using var reader = manifestCmd.ExecuteReader();
            if (!reader.Read())
                return new NoSuchManifestError(cleanName, reference);

            var digest = reader.GetString(0);
            var mediaType = reader.GetString(1);
            var size = reader.GetInt64(2);
            var content = (byte[])reader[3];
            var createdAt = DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture);

            return new ContainerManifest(digest, mediaType, size, content, createdAt);
        }
    }

    public Result<PutManifestOutcome> PutManifest(string repoName, string reference, string mediaType, byte[] payload, IReadOnlyList<string> layerDigests)
    {
        var cleanName = NormalizeRepoName(repoName);
        var shaHex = Convert.ToHexStringLower(SHA256.HashData(payload));
        var manifestDigest = $"sha256:{shaHex}";

        lock (gate)
        {
            EnsureOpen();
            var repoRes = GetOrCreateRepo(cleanName);
            if (!repoRes.TryGetValue(out _, out var repoErr)) return repoErr;

            var repoId = GetRepoId(cleanName)!;
            var now = clock.GetUtcNow();

            using var tx = conn!.BeginTransaction();

            using var putManifestCmd = conn.CreateCommand();
            putManifestCmd.Transaction = tx;
            putManifestCmd.CommandText = """
                INSERT INTO manifests (repo_id, digest, media_type, size, content, created_at)
                VALUES (@repoId, @digest, @mediaType, @size, @content, @created)
                ON CONFLICT(repo_id, digest) DO UPDATE SET
                    media_type = excluded.media_type,
                    size = excluded.size,
                    content = excluded.content;
                """;
            putManifestCmd.Parameters.AddWithValue("@repoId", repoId);
            putManifestCmd.Parameters.AddWithValue("@digest", manifestDigest);
            putManifestCmd.Parameters.AddWithValue("@mediaType", mediaType);
            putManifestCmd.Parameters.AddWithValue("@size", (long)payload.Length);
            putManifestCmd.Parameters.AddWithValue("@content", payload);
            putManifestCmd.Parameters.AddWithValue("@created", now.ToString("O", CultureInfo.InvariantCulture));
            putManifestCmd.ExecuteNonQuery();

            using var selfBlobCmd = conn.CreateCommand();
            selfBlobCmd.Transaction = tx;
            selfBlobCmd.CommandText = """
                INSERT OR IGNORE INTO manifest_blobs (repo_id, manifest_digest, blob_sha)
                VALUES (@repoId, @manifestDigest, @blobSha);
                """;
            selfBlobCmd.Parameters.AddWithValue("@repoId", repoId);
            selfBlobCmd.Parameters.AddWithValue("@manifestDigest", manifestDigest);
            selfBlobCmd.Parameters.AddWithValue("@blobSha", shaHex);
            selfBlobCmd.ExecuteNonQuery();

            foreach (var layer in layerDigests)
            {
                var cleanLayerSha = layer.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
                    ? layer[7..].ToLowerInvariant()
                    : layer.ToLowerInvariant();

                using var linkCmd = conn.CreateCommand();
                linkCmd.Transaction = tx;
                linkCmd.CommandText = """
                    INSERT OR IGNORE INTO manifest_blobs (repo_id, manifest_digest, blob_sha)
                    VALUES (@repoId, @manifestDigest, @blobSha);
                    """;
                linkCmd.Parameters.AddWithValue("@repoId", repoId);
                linkCmd.Parameters.AddWithValue("@manifestDigest", manifestDigest);
                linkCmd.Parameters.AddWithValue("@blobSha", cleanLayerSha);
                linkCmd.ExecuteNonQuery();
            }

            if (!reference.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
            {
                using var tagCmd = conn.CreateCommand();
                tagCmd.Transaction = tx;
                tagCmd.CommandText = """
                    INSERT INTO tags (repo_id, name, manifest_digest, updated_at)
                    VALUES (@repoId, @name, @digest, @updated)
                    ON CONFLICT(repo_id, name) DO UPDATE SET
                        manifest_digest = excluded.manifest_digest,
                        updated_at = excluded.updated_at;
                    """;
                tagCmd.Parameters.AddWithValue("@repoId", repoId);
                tagCmd.Parameters.AddWithValue("@name", reference);
                tagCmd.Parameters.AddWithValue("@digest", manifestDigest);
                tagCmd.Parameters.AddWithValue("@updated", now.ToString("O", CultureInfo.InvariantCulture));
                tagCmd.ExecuteNonQuery();
            }

            tx.Commit();
            return new PutManifestOutcome(manifestDigest, Created: true, layerDigests);
        }
    }

    public Result<bool> DeleteManifest(string repoName, string reference)
    {
        var cleanName = NormalizeRepoName(repoName);
        lock (gate)
        {
            EnsureOpen();
            var repoId = GetRepoId(cleanName);
            if (repoId is null)
                return new NoSuchContainerRepoError(cleanName);

            if (!reference.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
            {
                return DeleteTag(cleanName, reference);
            }

            using var tx = conn!.BeginTransaction();

            using var delTags = conn.CreateCommand();
            delTags.Transaction = tx;
            delTags.CommandText = "DELETE FROM tags WHERE repo_id = @repoId AND manifest_digest = @digest;";
            delTags.Parameters.AddWithValue("@repoId", repoId);
            delTags.Parameters.AddWithValue("@digest", reference.ToLowerInvariant());
            delTags.ExecuteNonQuery();

            using var delBlobs = conn.CreateCommand();
            delBlobs.Transaction = tx;
            delBlobs.CommandText = "DELETE FROM manifest_blobs WHERE repo_id = @repoId AND manifest_digest = @digest;";
            delBlobs.Parameters.AddWithValue("@repoId", repoId);
            delBlobs.Parameters.AddWithValue("@digest", reference.ToLowerInvariant());
            delBlobs.ExecuteNonQuery();

            using var delManifest = conn.CreateCommand();
            delManifest.Transaction = tx;
            delManifest.CommandText = "DELETE FROM manifests WHERE repo_id = @repoId AND digest = @digest;";
            delManifest.Parameters.AddWithValue("@repoId", repoId);
            delManifest.Parameters.AddWithValue("@digest", reference.ToLowerInvariant());
            var affected = delManifest.ExecuteNonQuery();

            tx.Commit();
            return affected > 0;
        }
    }

    public Result<bool> DeleteTag(string repoName, string tag)
    {
        var cleanName = NormalizeRepoName(repoName);
        lock (gate)
        {
            EnsureOpen();
            var repoId = GetRepoId(cleanName);
            if (repoId is null)
                return new NoSuchContainerRepoError(cleanName);

            using var cmd = conn!.CreateCommand();
            cmd.CommandText = "DELETE FROM tags WHERE repo_id = @repoId AND name = @name;";
            cmd.Parameters.AddWithValue("@repoId", repoId);
            cmd.Parameters.AddWithValue("@name", tag);
            var affected = cmd.ExecuteNonQuery();
            return affected > 0;
        }
    }

    public Result<ContainerUploadSession> StartUploadSession(string repoName)
    {
        var cleanName = NormalizeRepoName(repoName);
        lock (gate)
        {
            EnsureOpen();
            var repoRes = GetOrCreateRepo(cleanName);
            if (!repoRes.TryGetValue(out _, out var err)) return err;

            var repoId = GetRepoId(cleanName)!;
            var uploadId = Ulid.NewUlid().ToString();
            Directory.CreateDirectory(uploadsDir);
            var tempFilePath = Path.Combine(uploadsDir, $"{uploadId}.tmp");

            using (var fs = File.Create(tempFilePath)) { }

            var now = clock.GetUtcNow();
            var expiresAt = now.AddHours(24);

            using var cmd = conn!.CreateCommand();
            cmd.CommandText = """
                INSERT INTO upload_sessions (id, repo_id, temp_path, bytes_received, created_at, expires_at)
                VALUES (@id, @repoId, @tempPath, 0, @created, @expires);
                """;
            cmd.Parameters.AddWithValue("@id", uploadId);
            cmd.Parameters.AddWithValue("@repoId", repoId);
            cmd.Parameters.AddWithValue("@tempPath", tempFilePath);
            cmd.Parameters.AddWithValue("@created", now.ToString("O", CultureInfo.InvariantCulture));
            cmd.Parameters.AddWithValue("@expires", expiresAt.ToString("O", CultureInfo.InvariantCulture));
            cmd.ExecuteNonQuery();

            return new ContainerUploadSession(uploadId, cleanName, tempFilePath, 0, now, expiresAt);
        }
    }

    public Result<ContainerUploadSession> GetUploadSession(string uploadId)
    {
        lock (gate)
        {
            EnsureOpen();
            using var cmd = conn!.CreateCommand();
            cmd.CommandText = """
                SELECT u.id, r.name, u.temp_path, u.bytes_received, u.created_at, u.expires_at
                FROM upload_sessions u
                JOIN repos r ON r.id = u.repo_id
                WHERE u.id = @id;
                """;
            cmd.Parameters.AddWithValue("@id", uploadId);

            using var reader = cmd.ExecuteReader();
            return !reader.Read()
                ? new BlobUploadUnknownError(uploadId)
                : new ContainerUploadSession(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetInt64(3),
                    DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture),
                    DateTimeOffset.Parse(reader.GetString(5), CultureInfo.InvariantCulture));
        }
    }

    public Result UpdateUploadSession(string uploadId, long bytesReceived)
    {
        lock (gate)
        {
            EnsureOpen();
            using var cmd = conn!.CreateCommand();
            cmd.CommandText = "UPDATE upload_sessions SET bytes_received = @bytes WHERE id = @id;";
            cmd.Parameters.AddWithValue("@bytes", bytesReceived);
            cmd.Parameters.AddWithValue("@id", uploadId);
            var rows = cmd.ExecuteNonQuery();
            return rows > 0 ? Result.Ok : new BlobUploadUnknownError(uploadId);
        }
    }

    public Result CompleteUploadSession(string uploadId)
    {
        lock (gate)
        {
            EnsureOpen();
            using var cmd = conn!.CreateCommand();
            cmd.CommandText = "DELETE FROM upload_sessions WHERE id = @id;";
            cmd.Parameters.AddWithValue("@id", uploadId);
            cmd.ExecuteNonQuery();
            return Result.Ok;
        }
    }

    public Result CancelUploadSession(string uploadId)
    {
        lock (gate)
        {
            EnsureOpen();
            using var cmd = conn!.CreateCommand();
            cmd.CommandText = "SELECT temp_path FROM upload_sessions WHERE id = @id;";
            cmd.Parameters.AddWithValue("@id", uploadId);
            var path = cmd.ExecuteScalar() as string;

            using var delCmd = conn.CreateCommand();
            delCmd.CommandText = "DELETE FROM upload_sessions WHERE id = @id;";
            delCmd.Parameters.AddWithValue("@id", uploadId);
            delCmd.ExecuteNonQuery();

            if (path is not null && File.Exists(path))
            {
                try { File.Delete(path); } catch { }
            }
            return Result.Ok;
        }
    }

    public int ReapExpiredUploadSessions(DateTimeOffset cutoff)
    {
        lock (gate)
        {
            EnsureOpen();
            using var selectCmd = conn!.CreateCommand();
            selectCmd.CommandText = "SELECT id, temp_path FROM upload_sessions WHERE expires_at <= @cutoff;";
            selectCmd.Parameters.AddWithValue("@cutoff", cutoff.ToString("O", CultureInfo.InvariantCulture));

            var toDelete = new List<(string Id, string Path)>();
            using (var reader = selectCmd.ExecuteReader())
            {
                while (reader.Read())
                {
                    toDelete.Add((reader.GetString(0), reader.GetString(1)));
                }
            }

            foreach (var item in toDelete)
            {
                using var delCmd = conn.CreateCommand();
                delCmd.CommandText = "DELETE FROM upload_sessions WHERE id = @id;";
                delCmd.Parameters.AddWithValue("@id", item.Id);
                delCmd.ExecuteNonQuery();

                if (File.Exists(item.Path))
                {
                    try { File.Delete(item.Path); } catch { }
                }
            }

            return toDelete.Count;
        }
    }

    public IEnumerable<string> AllReferencedBlobs()
    {
        lock (gate)
        {
            EnsureOpen();
            using var cmd = conn!.CreateCommand();
            cmd.CommandText = "SELECT DISTINCT blob_sha FROM manifest_blobs;";

            var list = new List<string>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                list.Add(reader.GetString(0));
            }
            return list;
        }
    }

    public IEnumerable<string> EnumerateInFlightShas() => [];

    private string? GetRepoId(string cleanName)
    {
        using var cmd = conn!.CreateCommand();
        cmd.CommandText = "SELECT id FROM repos WHERE name = @name;";
        cmd.Parameters.AddWithValue("@name", cleanName);
        return cmd.ExecuteScalar() as string;
    }

    private static string NormalizeRepoName(string repoName) =>
        repoName.Trim('/').ToLowerInvariant();

    private void EnsureOpen()
    {
        if (conn is null)
        {
            conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadWriteCreate;Pooling=False");
            conn.Open();
        }
    }

    private void InitializeDatabase()
    {
        var dir = Path.GetDirectoryName(dbPath)!;
        Directory.CreateDirectory(dir);
        Directory.CreateDirectory(uploadsDir);

        EnsureOpen();

        using var pragma = conn!.CreateCommand();
        pragma.CommandText = """
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous = NORMAL;
            PRAGMA foreign_keys = ON;
            """;
        pragma.ExecuteNonQuery();

        using var schema = conn.CreateCommand();
        schema.CommandText = """
            CREATE TABLE IF NOT EXISTS repos (
                id TEXT PRIMARY KEY,
                name TEXT NOT NULL UNIQUE,
                created_at TEXT NOT NULL,
                owner_id TEXT
            );

            CREATE TABLE IF NOT EXISTS manifests (
                repo_id TEXT NOT NULL,
                digest TEXT NOT NULL,
                media_type TEXT NOT NULL,
                size INTEGER NOT NULL,
                content BLOB NOT NULL,
                created_at TEXT NOT NULL,
                PRIMARY KEY(repo_id, digest),
                FOREIGN KEY(repo_id) REFERENCES repos(id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS tags (
                repo_id TEXT NOT NULL,
                name TEXT NOT NULL,
                manifest_digest TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                PRIMARY KEY(repo_id, name),
                FOREIGN KEY(repo_id) REFERENCES repos(id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS manifest_blobs (
                repo_id TEXT NOT NULL,
                manifest_digest TEXT NOT NULL,
                blob_sha TEXT NOT NULL,
                PRIMARY KEY(repo_id, manifest_digest, blob_sha),
                FOREIGN KEY(repo_id) REFERENCES repos(id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS upload_sessions (
                id TEXT PRIMARY KEY,
                repo_id TEXT NOT NULL,
                temp_path TEXT NOT NULL,
                bytes_received INTEGER NOT NULL,
                created_at TEXT NOT NULL,
                expires_at TEXT NOT NULL,
                FOREIGN KEY(repo_id) REFERENCES repos(id) ON DELETE CASCADE
            );

            CREATE INDEX IF NOT EXISTS idx_manifest_blobs_sha ON manifest_blobs(blob_sha);
            CREATE INDEX IF NOT EXISTS idx_tags_repo_name ON tags(repo_id, name);
            CREATE INDEX IF NOT EXISTS idx_repos_name ON repos(name);
            """;
        schema.ExecuteNonQuery();
    }
}
