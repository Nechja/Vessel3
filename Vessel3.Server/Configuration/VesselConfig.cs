using System.Collections.Frozen;
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
    string LogFormat = VesselDefaults.DefaultLogFormat,
    LogLevel LogLevel = LogLevel.Information,
    bool AccessLogEnabled = true,
    string? NodeId = null,
    OtelConfig? Otel = null,
    IReadOnlyList<StorageVolume>? Volumes = null,
    ClusterMembershipOptions? Cluster = null)
{
    private const string WriteProbeFileName = ".vessel3-write-test";
    private const string ListDelimiters = ",;";
    private const string LogLevelInfoShort = "info";
    private const string LogLevelWarnShort = "warn";

    public static bool TryCreate([NotNullWhen(true)] out VesselConfig? config, [NotNullWhen(false)] out string? error)
    {
        var dataRoot = ReadString(VesselEnvVars.Data, Path.Combine(AppContext.BaseDirectory, VesselDefaults.DataDirectoryName))!;
        if (!EnsureDataRootWritable(dataRoot, out error))
        {
            config = null;
            return false;
        }

        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(VesselEnvVars.XdgDataHome)))
        {
            Environment.SetEnvironmentVariable(VesselEnvVars.XdgDataHome, Path.Combine(dataRoot, VesselDefaults.XdgSubdirectory));
        }

        if (!OidcOptions.FromEnvironment().TryGetValue(out var oidc, out var oidcError))
        {
            config = null;
            error = oidcError.Message;
            return false;
        }

        var accessKey = ReadString(VesselEnvVars.AccessKey);
        var secretKey = ReadString(VesselEnvVars.SecretKey);
        var region = ReadString(VesselEnvVars.Region, VesselDefaults.DefaultRegion)!;
        var baseDomains = ReadDomains(VesselEnvVars.Domain);

        var gcMaxWaitSeconds = ReadLong(VesselEnvVars.GcMaxWaitSeconds, 120);
        var lifecycleIntervalSeconds = ReadLong(VesselEnvVars.LifecycleIntervalSeconds, 3600);
        var compactIntervalSeconds = ReadLong(VesselEnvVars.CompactIntervalSeconds, 3600);
        var compactThresholdBytes = ReadLong(VesselEnvVars.CompactThresholdBytes, 64L * 1024 * 1024);
        var slowRequestMilliseconds = ReadLong(VesselEnvVars.SlowRequestMs, 1000);

        var metricsToken = ReadString(VesselEnvVars.MetricsToken);
        var metricsAllowAnonymous = ReadBool(VesselEnvVars.MetricsAllowAnonymous);
        var adminUsers = ReadList(VesselEnvVars.AdminUsers);

        var ociEnabled = ReadFeatureFlag(VesselEnvVars.OciEnabled);
        var webDavEnabled = ReadFeatureFlag(VesselEnvVars.WebDavEnabled);

        var webhooksFile = ReadString(VesselEnvVars.WebhooksFile);
        var logFormat = ReadString(VesselEnvVars.LogFormat, VesselDefaults.DefaultLogFormat)!.ToLowerInvariant();
        var logLevel = ReadLogLevel(VesselEnvVars.LogLevel, LogLevel.Information);
        var accessLogEnabled = ReadFeatureFlag(VesselEnvVars.AccessLog);
        var nodeId = ReadString(VesselEnvVars.NodeId, Environment.MachineName)!;

        var advertiseUrl = ReadString(VesselEnvVars.AdvertiseUrl, string.Empty)!;
        var seedPeers = ParsePeerSeeds(Environment.GetEnvironmentVariable(VesselEnvVars.Peers));
        var clusterSecret = ReadString(VesselEnvVars.ClusterSecret) ?? secretKey;
        var gossipIntervalMs = ReadLong(VesselEnvVars.GossipIntervalMs, 1000);
        var peerSuspectMs = ReadLong(VesselEnvVars.PeerSuspectMs, 5000);
        var peerDeadMs = ReadLong(VesselEnvVars.PeerDeadMs, 15000);
        var cluster = new ClusterMembershipOptions(
            nodeId,
            advertiseUrl,
            seedPeers,
            TimeSpan.FromMilliseconds(gossipIntervalMs),
            TimeSpan.FromMilliseconds(peerSuspectMs),
            TimeSpan.FromMilliseconds(peerDeadMs),
            clusterSecret);

        var otelEndpoint = ReadString(VesselEnvVars.OtelExporterOtlpEndpoint);
        var otelEnabled = ReadBool(VesselEnvVars.OtelEnabled) || !string.IsNullOrWhiteSpace(otelEndpoint);
        var otelServiceName = ReadString(VesselEnvVars.OtelServiceName, VesselDefaults.ServiceName)!;
        var otel = new OtelConfig(otelEnabled, otelEndpoint, otelServiceName);

        if (!TryReadVolumes(VesselEnvVars.Volumes, dataRoot, out var volumes, out error, nodeId, cluster.IsClusterEnabled))
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
            volumes,
            cluster);

        error = null;
        return true;
    }

    private static bool EnsureDataRootWritable(string dataRoot, [NotNullWhen(false)] out string? error)
    {
        try
        {
            Directory.CreateDirectory(dataRoot);
            var probe = Path.Combine(dataRoot, WriteProbeFileName);
            File.WriteAllText(probe, string.Empty);
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
        return string.IsNullOrEmpty(value) ? defaultValue : !string.Equals(value, bool.FalseString, StringComparison.OrdinalIgnoreCase);
    }

    private static readonly FrozenDictionary<string, LogLevel> LogLevelByName =
        new Dictionary<string, LogLevel>(StringComparer.OrdinalIgnoreCase)
        {
            [nameof(LogLevel.Trace)] = LogLevel.Trace,
            [nameof(LogLevel.Debug)] = LogLevel.Debug,
            [LogLevelInfoShort] = LogLevel.Information,
            [nameof(LogLevel.Information)] = LogLevel.Information,
            [LogLevelWarnShort] = LogLevel.Warning,
            [nameof(LogLevel.Warning)] = LogLevel.Warning,
            [nameof(LogLevel.Error)] = LogLevel.Error,
            [nameof(LogLevel.Critical)] = LogLevel.Critical,
            [nameof(LogLevel.None)] = LogLevel.None,
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    private static LogLevel ReadLogLevel(string name, LogLevel fallback)
    {
        var raw = Environment.GetEnvironmentVariable(name);
        return raw is not null && LogLevelByName.TryGetValue(raw, out var level) ? level : fallback;
    }

    private static string? ReadString(string name, string? fallback = null)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrEmpty(value) ? fallback : value;
    }

    private static long ReadLong(string name, long fallback) =>
        long.TryParse(Environment.GetEnvironmentVariable(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedValue) ? parsedValue : fallback;

    private static bool ReadBool(string name) =>
        string.Equals(Environment.GetEnvironmentVariable(name), bool.TrueString, StringComparison.OrdinalIgnoreCase);

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

    internal static string[] ParsePeerSeeds(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return [];

        List<string> seeds = [];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        var span = raw.AsSpan();
        foreach (var range in span.SplitAny(ListDelimiters))
        {
            var trimmed = span[range].Trim().TrimEnd('/');
            if (trimmed.IsEmpty)
                continue;
            var url = trimmed.ToString();
            if (seen.Add(url))
                seeds.Add(url);
        }

        return [.. seeds];
    }

    private static readonly FrozenDictionary<string, VolumeCapabilities> CapabilityByName =
        new Dictionary<string, VolumeCapabilities>(StringComparer.OrdinalIgnoreCase)
        {
            [nameof(VolumeCapabilities.None)] = VolumeCapabilities.None,
            [nameof(VolumeCapabilities.Ingest)] = VolumeCapabilities.Ingest,
            [nameof(VolumeCapabilities.OnDemand)] = VolumeCapabilities.OnDemand,
            [nameof(VolumeCapabilities.ReadOnly)] = VolumeCapabilities.ReadOnly,
            [nameof(VolumeCapabilities.Mirrored)] = VolumeCapabilities.Mirrored,
            [nameof(VolumeCapabilities.Remote)] = VolumeCapabilities.Remote,
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    internal static bool TryParseVolumes(
        string? raw,
        string dataRoot,
        [NotNullWhen(true)] out IReadOnlyList<StorageVolume>? volumes,
        [NotNullWhen(false)] out string? error,
        string? nodeId = null,
        bool clusterEnabled = false)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            volumes = [StorageVolume.CreateDefault(dataRoot, nodeId, clusterEnabled)];
            error = null;
            return true;
        }

        List<StorageVolume> list = [];
        HashSet<string> seenIds = new(StringComparer.OrdinalIgnoreCase);
        var rawSpan = raw.AsSpan();
        Span<Range> tokens = stackalloc Range[5];

        foreach (var entryRange in rawSpan.SplitAny(ListDelimiters))
        {
            var partSpan = rawSpan[entryRange].Trim();
            if (partSpan.IsEmpty)
                continue;

            var tokenCount = partSpan.Split(tokens, ':', StringSplitOptions.TrimEntries);
            if (tokenCount < 2)
            {
                volumes = null;
                error = $"Invalid volume entry '{partSpan}': expected at least 'id:path'";
                return false;
            }

            var idSpan = partSpan[tokens[0]];
            var pathSpan = partSpan[tokens[1]];
            if (idSpan.IsEmpty)
            {
                volumes = null;
                error = $"Invalid volume entry '{partSpan}': volume id cannot be empty";
                return false;
            }

            if (pathSpan.IsEmpty)
            {
                volumes = null;
                error = $"Invalid volume entry '{partSpan}': volume path cannot be empty";
                return false;
            }

            var id = idSpan.ToString();
            if (!seenIds.Add(id))
            {
                volumes = null;
                error = $"Duplicate volume id '{id}'";
                return false;
            }

            var poolSpan = tokenCount > 2 ? partSpan[tokens[2]] : [];
            var pool = poolSpan.IsEmpty ? VesselDefaults.DefaultVolumePool : poolSpan.ToString();
            var flagsSpan = tokenCount > 3 ? partSpan[tokens[3]] : [];

            if (!TryParseCapabilities(flagsSpan, idSpan, out var flags, out error))
            {
                volumes = null;
                return false;
            }

            list.Add(new StorageVolume(id, pathSpan.ToString(), pool, flags, NodeId: nodeId));
        }

        if (list.Count == 0)
        {
            volumes = [StorageVolume.CreateDefault(dataRoot, nodeId, clusterEnabled)];
            error = null;
            return true;
        }

        if (!list.Any(v => v.IsLocalReplicaWritable))
        {
            volumes = null;
            error = "No writable volume configured. At least one volume must not be ReadOnly or Remote.";
            return false;
        }

        volumes = list;
        error = null;
        return true;
    }

    private static bool TryParseCapabilities(
        ReadOnlySpan<char> flagsSpan,
        ReadOnlySpan<char> volumeId,
        out VolumeCapabilities flags,
        [NotNullWhen(false)] out string? error)
    {
        flags = VolumeCapabilities.None;
        error = null;
        if (flagsSpan.IsEmpty)
        {
            flags = VolumeCapabilities.Ingest;
            return true;
        }

        var capLookup = CapabilityByName.GetAlternateLookup<ReadOnlySpan<char>>();
        Span<Range> flagRanges = stackalloc Range[8];
        var flagCount = flagsSpan.Split(flagRanges, '+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        for (var i = 0; i < flagCount; i++)
        {
            var ft = flagsSpan[flagRanges[i]];
            if (!capLookup.TryGetValue(ft, out var parsedFlag))
            {
                error = $"Invalid volume capability '{ft}' for volume '{volumeId}'";
                return false;
            }
            flags |= parsedFlag;
        }

        if (flags == VolumeCapabilities.None)
            flags = VolumeCapabilities.Ingest;

        return true;
    }

    private static bool TryReadVolumes(
        string name,
        string dataRoot,
        [NotNullWhen(true)] out IReadOnlyList<StorageVolume>? volumes,
        [NotNullWhen(false)] out string? error,
        string? nodeId = null,
        bool clusterEnabled = false) =>
        TryParseVolumes(Environment.GetEnvironmentVariable(name), dataRoot, out volumes, out error, nodeId, clusterEnabled);
}
