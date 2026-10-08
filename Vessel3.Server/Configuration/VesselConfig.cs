using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Vessel3.Server.Configuration;

internal sealed record VesselConfig(
    string DataRoot,
    string? AccessKey,
    string? SecretKey,
    string Region,
    string[] BaseDomains,
    TimeSpan GcMaxWait,
    TimeSpan LifecycleInterval,
    TimeSpan CompactInterval,
    long CompactThresholdBytes,
    TimeSpan SlowRequestThreshold,
    string? MetricsToken,
    bool MetricsAllowAnonymous,
    OidcOptions? Oidc,
    IReadOnlyList<string>? AdminUsers = null,
    bool OciEnabled = true,
    bool WebDavEnabled = true,
    string? WebhooksFile = null,
    string LogFormat = "text",
    LogLevel LogLevel = LogLevel.Information,
    bool AccessLogEnabled = true,
    string? NodeId = null,
    OtelConfig? Otel = null,
    IReadOnlyList<StorageVolume>? Volumes = null)
{
    public static bool TryCreate([NotNullWhen(true)] out VesselConfig? config, [NotNullWhen(false)] out string? error)
    {
        var dataRoot = ReadString("VESSEL3_DATA", Path.Combine(AppContext.BaseDirectory, "data"))!;
        if (!EnsureDataRootWritable(dataRoot, out error))
        {
            config = null;
            return false;
        }

        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("XDG_DATA_HOME")))
        {
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", Path.Combine(dataRoot, ".xdg"));
        }

        if (!OidcOptions.FromEnvironment().TryGetValue(out var oidc, out var oidcError))
        {
            config = null;
            error = oidcError.Message;
            return false;
        }

        var accessKey = ReadString("VESSEL3_ACCESS_KEY");
        var secretKey = ReadString("VESSEL3_SECRET_KEY");
        var region = ReadString("VESSEL3_REGION", "us-east-1")!;
        var baseDomains = ReadDomains("VESSEL3_DOMAIN");

        var gcMaxWaitSeconds = ReadLong("VESSEL3_GC_MAX_WAIT_SECONDS", 120);
        var lifecycleIntervalSeconds = ReadLong("VESSEL3_LIFECYCLE_INTERVAL_SECONDS", 3600);
        var compactIntervalSeconds = ReadLong("VESSEL3_COMPACT_INTERVAL_SECONDS", 3600);
        var compactThresholdBytes = ReadLong("VESSEL3_COMPACT_THRESHOLD_BYTES", 64L * 1024 * 1024);
        var slowRequestMilliseconds = ReadLong("VESSEL3_SLOW_REQUEST_MS", 1000);

        var metricsToken = ReadString("VESSEL3_METRICS_TOKEN");
        var metricsAllowAnonymous = ReadBool("VESSEL3_METRICS_ALLOW_ANONYMOUS");
        var adminUsers = ReadList("VESSEL3_ADMIN_USERS");

        var ociEnabled = ReadFeatureFlag("VESSEL3_OCI_ENABLED");
        var webDavEnabled = ReadFeatureFlag("VESSEL3_WEBDAV_ENABLED");

        var webhooksFile = ReadString("VESSEL3_WEBHOOKS_FILE");
        var logFormat = ReadString("VESSEL3_LOG_FORMAT", "text")!.ToLowerInvariant();
        var logLevel = ReadLogLevel("VESSEL3_LOG_LEVEL", LogLevel.Information);
        var accessLogEnabled = ReadFeatureFlag("VESSEL3_ACCESS_LOG");
        var nodeId = ReadString("VESSEL3_NODE_ID", Environment.MachineName);

        var otelEndpoint = ReadString("VESSEL3_OTEL_EXPORTER_OTLP_ENDPOINT");
        var otelEnabled = ReadBool("VESSEL3_OTEL_ENABLED") || !string.IsNullOrWhiteSpace(otelEndpoint);
        var otelServiceName = ReadString("VESSEL3_OTEL_SERVICE_NAME", "vessel3")!;
        var otel = new OtelConfig(otelEnabled, otelEndpoint, otelServiceName);

        if (!TryReadVolumes("VESSEL3_VOLUMES", dataRoot, out var volumes, out error))
        {
            config = null;
            return false;
        }

        config = new VesselConfig(
            dataRoot,
            accessKey,
            secretKey,
            region,
            baseDomains,
            TimeSpan.FromSeconds(gcMaxWaitSeconds),
            TimeSpan.FromSeconds(lifecycleIntervalSeconds),
            TimeSpan.FromSeconds(compactIntervalSeconds),
            compactThresholdBytes,
            TimeSpan.FromMilliseconds(slowRequestMilliseconds),
            metricsToken,
            metricsAllowAnonymous,
            oidc,
            adminUsers,
            ociEnabled,
            webDavEnabled,
            webhooksFile,
            logFormat,
            logLevel,
            accessLogEnabled,
            nodeId,
            otel,
            volumes);

        error = null;
        return true;
    }

    private static bool EnsureDataRootWritable(string dataRoot, [NotNullWhen(false)] out string? error)
    {
        try
        {
            Directory.CreateDirectory(dataRoot);
            var probe = Path.Combine(dataRoot, ".vessel3-write-test");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            error = null;
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            error = $"VESSEL3_DATA ({dataRoot}) is not writable by the running user. " +
                    "On Kubernetes, set the pod securityContext.fsGroup to the runtime uid (1654) " +
                    "so the kubelet chowns the volume on mount.";
            return false;
        }
    }

    private static bool ReadFeatureFlag(string name, bool defaultValue = true)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrEmpty(value) ? defaultValue : !string.Equals(value, "false", StringComparison.OrdinalIgnoreCase);
    }

    private static LogLevel ReadLogLevel(string name, LogLevel fallback) =>
        Environment.GetEnvironmentVariable(name)?.ToLowerInvariant() switch
        {
            "trace" => LogLevel.Trace,
            "debug" => LogLevel.Debug,
            "info" or "information" => LogLevel.Information,
            "warn" or "warning" => LogLevel.Warning,
            "error" => LogLevel.Error,
            "critical" => LogLevel.Critical,
            "none" => LogLevel.None,
            _ => fallback
        };

    private static string? ReadString(string name, string? fallback = null)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrEmpty(value) ? fallback : value;
    }

    private static long ReadLong(string name, long fallback) =>
        long.TryParse(Environment.GetEnvironmentVariable(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedValue) ? parsedValue : fallback;

    private static bool ReadBool(string name) =>
        string.Equals(Environment.GetEnvironmentVariable(name), "true", StringComparison.OrdinalIgnoreCase);

    private static string[] ReadDomains(string name)
    {
        var raw = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(raw)
            ? []
            : [.. raw.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(domain => VirtualHostResolver.StripPort(domain).ToLowerInvariant())
                .Distinct()];
    }

    private static string[] ReadList(string name)
    {
        var raw = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(raw)
            ? []
            : [.. raw.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct()];
    }

    internal static bool TryParseVolumes(
        string? raw,
        string dataRoot,
        [NotNullWhen(true)] out IReadOnlyList<StorageVolume>? volumes,
        [NotNullWhen(false)] out string? error)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            volumes = [StorageVolume.CreateDefault(dataRoot)];
            error = null;
            return true;
        }

        List<StorageVolume> list = [];
        HashSet<string> seenIds = new(StringComparer.OrdinalIgnoreCase);
        var parts = raw.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var part in parts)
        {
            var tokens = part.Split(':', StringSplitOptions.TrimEntries);
            if (tokens.Length < 2)
            {
                volumes = null;
                error = $"Invalid volume entry '{part}': expected at least 'id:path'";
                return false;
            }

            var id = tokens[0];
            var path = tokens[1];
            if (string.IsNullOrWhiteSpace(id))
            {
                volumes = null;
                error = $"Invalid volume entry '{part}': volume id cannot be empty";
                return false;
            }

            if (string.IsNullOrWhiteSpace(path))
            {
                volumes = null;
                error = $"Invalid volume entry '{part}': volume path cannot be empty";
                return false;
            }

            if (!seenIds.Add(id))
            {
                volumes = null;
                error = $"Duplicate volume id '{id}'";
                return false;
            }

            var pool = tokens.Length > 2 && !string.IsNullOrWhiteSpace(tokens[2]) ? tokens[2] : "default";
            var flags = VolumeCapabilities.None;

            if (tokens.Length > 3 && !string.IsNullOrWhiteSpace(tokens[3]))
            {
                var flagTokens = tokens[3].Split('+', StringSplitOptions.TrimEntries);
                foreach (var ft in flagTokens)
                {
                    if (string.IsNullOrWhiteSpace(ft)) continue;
                    if (!Enum.TryParse<VolumeCapabilities>(ft, ignoreCase: true, out var parsedFlag))
                    {
                        volumes = null;
                        error = $"Invalid volume capability '{ft}' for volume '{id}'";
                        return false;
                    }
                    flags |= parsedFlag;
                }
            }

            if (flags == VolumeCapabilities.None)
            {
                flags = VolumeCapabilities.Ingest;
            }

            list.Add(new StorageVolume(id, path, pool, flags));
        }

        if (list.Count == 0)
        {
            volumes = [StorageVolume.CreateDefault(dataRoot)];
            error = null;
            return true;
        }

        if (!list.Any(v => v.IsWritable))
        {
            volumes = null;
            error = "No writable volume configured. At least one volume must not be ReadOnly or Remote.";
            return false;
        }

        volumes = list;
        error = null;
        return true;
    }

    private static bool TryReadVolumes(
        string name,
        string dataRoot,
        [NotNullWhen(true)] out IReadOnlyList<StorageVolume>? volumes,
        [NotNullWhen(false)] out string? error) =>
        TryParseVolumes(Environment.GetEnvironmentVariable(name), dataRoot, out volumes, out error);
}
