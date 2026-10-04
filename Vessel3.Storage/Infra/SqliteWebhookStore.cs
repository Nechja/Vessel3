using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Vessel3.Storage;

internal sealed class SqliteWebhookStore : IWebhookStore
{
    private readonly string dbPath;
    private readonly System.Threading.Lock gate = new();
    private readonly TimeProvider clock;
    private SqliteConnection? conn;
    private bool disposed;

    public SqliteWebhookStore(WebhookStoreOptions options) : this(options, TimeProvider.System) { }

    public SqliteWebhookStore(WebhookStoreOptions options, TimeProvider clock)
    {
        this.clock = clock;
        Directory.CreateDirectory(options.Root);
        dbPath = Path.Combine(options.Root, "webhooks.db");
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

    private void EnsureOpen()
    {
        if (conn is null)
        {
            conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadWriteCreate;Cache=Shared");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA journal_mode = WAL; PRAGMA synchronous = NORMAL;";
            cmd.ExecuteNonQuery();
        }
    }

    private void InitializeDatabase()
    {
        lock (gate)
        {
            EnsureOpen();
            using var cmd = conn!.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS webhooks (
                    id TEXT PRIMARY KEY,
                    name TEXT NOT NULL,
                    url TEXT NOT NULL,
                    secret TEXT,
                    event_filters TEXT NOT NULL,
                    resource_filters TEXT,
                    active INTEGER NOT NULL DEFAULT 1,
                    created_at TEXT NOT NULL,
                    last_triggered_at TEXT,
                    last_status_code INTEGER,
                    last_error TEXT,
                    is_static INTEGER NOT NULL DEFAULT 0
                );
                CREATE INDEX IF NOT EXISTS idx_webhooks_active ON webhooks(active);
                """;
            cmd.ExecuteNonQuery();
        }
    }

    public Result<IReadOnlyList<Webhook>> ListWebhooks()
    {
        lock (gate)
        {
            EnsureOpen();
            using var cmd = conn!.CreateCommand();
            cmd.CommandText = "SELECT id, name, url, secret, event_filters, resource_filters, active, created_at, last_triggered_at, last_status_code, last_error, is_static FROM webhooks ORDER BY created_at ASC;";
            using var reader = cmd.ExecuteReader();
            var list = new List<Webhook>();
            while (reader.Read())
            {
                list.Add(ReadWebhook(reader));
            }
            return list;
        }
    }

    public Result<Webhook?> GetWebhook(string id)
    {
        lock (gate)
        {
            EnsureOpen();
            using var cmd = conn!.CreateCommand();
            cmd.CommandText = "SELECT id, name, url, secret, event_filters, resource_filters, active, created_at, last_triggered_at, last_status_code, last_error, is_static FROM webhooks WHERE id = $id;";
            cmd.Parameters.AddWithValue("$id", id);
            using var reader = cmd.ExecuteReader();
            return reader.Read() ? (Webhook?)ReadWebhook(reader) : null;
        }
    }

    public Result<Webhook> CreateWebhook(CreateWebhookRequest request, bool isStatic = false)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            return new InvalidArgumentError("Webhook name cannot be empty");
        if (string.IsNullOrWhiteSpace(request.Url) || !Uri.TryCreate(request.Url, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
            return new InvalidArgumentError("Valid HTTP/HTTPS webhook URL is required");
        if (request.EventFilters is null || request.EventFilters.Count == 0)
            return new InvalidArgumentError("At least one event filter must be specified");

        var id = "whk_" + Ulid.NewUlid().ToString();
        var now = clock.GetUtcNow();

        lock (gate)
        {
            EnsureOpen();
            using var cmd = conn!.CreateCommand();
            cmd.CommandText = """
                INSERT INTO webhooks (id, name, url, secret, event_filters, resource_filters, active, created_at, is_static)
                VALUES ($id, $name, $url, $secret, $events, $resources, $active, $created_at, $is_static);
                """;
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$name", request.Name.Trim());
            cmd.Parameters.AddWithValue("$url", request.Url.Trim());
            cmd.Parameters.AddWithValue("$secret", (object?)request.Secret ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$events", JsonSerializer.Serialize(request.EventFilters, WebhookJsonContext.Default.IReadOnlyListString));
            cmd.Parameters.AddWithValue("$resources", request.ResourceFilters is { Count: > 0 } ? JsonSerializer.Serialize(request.ResourceFilters, WebhookJsonContext.Default.IReadOnlyListString) : DBNull.Value);
            cmd.Parameters.AddWithValue("$active", request.Active ? 1 : 0);
            cmd.Parameters.AddWithValue("$created_at", now.ToString("O"));
            cmd.Parameters.AddWithValue("$is_static", isStatic ? 1 : 0);
            cmd.ExecuteNonQuery();

            return new Webhook(
                id,
                request.Name.Trim(),
                request.Url.Trim(),
                request.Secret,
                request.EventFilters,
                request.ResourceFilters,
                request.Active,
                now,
                null,
                null,
                null,
                isStatic);
        }
    }

    public Result<Webhook> UpdateWebhook(string id, UpdateWebhookRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            return new InvalidArgumentError("Webhook name cannot be empty");
        if (string.IsNullOrWhiteSpace(request.Url) || !Uri.TryCreate(request.Url, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
            return new InvalidArgumentError("Valid HTTP/HTTPS webhook URL is required");
        if (request.EventFilters is null || request.EventFilters.Count == 0)
            return new InvalidArgumentError("At least one event filter must be specified");

        lock (gate)
        {
            EnsureOpen();
            var existingResult = GetWebhook(id);
            if (!existingResult.TryGetValue(out var existing, out var err))
                return err;
            if (existing is null)
                return new NoSuchWebhookError(id);
            if (existing.IsStatic)
                return new InvalidArgumentError("Cannot modify static webhook defined via YAML");

            using var cmd = conn!.CreateCommand();
            cmd.CommandText = """
                UPDATE webhooks
                SET name = $name, url = $url, secret = $secret, event_filters = $events, resource_filters = $resources, active = $active
                WHERE id = $id;
                """;
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$name", request.Name.Trim());
            cmd.Parameters.AddWithValue("$url", request.Url.Trim());
            cmd.Parameters.AddWithValue("$secret", (object?)request.Secret ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$events", JsonSerializer.Serialize(request.EventFilters, WebhookJsonContext.Default.IReadOnlyListString));
            cmd.Parameters.AddWithValue("$resources", request.ResourceFilters is { Count: > 0 } ? JsonSerializer.Serialize(request.ResourceFilters, WebhookJsonContext.Default.IReadOnlyListString) : DBNull.Value);
            cmd.Parameters.AddWithValue("$active", request.Active ? 1 : 0);
            cmd.ExecuteNonQuery();

            return existing with
            {
                Name = request.Name.Trim(),
                Url = request.Url.Trim(),
                Secret = request.Secret,
                EventFilters = request.EventFilters,
                ResourceFilters = request.ResourceFilters,
                Active = request.Active
            };
        }
    }

    public Result DeleteWebhook(string id)
    {
        lock (gate)
        {
            EnsureOpen();
            var existingResult = GetWebhook(id);
            if (!existingResult.TryGetValue(out var existing, out var err))
                return err;
            if (existing is null)
                return new NoSuchWebhookError(id);
            if (existing.IsStatic)
                return new InvalidArgumentError("Cannot delete static webhook defined via YAML");

            using var cmd = conn!.CreateCommand();
            cmd.CommandText = "DELETE FROM webhooks WHERE id = $id;";
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
            return Result.Ok;
        }
    }

    public Result RecordDeliveryResult(string id, DateTimeOffset timestamp, int? statusCode, string? error)
    {
        lock (gate)
        {
            EnsureOpen();
            using var cmd = conn!.CreateCommand();
            cmd.CommandText = """
                UPDATE webhooks
                SET last_triggered_at = $triggered, last_status_code = $code, last_error = $error
                WHERE id = $id;
                """;
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$triggered", timestamp.ToString("O"));
            cmd.Parameters.AddWithValue("$code", (object?)statusCode ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$error", (object?)error ?? DBNull.Value);
            cmd.ExecuteNonQuery();
            return Result.Ok;
        }
    }

    public void UpsertStaticWebhook(Webhook webhook)
    {
        lock (gate)
        {
            EnsureOpen();
            using var cmd = conn!.CreateCommand();
            cmd.CommandText = """
                INSERT INTO webhooks (id, name, url, secret, event_filters, resource_filters, active, created_at, is_static)
                VALUES ($id, $name, $url, $secret, $events, $resources, $active, $created_at, 1)
                ON CONFLICT(id) DO UPDATE SET
                    name = excluded.name,
                    url = excluded.url,
                    secret = excluded.secret,
                    event_filters = excluded.event_filters,
                    resource_filters = excluded.resource_filters,
                    active = excluded.active,
                    is_static = 1;
                """;
            cmd.Parameters.AddWithValue("$id", webhook.Id);
            cmd.Parameters.AddWithValue("$name", webhook.Name);
            cmd.Parameters.AddWithValue("$url", webhook.Url);
            cmd.Parameters.AddWithValue("$secret", (object?)webhook.Secret ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$events", JsonSerializer.Serialize(webhook.EventFilters, WebhookJsonContext.Default.IReadOnlyListString));
            cmd.Parameters.AddWithValue("$resources", webhook.ResourceFilters is { Count: > 0 } ? JsonSerializer.Serialize(webhook.ResourceFilters, WebhookJsonContext.Default.IReadOnlyListString) : DBNull.Value);
            cmd.Parameters.AddWithValue("$active", webhook.Active ? 1 : 0);
            cmd.Parameters.AddWithValue("$created_at", webhook.CreatedAt.ToString("O"));
            cmd.ExecuteNonQuery();
        }
    }

    private static Webhook ReadWebhook(SqliteDataReader reader)
    {
        var id = reader.GetString(0);
        var name = reader.GetString(1);
        var url = reader.GetString(2);
        var secret = reader.IsDBNull(3) ? null : reader.GetString(3);
        var eventsJson = reader.GetString(4);
        var resourcesJson = reader.IsDBNull(5) ? null : reader.GetString(5);
        var active = reader.GetInt32(6) == 1;
        var createdAt = DateTimeOffset.Parse(reader.GetString(7), CultureInfo.InvariantCulture);
        var lastTriggered = reader.IsDBNull(8) ? (DateTimeOffset?)null : DateTimeOffset.Parse(reader.GetString(8), CultureInfo.InvariantCulture);
        var lastStatus = reader.IsDBNull(9) ? (int?)null : reader.GetInt32(9);
        var lastError = reader.IsDBNull(10) ? null : reader.GetString(10);
        var isStatic = reader.GetInt32(11) == 1;

        var events = JsonSerializer.Deserialize(eventsJson, WebhookJsonContext.Default.ListString) ?? [];
        var resources = !string.IsNullOrEmpty(resourcesJson)
            ? JsonSerializer.Deserialize(resourcesJson, WebhookJsonContext.Default.ListString)
            : null;

        return new Webhook(
            id,
            name,
            url,
            secret,
            events,
            resources,
            active,
            createdAt,
            lastTriggered,
            lastStatus,
            lastError,
            isStatic);
    }
}
