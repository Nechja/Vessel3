using System.Diagnostics;
using Xunit;

namespace Vessel3.Tests;

public sealed class KubernetesManifestAndChartTests
{
    private static readonly string RepoRoot = FindRepoRoot();

    [Fact]
    public void CustomResourceDefinitions_ExistAndContainCorrectSpecs()
    {
        string[] crdFiles =
        [
            "vessel.nechja.io_vesselservers.yaml",
            "vessel.nechja.io_vesselbuckets.yaml",
            "vessel.nechja.io_vesselusers.yaml",
            "vessel.nechja.io_vesselwebhooks.yaml"
        ];

        foreach (var file in crdFiles)
        {
            var deployPath = Path.Combine(RepoRoot, "deploy", "crds", file);
            var chartPath = Path.Combine(RepoRoot, "charts", "vessel3-operator", "crds", file);

            Assert.True(File.Exists(deployPath), $"Missing CRD in deploy/crds: {file}");
            Assert.True(File.Exists(chartPath), $"Missing CRD in chart crds: {file}");

            var deployContent = File.ReadAllText(deployPath);
            var chartContent = File.ReadAllText(chartPath);

            Assert.Equal(deployContent, chartContent);
            Assert.Contains("group: vessel.nechja.io", deployContent);
            Assert.Contains("name: v1alpha1", deployContent);
        }
    }

    [Fact]
    public void HelmCharts_ContainValidMetadata()
    {
        var serverChartPath = Path.Combine(RepoRoot, "charts", "vessel3", "Chart.yaml");
        var operatorChartPath = Path.Combine(RepoRoot, "charts", "vessel3-operator", "Chart.yaml");

        Assert.True(File.Exists(serverChartPath));
        Assert.True(File.Exists(operatorChartPath));

        var serverChart = File.ReadAllText(serverChartPath);
        var operatorChart = File.ReadAllText(operatorChartPath);

        Assert.Contains("apiVersion: v2", serverChart);
        Assert.Contains("name: vessel3", serverChart);

        Assert.Contains("apiVersion: v2", operatorChart);
        Assert.Contains("name: vessel3-operator", operatorChart);
    }

    [Fact]
    public void ServerChartValues_ConfiguresProbeRoutesWithoutMagicStrings()
    {
        var valuesPath = Path.Combine(RepoRoot, "charts", "vessel3", "values.yaml");
        Assert.True(File.Exists(valuesPath));

        var content = File.ReadAllText(valuesPath);

        Assert.Contains($"path: {WellKnownRoutes.Healthz}", content);
        Assert.Contains($"path: {WellKnownRoutes.Readyz}", content);
        Assert.Contains("port: 9000", content);
    }

    [Fact]
    public void AllHelmTemplates_HaveBalancedDelimiters()
    {
        string[] templateFiles = [.. Directory.GetFiles(Path.Combine(RepoRoot, "charts"), "*.*", SearchOption.AllDirectories)
            .Where(f => f.Contains(Path.Combine("templates", "")))];

        Assert.NotEmpty(templateFiles);

        foreach (var file in templateFiles)
        {
            var content = File.ReadAllText(file);
            var opens = CountOccurrences(content, "{{");
            var closes = CountOccurrences(content, "}}");

            Assert.True(opens == closes, $"Template delimiters unbalanced in {Path.GetFileName(file)}: {opens} opens vs {closes} closes");
        }
    }

    [Fact]
    public void GitOpsExamples_ExistAndAreConfiguredProperly()
    {
        var argoPath = Path.Combine(RepoRoot, "examples", "gitops", "argocd-vessel3.yaml");
        var telemetryPath = Path.Combine(RepoRoot, "examples", "gitops", "monitoring-stack-buckets.yaml");

        Assert.True(File.Exists(argoPath));
        Assert.True(File.Exists(telemetryPath));

        var argoContent = File.ReadAllText(argoPath);
        Assert.Contains("kind: Application", argoContent);
        Assert.Contains("namespace: storage", argoContent);

        var telemetryContent = File.ReadAllText(telemetryPath);
        Assert.Contains("kind: VesselServer", telemetryContent);
        Assert.Contains("kind: VesselBucket", telemetryContent);
        Assert.Contains("name: mimir-blocks", telemetryContent);
        Assert.Contains("name: tempo-traces", telemetryContent);
        Assert.Contains("name: loki-chunks", telemetryContent);
        Assert.Contains("kind: VesselUser", telemetryContent);
    }

    [Fact]
    public void AllStaticYamlFiles_AreValidAndParseable()
    {
        string[] searchDirs = ["charts", "deploy", "examples"];
        List<string> yamlFiles = [];

        foreach (var dir in searchDirs)
        {
            var fullDir = Path.Combine(RepoRoot, dir);
            if (!Directory.Exists(fullDir))
            {
                continue;
            }

            var files = Directory.GetFiles(fullDir, "*.yaml", SearchOption.AllDirectories)
                .Concat(Directory.GetFiles(fullDir, "*.yml", SearchOption.AllDirectories))
                .Where(f => !f.Contains(Path.Combine("templates", "")));

            yamlFiles.AddRange(files);
        }

        Assert.NotEmpty(yamlFiles);

        foreach (var file in yamlFiles)
        {
            var content = File.ReadAllText(file);
            using var reader = new StringReader(content);
            var yaml = new YamlDotNet.RepresentationModel.YamlStream();
            yaml.Load(reader);
            Assert.NotEmpty(yaml.Documents);
        }
    }

    [Fact]
    public void HelmCli_LintsAndTemplatesSuccessfully_WhenHelmIsAvailable()
    {
        if (!IsCommandAvailable("helm"))
        {
            return;
        }

        string[] chartDirs =
        [
            Path.Combine(RepoRoot, "charts", "vessel3"),
            Path.Combine(RepoRoot, "charts", "vessel3-operator")
        ];

        foreach (var chart in chartDirs)
        {
            var lintResult = ExecuteProcess("helm", ["lint", chart]);
            Assert.True(lintResult.ExitCode == 0, $"helm lint failed for {chart}: {lintResult.Output}");

            var templateResult = ExecuteProcess("helm", ["template", "test-release", chart]);
            Assert.True(templateResult.ExitCode == 0, $"helm template failed for {chart}: {templateResult.Output}");
        }
    }

    private static bool IsCommandAvailable(string command)
    {
        try
        {
            var result = ExecuteProcess("which", [command]);
            return result.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static (int ExitCode, string Output) ExecuteProcess(string fileName, IReadOnlyList<string> arguments)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        foreach (var arg in arguments)
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = Process.Start(psi);
        if (process is null)
        {
            return (-1, "Failed to start process");
        }

        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit(10000);
        return (process.ExitCode, output);
    }

    private static int CountOccurrences(string text, string pattern)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(pattern, index, StringComparison.Ordinal)) != -1)
        {
            count++;
            index += pattern.Length;
        }
        return count;
    }

    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "Vessel3.slnx")))
        {
            dir = Directory.GetParent(dir)?.FullName;
        }
        return dir ?? throw new InvalidOperationException("Could not locate repository root directory");
    }
}
