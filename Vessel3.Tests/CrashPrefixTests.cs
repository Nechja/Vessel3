using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Vessel3.Server;
using Vessel3.Server.Storage;
using Xunit;

namespace Vessel3.Tests;

public sealed class CrashPrefixTests(ITestOutputHelper output) : IDisposable
{
    private readonly string sandbox = CreateSandbox();
    private readonly IFileSync sync = new PortableFileSync();
    private readonly IDurableWrite durable = new DurableWrite(new PortableFileSync());

    private static string CreateSandbox()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"vessel3-crashprefix-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    public void Dispose()
    {
        try { Directory.Delete(sandbox, recursive: true); } catch { }
    }

    public static TheoryData<string> Scenarios =>
    [
        "unversioned-overwrite",
        "suspended-overwrite",
        "mixed-suspended-overwrite",
        "versioned-overwrite",
        "unversioned-delete",
        "suspended-delete",
        "versioned-delete",
        "unversioned-expire",
        "suspended-expire",
        "versioned-expire",
    ];

    [Theory]
    [MemberData(nameof(Scenarios))]
    public void Acked_values_survive_every_crash_prefix(string scenario)
    {
        var clock = Stopwatch.StartNew();
        var run = BuildScenario(scenario);
        var buildMs = clock.ElapsedMilliseconds;

        var violations = new List<Violation>();
        var cases = 0;

        for (var durableRecords = 0; durableRecords <= run.RecordEnds.Count; durableRecords++)
        {
            var wholeBytes = durableRecords is 0 ? 0 : run.RecordEnds[durableRecords - 1];
            cases++;
            violations.AddRange(Inspect(run, wholeBytes, durableRecords, torn: false));

            if (durableRecords == run.RecordEnds.Count) continue;
            var tornBytes = wholeBytes + (run.RecordEnds[durableRecords] - wholeBytes) / 2;
            if (tornBytes <= wholeBytes) continue;
            cases++;
            violations.AddRange(Inspect(run, tornBytes, durableRecords, torn: true));
        }

        output.WriteLine($"SCENARIO {scenario} records={run.RecordEnds.Count} keys={run.Keys.Count} cases={cases} violations={violations.Count} buildMs={buildMs} totalMs={clock.ElapsedMilliseconds}");
        foreach (var step in run.Steps)
            output.WriteLine($"  STEP {step.Description} records[{step.RecordsBefore}..{step.RecordsAfter})");
        var records = ReadRecords(run.Root);
        for (var i = 0; i < records.Count; i++)
            output.WriteLine($"  RECORD {i} {Summarise(records[i])}");
        foreach (var v in violations)
            output.WriteLine($"  VIOLATION {v.Class} durableRecords={v.DurableRecords} torn={v.Torn} key={v.Key} observed={v.Observed} allowed=[{string.Join('|', v.Allowed)}]");

        Assert.Empty(violations);
    }

    private IEnumerable<Violation> Inspect(ScenarioRun run, long logBytes, int durableRecords, bool torn)
    {
        var observed = Materialise(run, logBytes);
        var allowed = AllowedStates(run, durableRecords);

        foreach (var key in run.Keys)
        {
            var allowedForKey = allowed.Select(state => state.GetValueOrDefault(key)).ToHashSet();
            var seen = observed[key];
            if (allowedForKey.Contains(seen)) continue;

            var classification = seen is null
                ? "VANISHED"
                : run.EverAcked(key, seen) ? "STALE" : "UNEXPECTED";

            yield return new Violation(
                classification, durableRecords, torn, key,
                run.Label(seen),
                [.. allowedForKey.Select(run.Label)]);
        }
    }

    private Dictionary<string, string?> Materialise(ScenarioRun run, long logBytes)
    {
        var replica = Path.Combine(sandbox, $"replica-{Guid.NewGuid():N}");
        CopyTree(run.Root, replica);

        using (var log = new FileStream(Path.Combine(replica, "log"), FileMode.Open, FileAccess.Write, FileShare.None))
            log.SetLength(logBytes);

        foreach (var indexFile in Directory.GetFiles(replica, "index*"))
            File.Delete(indexFile);

        var observed = new Dictionary<string, string?>();
        using (var rebuilt = new Bucket("b", replica, sync, durable))
        {
            rebuilt.Open();
            foreach (var key in run.Keys)
                observed[key] = CurrentSha(rebuilt, key);
        }

        Directory.Delete(replica, recursive: true);
        return observed;
    }

