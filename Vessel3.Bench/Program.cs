using System.Globalization;
using Amazon.Runtime;
using Amazon.S3;
using Vessel3.Bench;

if (args.Length is 0 || args[0] is "-h" or "--help")
{
    PrintUsage();
    return 0;
}

var endpoint  = Environment.GetEnvironmentVariable("VESSEL3_ENDPOINT") ?? "http://127.0.0.1:9000";
var accessKey = Environment.GetEnvironmentVariable("VESSEL3_ACCESS_KEY") ?? "AKIATEST";
var secretKey = Environment.GetEnvironmentVariable("VESSEL3_SECRET_KEY") ?? "secretkey1234567890";
var region    = Environment.GetEnvironmentVariable("VESSEL3_REGION")     ?? "us-east-1";

var config = new AmazonS3Config
{
    ServiceURL = endpoint,
    ForcePathStyle = true,
    AuthenticationRegion = region,
    UseHttp = endpoint.StartsWith("http://", StringComparison.Ordinal),
};
using var s3 = new AmazonS3Client(new BasicAWSCredentials(accessKey, secretKey), config);

var scenario = args[0];
var opts = ParseOptions(args[1..]);
var json = args.Contains("--json");

var summary = scenario switch
{
    "put-small" => await Scenarios.PutSmall(s3, opts),
    "put-large" => await Scenarios.PutLarge(s3, opts),
    "get"       => await Scenarios.Get(s3, opts),
    "multipart" => await Scenarios.Multipart(s3, opts),
    "mixed"     => await Scenarios.Mixed(s3, opts),
    _           => throw new ArgumentException($"unknown scenario: {scenario}"),
};

if (json) PrintJson(scenario, opts, summary);
else PrintTable(scenario, opts, summary);
return 0;

static BenchOptions ParseOptions(string[] rest)
{
    var bucket = "vessel3-bench";
    var concurrency = 8;
    var duration = TimeSpan.FromSeconds(10);
    var warmup = TimeSpan.FromSeconds(1);
    var objectSize = 0;
    var seedKeys = 100;

    for (var i = 0; i < rest.Length; i++)
    {
        switch (rest[i])
        {
            case "--bucket": bucket = rest[++i]; break;
            case "--concurrency": concurrency = int.Parse(rest[++i], CultureInfo.InvariantCulture); break;
            case "--duration": duration = ParseDuration(rest[++i]); break;
            case "--warmup": warmup = ParseDuration(rest[++i]); break;
            case "--object-size": objectSize = ParseSize(rest[++i]); break;
            case "--seed-keys": seedKeys = int.Parse(rest[++i], CultureInfo.InvariantCulture); break;
            case "--json": break;
            default: throw new ArgumentException($"unknown flag: {rest[i]}");
        }
    }

    return new BenchOptions(bucket, concurrency, duration, warmup, objectSize, seedKeys);
}

static TimeSpan ParseDuration(string raw) => raw switch
{
    _ when raw.EndsWith("ms", StringComparison.Ordinal) => TimeSpan.FromMilliseconds(long.Parse(raw[..^2], CultureInfo.InvariantCulture)),
    _ when raw.EndsWith('s') => TimeSpan.FromSeconds(double.Parse(raw[..^1], CultureInfo.InvariantCulture)),
    _ when raw.EndsWith('m') => TimeSpan.FromMinutes(double.Parse(raw[..^1], CultureInfo.InvariantCulture)),
    _ => TimeSpan.FromSeconds(double.Parse(raw, CultureInfo.InvariantCulture)),
};

static int ParseSize(string raw) => raw switch
{
    _ when raw.EndsWith("kb", StringComparison.OrdinalIgnoreCase) => int.Parse(raw[..^2], CultureInfo.InvariantCulture) * 1024,
    _ when raw.EndsWith("mb", StringComparison.OrdinalIgnoreCase) => int.Parse(raw[..^2], CultureInfo.InvariantCulture) * 1024 * 1024,
    _ when raw.EndsWith('b') => int.Parse(raw[..^1], CultureInfo.InvariantCulture),
    _ => int.Parse(raw, CultureInfo.InvariantCulture),
};

