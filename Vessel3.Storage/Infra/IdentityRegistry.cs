using System.Globalization;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace Vessel3.Storage;

internal sealed class IdentityRegistry : IIdentityRegistry
{
    private const string KeyAlphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
    private const string KeyPrefix = "V3AK";
    private readonly string dbPath;
    private readonly TimeProvider clock;
    private readonly Lock gate = new();
    private SqliteConnection? conn;
    private readonly IWebhookEventPublisher? publisher;
    private bool disposed;

    public IdentityRegistry(IdentityOptions options) : this(options, TimeProvider.System) { }

    public IdentityRegistry(IdentityOptions options, TimeProvider clock, IWebhookEventPublisher? publisher = null)
    {
        this.clock = clock;
        this.publisher = publisher;
        dbPath = Path.Combine(options.Root, "iam.db");
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

            var id = "usr_" + Ulid.NewUlid().ToString();
            var now = clock.GetUtcNow();

            using var cmd = conn!.CreateCommand();
            cmd.CommandText = """
                INSERT INTO users (id, username, role, status, created_at)
                VALUES (@id, @username, @role, @status, @created_at);
                """;
            cmd.Parameters.AddWithValue("@id", id);
            cmd.Parameters.AddWithValue("@username", username);
            cmd.Parameters.AddWithValue("@role", (int)role);
            cmd.Parameters.AddWithValue("@status", (int)UserStatus.Active);
            cmd.Parameters.AddWithValue("@created_at", now.ToString("O"));
            cmd.ExecuteNonQuery();

            user = new User(id, username, role, UserStatus.Active, now);
        }

        publisher?.Publish(VesselEvents.UserCreated(user.Id, role.ToString()));
        return user;
    }

    public Result<User?> GetUser(string userId)
    {
        lock (gate)
        {
            EnsureOpen();
            using var cmd = conn!.CreateCommand();
            cmd.CommandText = "SELECT id, username, role, status, created_at FROM users WHERE id = @id;";
            cmd.Parameters.AddWithValue("@id", userId);
            using var reader = cmd.ExecuteReader();
            return reader.Read() ? ReadUser(reader) : (User?)null;
        }
    }

    public Result<User?> FindUserByUsername(string username)
    {
        lock (gate)
        {
            EnsureOpen();
            using var cmd = conn!.CreateCommand();
            cmd.CommandText = "SELECT id, username, role, status, created_at FROM users WHERE username = @username;";
            cmd.Parameters.AddWithValue("@username", username);
            using var reader = cmd.ExecuteReader();
            return reader.Read() ? ReadUser(reader) : (User?)null;
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
            using var cmd = conn!.CreateCommand();
            cmd.CommandText = "UPDATE users SET role = @role WHERE id = @id;";
            cmd.Parameters.AddWithValue("@role", (int)role);
            cmd.Parameters.AddWithValue("@id", userId);
            return cmd.ExecuteNonQuery() == 0
                ? new NotFoundError($"User {userId}")
                : Result.Ok;
        }
    }

    public Result UpdateUserStatus(string userId, UserStatus status)
    {
        lock (gate)
        {
            EnsureOpen();
            using var cmd = conn!.CreateCommand();
            cmd.CommandText = "UPDATE users SET status = @status WHERE id = @id;";
            cmd.Parameters.AddWithValue("@status", (int)status);
            cmd.Parameters.AddWithValue("@id", userId);
            return cmd.ExecuteNonQuery() == 0
                ? new NotFoundError($"User {userId}")
                : Result.Ok;
        }
    }

    public Result DeleteUser(string userId)
    {
        lock (gate)
        {
            EnsureOpen();
            using var cmd = conn!.CreateCommand();
            cmd.CommandText = "DELETE FROM users WHERE id = @id;";
            cmd.Parameters.AddWithValue("@id", userId);
            if (cmd.ExecuteNonQuery() == 0)
                return new NotFoundError($"User {userId}");
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

            return new AccessKey(keyId, secretKey, userId, description, now, expiresAt, false);
        }
    }

    public Result<AccessKey?> GetAccessKey(string accessKeyId)
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
            return reader.Read() ? ReadAccessKey(reader) : (AccessKey?)null;
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
            return cmd.ExecuteNonQuery() == 0
                ? new NotFoundError($"AccessKey {accessKeyId}")
                : Result.Ok;
        }
    }

    public Result<CallerIdentity> AuthenticateAccessKey(string accessKeyId)
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

            foreach (var id in toPromote)
            {
                using var updateCmd = conn.CreateCommand();
                updateCmd.CommandText = "UPDATE users SET role = @role WHERE id = @id;";
                updateCmd.Parameters.AddWithValue("@role", (int)UserRole.Admin);
                updateCmd.Parameters.AddWithValue("@id", id);
                updateCmd.ExecuteNonQuery();
            }

            return Result.Ok;
        }
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
        checkCmd.CommandText = "SELECT id, username, role, status, created_at FROM users WHERE username = 'admin';";
        using var reader = checkCmd.ExecuteReader();
        if (reader.Read()) return ReadUser(reader);
        reader.Close();

        var id = "usr_" + Ulid.NewUlid().ToString();
        var now = clock.GetUtcNow();
        using var insertCmd = conn.CreateCommand();
        insertCmd.CommandText = """
            INSERT INTO users (id, username, role, status, created_at)
            VALUES (@id, 'admin', 0, 0, @now);
            """;
        insertCmd.Parameters.AddWithValue("@id", id);
        insertCmd.Parameters.AddWithValue("@now", now.ToString("O"));
        insertCmd.ExecuteNonQuery();

        return new User(id, "admin", UserRole.Admin, UserStatus.Active, now);
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
            VALUES (@id, @secret, @user_id, 'Bootstrap Admin Key', @now, NULL, 0);
            """;
        insertCmd.Parameters.AddWithValue("@id", accessKey);
        insertCmd.Parameters.AddWithValue("@secret", secretKey);
        insertCmd.Parameters.AddWithValue("@user_id", userId);
        insertCmd.Parameters.AddWithValue("@now", now.ToString("O"));
        insertCmd.ExecuteNonQuery();
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
                created_at TEXT NOT NULL
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

            CREATE INDEX IF NOT EXISTS idx_access_keys_user ON access_keys(user_id);
            """;
        schema.ExecuteNonQuery();
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

    private bool UserExistsById(string id)
    {
        using var cmd = conn!.CreateCommand();
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

    private static string GenerateSecretToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(30));
}
