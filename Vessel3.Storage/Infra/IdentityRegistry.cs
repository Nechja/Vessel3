using System.Globalization;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace Vessel3.Storage;

internal sealed class IdentityRegistry : IIdentityRegistry
{
    private const string KeyAlphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
    private const string KeyPrefix = "V3AK";
    private const string IamDbFileName = "iam.db";
    private const string UserPrefix = "usr_";
    private const string AdminUsername = "admin";
    private const string BootstrapAdminUserId = "usr_bootstrap_admin";
    private const string BootstrapAdminKeyDesc = "Bootstrap Admin Key";
    private const string IamGenerationMetaKey = "iam_generation";
    private const string TableUsers = "users";
    private const string ColUpdatedAtMs = "updated_at_ms";
    private const string TypeIntDefaultZero = "INTEGER NOT NULL DEFAULT 0";

    private static readonly AsyncLocal<bool> SyncInProgress = new();

    private readonly string dbPath;
    private readonly TimeProvider clock;
    private readonly Lock gate = new();
    private SqliteConnection? conn;
    private readonly IWebhookEventPublisher? publisher;
    private long generation;
    private IClusterIamSyncer? clusterSync;
    private bool disposed;

    public void RegisterClusterSyncer(IClusterIamSyncer syncer) => clusterSync = syncer;

    public long Generation => Volatile.Read(ref generation);

    public IdentityRegistry(IdentityOptions options) : this(options, TimeProvider.System) { }