    private static List<Dictionary<string, string?>> AllowedStates(ScenarioRun run, int durableRecords)
    {
        var settled = run.Keys.ToDictionary(k => k, _ => (string?)null);
        foreach (var step in run.Steps)
            if (step.RecordsAfter <= durableRecords) settled = step.Expected;

        var allowed = new List<Dictionary<string, string?>> { settled };
        foreach (var step in run.Steps)
            if (step.RecordsBefore < durableRecords && durableRecords < step.RecordsAfter)
                allowed.Add(step.Expected);
        return allowed;
    }

    private ScenarioRun BuildScenario(string scenario)
    {
        var root = Path.Combine(sandbox, scenario);
        Directory.CreateDirectory(root);

        using (var bucket = new Bucket("b", root, sync, durable))
        {
            bucket.Open();
            var recorder = new Recorder(bucket, root);

            switch (scenario)
            {
                case "unversioned-overwrite":
                    recorder.Put("k", "v1");
                    recorder.Put("k", "v2");
                    recorder.Put("k", "v3");
                    break;

                case "suspended-overwrite":
                    bucket.SetVersioning(VersioningStatus.Enabled);
                    bucket.SetVersioning(VersioningStatus.Suspended);
                    recorder.Put("k", "v1");
                    recorder.Put("k", "v2");
                    recorder.Put("k", "v3");
                    break;

                case "mixed-suspended-overwrite":
                    bucket.SetVersioning(VersioningStatus.Enabled);
                    recorder.Put("k", "v1");
                    bucket.SetVersioning(VersioningStatus.Suspended);
                    recorder.Put("k", "v2");
                    recorder.Put("k", "v3");
                    break;

                case "versioned-overwrite":
                    bucket.SetVersioning(VersioningStatus.Enabled);
                    recorder.Put("k", "v1");
                    recorder.Put("k", "v2");
                    recorder.Put("k", "v3");
                    break;

                case "unversioned-delete":
                    recorder.Put("k", "v1");
                    recorder.Put("k", "v2");
                    recorder.Delete("k");
                    break;

                case "suspended-delete":
                    bucket.SetVersioning(VersioningStatus.Enabled);
                    bucket.SetVersioning(VersioningStatus.Suspended);
                    recorder.Put("k", "v1");
                    recorder.Put("k", "v2");
                    recorder.Delete("k");
                    break;

                case "versioned-delete":
                    bucket.SetVersioning(VersioningStatus.Enabled);
                    recorder.Put("k", "v1");
                    recorder.Put("k", "v2");
                    recorder.Delete("k");
                    break;

                case "unversioned-expire":
                    recorder.Put("k", "v1");
                    recorder.Put("k", "v2");
                    recorder.Expire("k");
                    break;

                case "suspended-expire":
                    bucket.SetVersioning(VersioningStatus.Enabled);
                    bucket.SetVersioning(VersioningStatus.Suspended);
                    recorder.Put("k", "v1");
                    recorder.Put("k", "v2");
                    recorder.Expire("k");
                    break;

                case "versioned-expire":
                    bucket.SetVersioning(VersioningStatus.Enabled);
                    recorder.Put("k", "v1");
                    recorder.Put("k", "v2");
                    recorder.Expire("k");
                    break;

                default:
                    throw new ArgumentOutOfRangeException(nameof(scenario), scenario, "unknown scenario");
            }

            return new ScenarioRun(scenario, root, recorder.Steps, recorder.Keys, recorder.Labels, RecordEnds(root));
        }
    }

    private sealed class Recorder(Bucket bucket, string root)
    {
        private readonly Dictionary<string, string?> live = [];
        private readonly List<string> keys = [];

        public List<AckedStep> Steps { get; } = [];
        public Dictionary<string, string> Labels { get; } = [];
        public IReadOnlyList<string> Keys => keys;