static void PrintTable(string scenario, BenchOptions opts, LatencySummary s)
{
    Console.WriteLine($"== {scenario} ==");
    Console.WriteLine($"  endpoint:    {Environment.GetEnvironmentVariable("VESSEL3_ENDPOINT") ?? "http://127.0.0.1:9000"}");
    Console.WriteLine($"  bucket:      {opts.Bucket}");
    Console.WriteLine($"  concurrency: {opts.Concurrency}");
    Console.WriteLine($"  duration:    {s.WallTime.TotalSeconds:F2}s (warmup {opts.Warmup.TotalSeconds:F2}s)");
    Console.WriteLine($"  ops:         {s.Ops:N0}  ({s.OpsPerSecond,9:N1} ops/s)");
    Console.WriteLine($"  bytes:       {FormatBytes(s.Bytes)}  ({FormatBytes((long)s.BytesPerSecond)}/s)");
    Console.WriteLine($"  latency ms:  avg={s.AvgMs:F2}  p50={s.P50Ms:F2}  p95={s.P95Ms:F2}  p99={s.P99Ms:F2}  p999={s.P999Ms:F2}  max={s.MaxMs:F2}");
}

static void PrintJson(string scenario, BenchOptions opts, LatencySummary s)
{
    var payload = new Dictionary<string, object?>
    {
        ["scenario"] = scenario,
        ["bucket"] = opts.Bucket,
        ["concurrency"] = opts.Concurrency,
        ["duration_s"] = Math.Round(s.WallTime.TotalSeconds, 3),
        ["warmup_s"] = Math.Round(opts.Warmup.TotalSeconds, 3),
        ["object_size"] = opts.ObjectSize,
        ["ops"] = s.Ops,
        ["ops_per_s"] = Math.Round(s.OpsPerSecond, 1),
        ["bytes"] = s.Bytes,
        ["bytes_per_s"] = (long)s.BytesPerSecond,
        ["latency_ms"] = new Dictionary<string, double>
        {
            ["avg"] = Math.Round(s.AvgMs, 3),
            ["p50"] = Math.Round(s.P50Ms, 3),
            ["p95"] = Math.Round(s.P95Ms, 3),
            ["p99"] = Math.Round(s.P99Ms, 3),
            ["p999"] = Math.Round(s.P999Ms, 3),
            ["max"] = Math.Round(s.MaxMs, 3),
        },
    };
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(payload, new System.Text.Json.JsonSerializerOptions { WriteIndented = false }));
}

static string FormatBytes(long bytes) =>
    bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):F2} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):F2} MB",
        >= 1L << 10 => $"{bytes / (double)(1L << 10):F2} KB",
        _ => $"{bytes} B",
    };

static void PrintUsage()
{
    Console.WriteLine("Usage: vessel3-bench <scenario> [flags]");
    Console.WriteLine();
    Console.WriteLine("Scenarios:");
    Console.WriteLine("  put-small   1 KB PUT (override with --object-size)");
    Console.WriteLine("  put-large   10 MB PUT (override with --object-size)");
    Console.WriteLine("  get         GET against pre-seeded bucket");
    Console.WriteLine("  multipart   3-part 5 MB multipart upload");
    Console.WriteLine("  mixed       70% GET / 30% PUT");
    Console.WriteLine();
    Console.WriteLine("Flags:");
    Console.WriteLine("  --bucket NAME           default vessel3-bench");
    Console.WriteLine("  --concurrency N         default 8");
    Console.WriteLine("  --duration 30s          default 10s (s, m, ms suffixes)");
    Console.WriteLine("  --warmup 2s             default 1s");
    Console.WriteLine("  --object-size 4kb       default per scenario (b, kb, mb suffixes)");
    Console.WriteLine("  --seed-keys 100         default 100 (for get/mixed)");
    Console.WriteLine("  --json                  emit JSON instead of table");
    Console.WriteLine();
    Console.WriteLine("Env: VESSEL3_ENDPOINT, VESSEL3_ACCESS_KEY, VESSEL3_SECRET_KEY, VESSEL3_REGION");
}
