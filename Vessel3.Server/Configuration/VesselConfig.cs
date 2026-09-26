using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Vessel3.Server.Oidc;
using Vessel3.Server.S3;

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
    OidcOptions? Oidc)
{
    public static bool TryCreate([NotNullWhen(true)] out VesselConfig? config, [NotNullWhen(false)] out string? error)
    {
        var dataRoot = ReadString("VESSEL3_DATA", Path.Combine(AppContext.BaseDirectory, "data"))!;
        try
        {
            Directory.CreateDirectory(dataRoot);
            var probe = Path.Combine(dataRoot, ".vessel3-write-test");
            File.WriteAllText(probe, "");
            File.Delete(probe);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            config = null;
            error = $"VESSEL3_DATA ({dataRoot}) is not writable by the running user. " +
                    "On Kubernetes, set the pod securityContext.fsGroup to the runtime uid (1654) " +
                    "so the kubelet chowns the volume on mount.";
            return false;
        }

        if (!OidcOptions.FromEnvironment().TryGetValue(out var oidc, out var oidcErr))
        {
            config = null;
            error = oidcErr.Message;
            return false;
        }

        var accessKey = ReadString("VESSEL3_ACCESS_KEY");
        var secretKey = ReadString("VESSEL3_SECRET_KEY");
        var region = ReadString("VESSEL3_REGION", "us-east-1")!;
        var baseDomains = ReadDomains("VESSEL3_DOMAIN");

        var gcWaitSec = ReadLong("VESSEL3_GC_MAX_WAIT_SECONDS", 120);
        var lcSec = ReadLong("VESSEL3_LIFECYCLE_INTERVAL_SECONDS", 3600);
        var cpSec = ReadLong("VESSEL3_COMPACT_INTERVAL_SECONDS", 3600);
        var cpThreshold = ReadLong("VESSEL3_COMPACT_THRESHOLD_BYTES", 64L * 1024 * 1024);
        var slowMs = ReadLong("VESSEL3_SLOW_REQUEST_MS", 1000);

        var metricsToken = ReadString("VESSEL3_METRICS_TOKEN");
        var metricsAllowAnon = ReadBool("VESSEL3_METRICS_ALLOW_ANONYMOUS");

        config = new VesselConfig(
            dataRoot,
            accessKey,
            secretKey,
            region,
            baseDomains,
            TimeSpan.FromSeconds(gcWaitSec),
            TimeSpan.FromSeconds(lcSec),
            TimeSpan.FromSeconds(cpSec),
            cpThreshold,
            TimeSpan.FromMilliseconds(slowMs),
            metricsToken,
            metricsAllowAnon,
            oidc);

        error = null;
        return true;
    }

    private static string? ReadString(string name, string? fallback = null)
    {
        var val = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrEmpty(val) ? fallback : val;
    }

    private static long ReadLong(string name, long fallback) =>
        long.TryParse(Environment.GetEnvironmentVariable(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    private static bool ReadBool(string name) =>
        string.Equals(Environment.GetEnvironmentVariable(name), "true", StringComparison.OrdinalIgnoreCase);

    private static string[] ReadDomains(string name)
    {
        var raw = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(raw)
            ? []
            : [.. raw.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(d => VirtualHostResolver.StripPort(d).ToLowerInvariant())
                .Distinct()];
    }
}