        public void Put(string key, string body)
        {
            Track(key);
            var sha = Sha(body);
            Labels[sha] = body;
            Record($"put {key}={body}", () =>
            {
                bucket.AppendPut(key, Request(body));
                live[key] = sha;
            });
        }

        public void Delete(string key)
        {
            Track(key);
            Record($"delete {key}", () =>
            {
                var outcome = bucket.AppendDelete(key, bypassGovernance: false);
                Assert.IsType<Result<DeleteOutcome>.Success>(outcome);
                live[key] = null;
            });
        }

        public void Expire(string key)
        {
            Track(key);
            var current = ((Result<PutEntry?>.Success)bucket.Index.GetCurrentPut(key)).Value!;
            Record($"expire {key}", () =>
            {
                Assert.True(bucket.ExpireCurrentVersion(key, current.VersionId, current.At));
                live[key] = null;
            });
        }

        private void Track(string key)
        {
            if (keys.Contains(key)) return;
            keys.Add(key);
            live[key] = null;
        }

        private void Record(string description, Action operation)
        {
            var before = RecordEnds(root).Count;
            operation();
            var after = RecordEnds(root).Count;
            Assert.True(after > before, $"{description} wrote no log record");

            foreach (var key in keys)
                Assert.Equal(live[key], CurrentSha(bucket, key));

            Steps.Add(new AckedStep(description, before, after, new Dictionary<string, string?>(live)));
        }
    }

    private sealed record AckedStep(
        string Description,
        int RecordsBefore,
        int RecordsAfter,
        Dictionary<string, string?> Expected);

    private sealed record Violation(
        string Class,
        int DurableRecords,
        bool Torn,
        string Key,
        string Observed,
        IReadOnlyList<string> Allowed);

    private sealed record ScenarioRun(
        string Name,
        string Root,
        IReadOnlyList<AckedStep> Steps,
        IReadOnlyList<string> Keys,
        IReadOnlyDictionary<string, string> Labels,
        IReadOnlyList<long> RecordEnds)
    {
        public string Label(string? sha) => sha is null ? "<absent>" : Labels.GetValueOrDefault(sha, sha);

        public bool EverAcked(string key, string sha) =>
            Steps.Any(s => s.Expected.GetValueOrDefault(key) == sha);
    }

    private static string? CurrentSha(Bucket bucket, string key) =>
        bucket.Index.GetCurrentPut(key) is Result<PutEntry?>.Success { Value: { } put } ? put.BlobSha : null;

    private static List<long> RecordEnds(string root)
    {
        var path = Path.Combine(root, "log");
        if (!File.Exists(path)) return [];

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var ends = new List<long>();
        var buffer = new byte[8192];
        long position = 0;
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            for (var i = 0; i < read; i++)
                if (buffer[i] is (byte)'\n') ends.Add(position + i + 1);
            position += read;
        }
        return ends;
    }

    private static List<string> ReadRecords(string root)
    {
        var path = Path.Combine(root, "log");
        if (!File.Exists(path)) return [];

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        var records = new List<string>();
        while (reader.ReadLine() is { } line)
            if (line.Length > 0) records.Add(line);
        return records;
    }

    private static string Summarise(string record)
    {
        const int frameHeaderLength = 21;
        var payload = record.StartsWith("v1 ", StringComparison.Ordinal) ? record[frameHeaderLength..] : record;
        using var doc = JsonDocument.Parse(payload);
        var root = doc.RootElement;
        if (!root.TryGetProperty("Ops", out var ops)) return Describe(root);
        return string.Join(" + ", ops.EnumerateArray().Select(Describe));
    }

    private static string Describe(JsonElement op) =>
        $"{op.GetProperty("kind").GetString()} seq={op.GetProperty("Seq").GetInt64()} key={op.GetProperty("Key").GetString()} version={op.GetProperty("VersionId").GetString()}";

    private static void CopyTree(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.GetFiles(source))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
        foreach (var dir in Directory.GetDirectories(source))
            CopyTree(dir, Path.Combine(destination, Path.GetFileName(dir)));
    }

    private static string Sha(string body) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(body)));

    private static PutRequest Request(string body) => new(
        BlobSha: Sha(body),
        Md5: Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(body))),
        Size: body.Length,
        ContentType: "text/plain",
        Metadata: new Dictionary<string, string>());
}