    public IdentityRegistry(IdentityOptions options, TimeProvider clock, IWebhookEventPublisher? publisher = null)
    {
        this.clock = clock;
        this.publisher = publisher;
        dbPath = Path.Combine(options.Root, IamDbFileName);
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

    public Result<User> CreateUser(string username, UserRole role = UserRole.Member)
    {
        if (string.IsNullOrWhiteSpace(username))
            return new InvalidArgumentError("Username cannot be empty");

        User user;
        lock (gate)
        {
            EnsureOpen();
            if (UserExists(username))
                return new InvalidArgumentError($"User '{username}' already exists");

            var id = UserPrefix + Ulid.NewUlid().ToString();
            var now = clock.GetUtcNow();
            var nowMs = now.ToUnixTimeMilliseconds();

            using var cmd = conn!.CreateCommand();
            cmd.CommandText = """
                INSERT INTO users (id, username, role, status, created_at, updated_at_ms)
                VALUES (@id, @username, @role, @status, @created_at, @updated_at_ms);
                DELETE FROM deleted_users WHERE username = @username;
                """;
            cmd.Parameters.AddWithValue("@id", id);
            cmd.Parameters.AddWithValue("@username", username);
            cmd.Parameters.AddWithValue("@role", (int)role);
            cmd.Parameters.AddWithValue("@status", (int)UserStatus.Active);
            cmd.Parameters.AddWithValue("@created_at", now.ToString("O"));
            cmd.Parameters.AddWithValue("@updated_at_ms", nowMs);
            cmd.ExecuteNonQuery();
            BumpGenerationLocked();

            user = new User(id, username, role, UserStatus.Active, now);
        }

        publisher?.Publish(VesselEvents.UserCreated(user.Id, role.ToString()));
        return user;
    }

    public Result<User> GetUser(string userId)
    {
        lock (gate)
        {
            EnsureOpen();
            using var cmd = conn!.CreateCommand();
            cmd.CommandText = "SELECT id, username, role, status, created_at FROM users WHERE id = @id;";
            cmd.Parameters.AddWithValue("@id", userId);
            using var reader = cmd.ExecuteReader();
            return reader.Read() ? ReadUser(reader) : new NotFoundError($"User {userId}");
        }
    }

    public Result<User> FindUserByUsername(string username)
    {
        lock (gate)
        {
            EnsureOpen();
            using var cmd = conn!.CreateCommand();
            cmd.CommandText = "SELECT id, username, role, status, created_at FROM users WHERE username = @username;";
            cmd.Parameters.AddWithValue("@username", username);
            using var reader = cmd.ExecuteReader();
            return reader.Read() ? ReadUser(reader) : new NotFoundError($"User with username '{username}'");
        }
    }

    public Result<IReadOnlyList<User>> ListUsers()
    {
        lock (gate)
        {
            EnsureOpen();
            using var cmd = conn!.CreateCommand();
            cmd.CommandText = "SELECT id, username, role, status, created_at FROM users ORDER BY username ASC;";
            using var reader = cmd.ExecuteReader();
            List<User> list = [];
            while (reader.Read())
            {
                list.Add(ReadUser(reader));
            }
            return list;
        }
    }

    public Result UpdateUserRole(string userId, UserRole role)
    {
        lock (gate)
        {
            EnsureOpen();
            var nowMs = clock.GetUtcNow().ToUnixTimeMilliseconds();
            using var cmd = conn!.CreateCommand();
            cmd.CommandText = "UPDATE users SET role = @role, updated_at_ms = MAX(updated_at_ms + 1, @now_ms) WHERE id = @id;";
            cmd.Parameters.AddWithValue("@role", (int)role);
            cmd.Parameters.AddWithValue("@now_ms", nowMs);
            cmd.Parameters.AddWithValue("@id", userId);
            if (cmd.ExecuteNonQuery() == 0)
                return new NotFoundError($"User {userId}");
            BumpGenerationLocked();
            return Result.Ok;
        }
    }

    public Result UpdateUserStatus(string userId, UserStatus status)
    {
        lock (gate)
        {
            EnsureOpen();
            var nowMs = clock.GetUtcNow().ToUnixTimeMilliseconds();
            using var cmd = conn!.CreateCommand();
            cmd.CommandText = "UPDATE users SET status = @status, updated_at_ms = MAX(updated_at_ms + 1, @now_ms) WHERE id = @id;";
            cmd.Parameters.AddWithValue("@status", (int)status);
            cmd.Parameters.AddWithValue("@now_ms", nowMs);
            cmd.Parameters.AddWithValue("@id", userId);
            if (cmd.ExecuteNonQuery() == 0)
                return new NotFoundError($"User {userId}");
            BumpGenerationLocked();
            return Result.Ok;
        }
    }

    public Result DeleteUser(string userId)
    {
        lock (gate)
        {
            EnsureOpen();
            string? username = null;
            using (var getCmd = conn!.CreateCommand())
            {
                getCmd.CommandText = "SELECT username FROM users WHERE id = @id;";
                getCmd.Parameters.AddWithValue("@id", userId);
                username = getCmd.ExecuteScalar() as string;
            }
            if (username is null)
                return new NotFoundError($"User {userId}");

            var nowMs = clock.GetUtcNow().ToUnixTimeMilliseconds();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                DELETE FROM users WHERE id = @id;
                INSERT INTO deleted_users (user_id, username, deleted_at_ms)
                VALUES (@id, @username, @deleted_at_ms)
                ON CONFLICT(user_id) DO UPDATE SET deleted_at_ms = excluded.deleted_at_ms;
                """;
            cmd.Parameters.AddWithValue("@id", userId);
            cmd.Parameters.AddWithValue("@username", username);
            cmd.Parameters.AddWithValue("@deleted_at_ms", nowMs);
            cmd.ExecuteNonQuery();
            BumpGenerationLocked();
        }

        publisher?.Publish(VesselEvents.UserDeleted(userId));
        return Result.Ok;
    }

    public Result<AccessKey> CreateAccessKey(string userId, string? description = null, TimeSpan? ttl = null)
    {
        lock (gate)
        {
            EnsureOpen();
            if (!UserExistsById(userId))
                return new NotFoundError($"User {userId}");

            var keyId = KeyPrefix + RandomNumberGenerator.GetString(KeyAlphabet, 16);
            var secretKey = GenerateSecretToken();
            var now = clock.GetUtcNow();
            var expiresAt = ttl.HasValue ? now + ttl.Value : (DateTimeOffset?)null;

            using var cmd = conn!.CreateCommand();
            cmd.CommandText = """
                INSERT INTO access_keys (id, secret_key, user_id, description, created_at, expires_at, is_revoked)
                VALUES (@id, @secret, @user_id, @desc, @created_at, @expires_at, 0);
                """;
            cmd.Parameters.AddWithValue("@id", keyId);
            cmd.Parameters.AddWithValue("@secret", secretKey);
            cmd.Parameters.AddWithValue("@user_id", userId);
            cmd.Parameters.AddWithValue("@desc", (object?)description ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@created_at", now.ToString("O"));
            cmd.Parameters.AddWithValue("@expires_at", expiresAt.HasValue ? expiresAt.Value.ToString("O") : DBNull.Value);
            cmd.ExecuteNonQuery();
            BumpGenerationLocked();

            return new AccessKey(keyId, secretKey, userId, description, now, expiresAt, false);
        }
    }

    public Result<AccessKey> GetAccessKey(string accessKeyId)
    {
        var result = GetAccessKeyCore(accessKeyId);
        if (result is Result<AccessKey>.Failure { Error: InvalidAccessKeyIdError } && TrySyncIamFromPeers())
            return GetAccessKeyCore(accessKeyId);
        return result;
    }

    private Result<AccessKey> GetAccessKeyCore(string accessKeyId)
    {
        lock (gate)
        {
            EnsureOpen();
            using var cmd = conn!.CreateCommand();
            cmd.CommandText = """
                SELECT id, secret_key, user_id, description, created_at, expires_at, is_revoked
                FROM access_keys WHERE id = @id;
                """;
            cmd.Parameters.AddWithValue("@id", accessKeyId);
            using var reader = cmd.ExecuteReader();
            return reader.Read() ? ReadAccessKey(reader) : new InvalidAccessKeyIdError(accessKeyId);
        }
    }

    public Result<IReadOnlyList<AccessKey>> ListAccessKeys(string userId)
    {
        lock (gate)
        {
            EnsureOpen();
            using var cmd = conn!.CreateCommand();
            cmd.CommandText = """
                SELECT id, secret_key, user_id, description, created_at, expires_at, is_revoked
                FROM access_keys WHERE user_id = @user_id ORDER BY created_at DESC;
                """;
            cmd.Parameters.AddWithValue("@user_id", userId);
            using var reader = cmd.ExecuteReader();
            List<AccessKey> list = [];
            while (reader.Read())
            {
                list.Add(ReadAccessKey(reader));
            }
            return list;
        }
    }

    public Result RevokeAccessKey(string accessKeyId)
    {
        lock (gate)
        {
            EnsureOpen();
            using var cmd = conn!.CreateCommand();
            cmd.CommandText = "UPDATE access_keys SET is_revoked = 1 WHERE id = @id;";
            cmd.Parameters.AddWithValue("@id", accessKeyId);
            if (cmd.ExecuteNonQuery() == 0)
                return new NotFoundError($"AccessKey {accessKeyId}");
            BumpGenerationLocked();
            return Result.Ok;
        }
    }

    public Result<CallerIdentity> AuthenticateAccessKey(string accessKeyId)
    {
        var result = AuthenticateAccessKeyCore(accessKeyId);
        if (result is Result<CallerIdentity>.Failure { Error: InvalidAccessKeyIdError } && TrySyncIamFromPeers())
            return AuthenticateAccessKeyCore(accessKeyId);
        return result;
    }

    private bool TrySyncIamFromPeers()
    {
        if (clusterSync is null || SyncInProgress.Value)
            return false;
        SyncInProgress.Value = true;
        try
        {
            return clusterSync.TrySyncIam();
        }
        finally
        {
            SyncInProgress.Value = false;
        }
    }

    private Result<CallerIdentity> AuthenticateAccessKeyCore(string accessKeyId)
    {
        lock (gate)
        {
            EnsureOpen();
            using var cmd = conn!.CreateCommand();
            cmd.CommandText = """
                SELECT a.id, a.expires_at, a.is_revoked, u.id, u.username, u.role, u.status
                FROM access_keys a
                JOIN users u ON a.user_id = u.id
                WHERE a.id = @id;
                """;
            cmd.Parameters.AddWithValue("@id", accessKeyId);
            using var reader = cmd.ExecuteReader();
            if (!reader.Read())
                return new InvalidAccessKeyIdError(accessKeyId);

            var isRevoked = reader.GetInt32(2) != 0;
            if (isRevoked)
                return new AccessDeniedError("Access key is revoked");

            if (!reader.IsDBNull(1))
            {
                var expiresAt = DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture);
                if (expiresAt <= clock.GetUtcNow())
                    return new ExpiredTokenError();
            }

            var status = (UserStatus)reader.GetInt32(6);
            if (status is UserStatus.Suspended)
                return new AccessDeniedError("User account is suspended");

            var userId = reader.GetString(3);
            var username = reader.GetString(4);
            var role = (UserRole)reader.GetInt32(5);

            return new CallerIdentity(userId, username, role, accessKeyId);
        }
    }

    public Result EnsureBootstrapAdmin(string? accessKey, string? secretKey)
    {
        if (string.IsNullOrEmpty(accessKey) || string.IsNullOrEmpty(secretKey))
            return Result.Ok;

        lock (gate)
        {
            EnsureOpen();
            var adminUser = FindOrCreateAdminUser();
            EnsureAdminKey(adminUser.Id, accessKey, secretKey);
            return Result.Ok;
        }
    }

    public Result EnsureAdminUsers(IReadOnlyList<string> adminPatterns)
    {
        if (adminPatterns is null || adminPatterns.Count == 0)
            return Result.Ok;

        lock (gate)
        {
            EnsureOpen();
            using var cmd = conn!.CreateCommand();
            cmd.CommandText = "SELECT id, username, role FROM users;";
            using var reader = cmd.ExecuteReader();
            List<string> toPromote = [];
            while (reader.Read())
            {
                var id = reader.GetString(0);
                var username = reader.GetString(1);
                var role = (UserRole)reader.GetInt32(2);
                if (role != UserRole.Admin && MatchesAdminPattern(username, adminPatterns))
                {
                    toPromote.Add(id);
                }
            }
            reader.Close();

            var nowMs = clock.GetUtcNow().ToUnixTimeMilliseconds();
            foreach (var id in toPromote)
            {
                using var updateCmd = conn.CreateCommand();
                updateCmd.CommandText = "UPDATE users SET role = @role, updated_at_ms = MAX(updated_at_ms + 1, @now_ms) WHERE id = @id;";
                updateCmd.Parameters.AddWithValue("@role", (int)UserRole.Admin);
                updateCmd.Parameters.AddWithValue("@now_ms", nowMs);
                updateCmd.Parameters.AddWithValue("@id", id);
                updateCmd.ExecuteNonQuery();
            }
            if (toPromote.Count > 0)
                BumpGenerationLocked();

            return Result.Ok;
        }
    }

    public IdentityReplicationSnapshot ExportSnapshot()
    {
        lock (gate)
        {
            EnsureOpen();
            var users = new List<ReplicatedUserEntry>();
            using (var uCmd = conn!.CreateCommand())
            {
                uCmd.CommandText = "SELECT id, username, role, status, created_at, updated_at_ms FROM users ORDER BY id ASC;";
                using var r = uCmd.ExecuteReader();
                while (r.Read())
                {
                    var created = DateTimeOffset.Parse(r.GetString(4), CultureInfo.InvariantCulture);
                    var updatedMs = r.GetInt64(5);
                    if (updatedMs <= 0) updatedMs = created.ToUnixTimeMilliseconds();
                    users.Add(new ReplicatedUserEntry(
                        r.GetString(0),
                        r.GetString(1),
                        (UserRole)r.GetInt32(2),
                        (UserStatus)r.GetInt32(3),
                        created,
                        updatedMs));
                }
            }

            var keys = new List<AccessKey>();
            using (var kCmd = conn.CreateCommand())
            {
                kCmd.CommandText = """
                    SELECT id, secret_key, user_id, description, created_at, expires_at, is_revoked
                    FROM access_keys ORDER BY id ASC;
                    """;
                using var r = kCmd.ExecuteReader();
                while (r.Read())
                    keys.Add(ReadAccessKey(r));
            }

            var deleted = new List<DeletedUserTombstone>();
            using (var dCmd = conn.CreateCommand())
            {
                dCmd.CommandText = "SELECT user_id, username, deleted_at_ms FROM deleted_users ORDER BY user_id ASC;";
                using var r = dCmd.ExecuteReader();
                while (r.Read())
                    deleted.Add(new DeletedUserTombstone(r.GetString(0), r.GetString(1), r.GetInt64(2)));
            }

            return new IdentityReplicationSnapshot(generation, users, keys, deleted);
        }
    }

    public bool MergeSnapshot(IdentityReplicationSnapshot snapshot)
    {
        lock (gate)
        {
            EnsureOpen();
            using var tx = conn!.BeginTransaction();
            var changed = false;
            var remappedUserIds = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (var tomb in snapshot.DeletedUsers)
            {
                if (MergeDeletedUserLocked(tomb, tx))
                    changed = true;
            }

            foreach (var u in snapshot.Users)
            {
                if (MergeUserLocked(u, remappedUserIds, tx))
                    changed = true;
            }

            foreach (var k in snapshot.AccessKeys)
            {
                if (MergeAccessKeyLocked(k, remappedUserIds, tx))
                    changed = true;
            }

            if (changed)
            {
                SetGenerationLocked(Math.Max(generation, snapshot.Generation) + 1, tx);
            }

            tx.Commit();
            return changed;
        }
    }

    private bool MergeDeletedUserLocked(DeletedUserTombstone tomb, SqliteTransaction tx)
    {
        var changed = false;
        using var checkDel = conn!.CreateCommand();
        checkDel.Transaction = tx;
        checkDel.CommandText = "SELECT deleted_at_ms FROM deleted_users WHERE user_id = @id;";
        checkDel.Parameters.AddWithValue("@id", tomb.UserId);
        var existingDelObj = checkDel.ExecuteScalar();
        var existingDelMs = existingDelObj is null or DBNull ? 0L : Convert.ToInt64(existingDelObj, CultureInfo.InvariantCulture);

        if (tomb.DeletedAtUnixMs > existingDelMs)
        {
            using var insDel = conn.CreateCommand();
            insDel.Transaction = tx;
            insDel.CommandText = """
                INSERT INTO deleted_users (user_id, username, deleted_at_ms)
                VALUES (@id, @username, @del_ms)
                ON CONFLICT(user_id) DO UPDATE SET deleted_at_ms = excluded.deleted_at_ms;
                """;
            insDel.Parameters.AddWithValue("@id", tomb.UserId);
            insDel.Parameters.AddWithValue("@username", tomb.Username);
            insDel.Parameters.AddWithValue("@del_ms", tomb.DeletedAtUnixMs);
            insDel.ExecuteNonQuery();
            changed = true;
        }

        using var delUser = conn.CreateCommand();
        delUser.Transaction = tx;
        delUser.CommandText = "DELETE FROM users WHERE id = @id AND updated_at_ms <= @del_ms;";
        delUser.Parameters.AddWithValue("@id", tomb.UserId);
        delUser.Parameters.AddWithValue("@del_ms", tomb.DeletedAtUnixMs);
        if (delUser.ExecuteNonQuery() > 0)
            changed = true;

        return changed;
    }

    private bool MergeUserLocked(ReplicatedUserEntry u, Dictionary<string, string> remappedUserIds, SqliteTransaction tx)
    {
        if (IsUserDeletedAfterLocked(u.Id, u.UpdatedAtUnixMs, tx))
            return false;

        using var byNameCmd = conn!.CreateCommand();
        byNameCmd.Transaction = tx;
        byNameCmd.CommandText = "SELECT id, role, status, updated_at_ms FROM users WHERE username = @username;";
        byNameCmd.Parameters.AddWithValue("@username", u.Username);
        using var r = byNameCmd.ExecuteReader();
        if (r.Read())
        {
            var localId = r.GetString(0);
            var localRole = (UserRole)r.GetInt32(1);
            var localStatus = (UserStatus)r.GetInt32(2);
            var localUpdated = r.GetInt64(3);
            r.Close();

            if (!string.Equals(localId, u.Id, StringComparison.Ordinal))
                remappedUserIds[u.Id] = localId;

            if (u.UpdatedAtUnixMs <= localUpdated || (localRole == u.Role && localStatus == u.Status))
                return false;

            using var upd = conn.CreateCommand();
            upd.Transaction = tx;
            upd.CommandText = "UPDATE users SET role = @role, status = @status, updated_at_ms = @upd WHERE id = @id;";
            upd.Parameters.AddWithValue("@role", (int)u.Role);
            upd.Parameters.AddWithValue("@status", (int)u.Status);
            upd.Parameters.AddWithValue("@upd", u.UpdatedAtUnixMs);
            upd.Parameters.AddWithValue("@id", localId);
            upd.ExecuteNonQuery();
            return true;
        }
        r.Close();

        using var ins = conn.CreateCommand();
        ins.Transaction = tx;
        ins.CommandText = """
            INSERT INTO users (id, username, role, status, created_at, updated_at_ms)
            VALUES (@id, @username, @role, @status, @created_at, @updated_at_ms)
            ON CONFLICT(id) DO UPDATE SET
                role = CASE WHEN excluded.updated_at_ms > users.updated_at_ms THEN excluded.role ELSE users.role END,
                status = CASE WHEN excluded.updated_at_ms > users.updated_at_ms THEN excluded.status ELSE users.status END,
                updated_at_ms = MAX(users.updated_at_ms, excluded.updated_at_ms);
            """;
        ins.Parameters.AddWithValue("@id", u.Id);
        ins.Parameters.AddWithValue("@username", u.Username);
        ins.Parameters.AddWithValue("@role", (int)u.Role);
        ins.Parameters.AddWithValue("@status", (int)u.Status);
        ins.Parameters.AddWithValue("@created_at", u.CreatedAt.ToString("O"));
        ins.Parameters.AddWithValue("@updated_at_ms", u.UpdatedAtUnixMs);
        ins.ExecuteNonQuery();
        return true;
    }

    private bool MergeAccessKeyLocked(AccessKey k, Dictionary<string, string> remappedUserIds, SqliteTransaction tx)
    {
        var targetUserId = remappedUserIds.GetValueOrDefault(k.UserId, k.UserId);
        if (!UserExistsById(targetUserId, tx))
            return false;

        using var checkKey = conn!.CreateCommand();
        checkKey.Transaction = tx;
        checkKey.CommandText = "SELECT is_revoked FROM access_keys WHERE id = @id;";
        checkKey.Parameters.AddWithValue("@id", k.Id);
        var existingRevokedObj = checkKey.ExecuteScalar();
        if (existingRevokedObj is not null and not DBNull)
        {
            var localRevoked = Convert.ToInt32(existingRevokedObj, CultureInfo.InvariantCulture) != 0;
            if (!k.IsRevoked || localRevoked)
                return false;

            using var revCmd = conn.CreateCommand();
            revCmd.Transaction = tx;
            revCmd.CommandText = "UPDATE access_keys SET is_revoked = 1 WHERE id = @id;";
            revCmd.Parameters.AddWithValue("@id", k.Id);
            revCmd.ExecuteNonQuery();
            return true;
        }

        using var insKey = conn.CreateCommand();
        insKey.Transaction = tx;
        insKey.CommandText = """
            INSERT INTO access_keys (id, secret_key, user_id, description, created_at, expires_at, is_revoked)
            VALUES (@id, @secret, @user_id, @desc, @created_at, @expires_at, @is_revoked);
            """;
        insKey.Parameters.AddWithValue("@id", k.Id);
        insKey.Parameters.AddWithValue("@secret", k.SecretKey);
        insKey.Parameters.AddWithValue("@user_id", targetUserId);
        insKey.Parameters.AddWithValue("@desc", (object?)k.Description ?? DBNull.Value);
        insKey.Parameters.AddWithValue("@created_at", k.CreatedAt.ToString("O"));
        insKey.Parameters.AddWithValue("@expires_at", k.ExpiresAt.HasValue ? k.ExpiresAt.Value.ToString("O") : DBNull.Value);
        insKey.Parameters.AddWithValue("@is_revoked", k.IsRevoked ? 1 : 0);
        insKey.ExecuteNonQuery();
        return true;
    }

    private bool IsUserDeletedAfterLocked(string userId, long userUpdatedMs, SqliteTransaction? tx = null)
    {
        using var cmd = conn!.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT deleted_at_ms FROM deleted_users WHERE user_id = @id;";
        cmd.Parameters.AddWithValue("@id", userId);
        var val = cmd.ExecuteScalar();
        return val is not null and not DBNull && Convert.ToInt64(val, CultureInfo.InvariantCulture) >= userUpdatedMs;
    }

    public static bool MatchesAdminPattern(string username, IReadOnlyList<string>? patterns)
    {
        if (patterns is null || patterns.Count == 0) return false;
        foreach (var raw in patterns)
        {
            var p = raw.Trim();
            if (string.IsNullOrEmpty(p)) continue;
            if (username.Contains(p, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private User FindOrCreateAdminUser()
    {
        using var checkCmd = conn!.CreateCommand();
        checkCmd.CommandText = "SELECT id, username, role, status, created_at FROM users WHERE username = @username;";
        checkCmd.Parameters.AddWithValue("@username", AdminUsername);
        using var reader = checkCmd.ExecuteReader();
        if (reader.Read()) return ReadUser(reader);
        reader.Close();

        var id = BootstrapAdminUserId;
        var now = clock.GetUtcNow();
        using var insertCmd = conn.CreateCommand();
        insertCmd.CommandText = """
            INSERT INTO users (id, username, role, status, created_at, updated_at_ms)
            VALUES (@id, @username, 0, 0, @now, @now_ms);
            """;
        insertCmd.Parameters.AddWithValue("@id", id);
        insertCmd.Parameters.AddWithValue("@username", AdminUsername);
        insertCmd.Parameters.AddWithValue("@now", now.ToString("O"));
        insertCmd.Parameters.AddWithValue("@now_ms", now.ToUnixTimeMilliseconds());
        insertCmd.ExecuteNonQuery();
        BumpGenerationLocked();

        return new User(id, AdminUsername, UserRole.Admin, UserStatus.Active, now);
    }

    private void EnsureAdminKey(string userId, string accessKey, string secretKey)
    {
        using var checkCmd = conn!.CreateCommand();
        checkCmd.CommandText = "SELECT id FROM access_keys WHERE id = @id;";
        checkCmd.Parameters.AddWithValue("@id", accessKey);
        using var reader = checkCmd.ExecuteReader();
        if (reader.Read()) return;
        reader.Close();

        var now = clock.GetUtcNow();
        using var insertCmd = conn.CreateCommand();
        insertCmd.CommandText = """
            INSERT INTO access_keys (id, secret_key, user_id, description, created_at, expires_at, is_revoked)
            VALUES (@id, @secret, @user_id, @desc, @now, NULL, 0);
            """;
        insertCmd.Parameters.AddWithValue("@id", accessKey);
        insertCmd.Parameters.AddWithValue("@secret", secretKey);
        insertCmd.Parameters.AddWithValue("@user_id", userId);
        insertCmd.Parameters.AddWithValue("@desc", BootstrapAdminKeyDesc);
        insertCmd.Parameters.AddWithValue("@now", now.ToString("O"));
        insertCmd.ExecuteNonQuery();
        BumpGenerationLocked();
    }

    private void InitializeDatabase()
    {
        var dir = Path.GetDirectoryName(dbPath)!;
        Directory.CreateDirectory(dir);

        conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadWriteCreate;Pooling=False");
        conn.Open();

        using var pragma = conn.CreateCommand();
        pragma.CommandText = """
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous = NORMAL;
            PRAGMA foreign_keys = ON;
            """;
        pragma.ExecuteNonQuery();

        using var schema = conn.CreateCommand();
        schema.CommandText = """
            CREATE TABLE IF NOT EXISTS users (
                id TEXT PRIMARY KEY,
                username TEXT NOT NULL UNIQUE,
                role INTEGER NOT NULL,
                status INTEGER NOT NULL,
                created_at TEXT NOT NULL,
                updated_at_ms INTEGER NOT NULL DEFAULT 0
            );

            CREATE TABLE IF NOT EXISTS access_keys (
                id TEXT PRIMARY KEY,
                secret_key TEXT NOT NULL,
                user_id TEXT NOT NULL REFERENCES users(id) ON DELETE CASCADE,
                description TEXT,
                created_at TEXT NOT NULL,
                expires_at TEXT,
                is_revoked INTEGER NOT NULL DEFAULT 0
            );

            CREATE TABLE IF NOT EXISTS deleted_users (
                user_id TEXT PRIMARY KEY,
                username TEXT NOT NULL,
                deleted_at_ms INTEGER NOT NULL
            );

            CREATE TABLE IF NOT EXISTS meta (
                key TEXT PRIMARY KEY,
                value INTEGER NOT NULL
            );

            INSERT OR IGNORE INTO meta (key, value) VALUES ('iam_generation', 1);

            CREATE INDEX IF NOT EXISTS idx_access_keys_user ON access_keys(user_id);
            """;
        schema.ExecuteNonQuery();

        EnsureColumnExists(TableUsers, ColUpdatedAtMs, TypeIntDefaultZero);
        generation = ReadGenerationLocked();
    }

    private void EnsureColumnExists(string table, string column, string definition)
    {
        using var check = conn!.CreateCommand();
        check.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = $col;";
        check.Parameters.AddWithValue("$col", column);
        if (Convert.ToInt64(check.ExecuteScalar(), CultureInfo.InvariantCulture) > 0)
            return;

        using var alter = conn.CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition};";
        alter.ExecuteNonQuery();
    }

    private long ReadGenerationLocked()
    {
        using var cmd = conn!.CreateCommand();
        cmd.CommandText = "SELECT value FROM meta WHERE key = @key;";
        cmd.Parameters.AddWithValue("@key", IamGenerationMetaKey);
        var val = cmd.ExecuteScalar();
        return val is null or DBNull ? 1L : Convert.ToInt64(val, CultureInfo.InvariantCulture);
    }

    private void BumpGenerationLocked() =>
        SetGenerationLocked(generation + 1);

    private void SetGenerationLocked(long nextGen, SqliteTransaction? tx = null)
    {
        Volatile.Write(ref generation, nextGen);
        using var cmd = conn!.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO meta (key, value) VALUES (@key, @val)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;
            """;
        cmd.Parameters.AddWithValue("@key", IamGenerationMetaKey);
        cmd.Parameters.AddWithValue("@val", nextGen);
        cmd.ExecuteNonQuery();
    }

    private void EnsureOpen() =>
        ObjectDisposedException.ThrowIf(disposed, this);

    private bool UserExists(string username)
    {
        using var cmd = conn!.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM users WHERE username = @username;";
        cmd.Parameters.AddWithValue("@username", username);
        return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture) > 0;
    }

    private bool UserExistsById(string id, SqliteTransaction? tx = null)
    {
        using var cmd = conn!.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT COUNT(*) FROM users WHERE id = @id;";
        cmd.Parameters.AddWithValue("@id", id);
        return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture) > 0;
    }

    private static User ReadUser(SqliteDataReader r) =>
        new(
            r.GetString(0),
            r.GetString(1),
            (UserRole)r.GetInt32(2),
            (UserStatus)r.GetInt32(3),
            DateTimeOffset.Parse(r.GetString(4), CultureInfo.InvariantCulture));

    private static AccessKey ReadAccessKey(SqliteDataReader r) =>
        new(
            r.GetString(0),
            r.GetString(1),
            r.GetString(2),
            r.IsDBNull(3) ? null : r.GetString(3),
            DateTimeOffset.Parse(r.GetString(4), CultureInfo.InvariantCulture),
            r.IsDBNull(5) ? null : DateTimeOffset.Parse(r.GetString(5), CultureInfo.InvariantCulture),
            r.GetInt32(6) != 0);

    private static string GenerateSecretToken()
    {
        Span<byte> bytes = stackalloc byte[30];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes);
    }
}
