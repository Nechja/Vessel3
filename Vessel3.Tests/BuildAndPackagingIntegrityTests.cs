using System.Xml.Linq;
using Xunit;

namespace Vessel3.Tests;

public sealed class BuildAndPackagingIntegrityTests
{
    private static readonly string RepoRoot = FindRepoRoot();

    [Fact]
    public void Vessel3ServerCsproj_PublishUiTarget_DoesNotBreakFreshOrCiEnvironments()
    {
        var serverCsprojPath = Path.Combine(RepoRoot, "Vessel3.Server", "Vessel3.Server.csproj");
        Assert.True(File.Exists(serverCsprojPath), "Vessel3.Server.csproj must exist");

        var document = XDocument.Load(serverCsprojPath);
        var projectElement = document.Root;
        Assert.NotNull(projectElement);

        var publishUiTarget = projectElement.Descendants("Target")
            .FirstOrDefault(target => (string?)target.Attribute("Name") == "PublishUI");
        Assert.NotNull(publishUiTarget);

        var execElement = publishUiTarget.Element("Exec");
        Assert.NotNull(execElement);

        var commandString = (string?)execElement.Attribute("Command") ?? string.Empty;
        var environmentVariables = (string?)execElement.Attribute("EnvironmentVariables") ?? string.Empty;

        // Ensure we do not unconditionally force --no-build or --no-restore directly in the Command
        // because fresh CI runners have not built or restored Vessel3.UI beforehand.
        Assert.DoesNotContain("--no-build", commandString);

        // If --no-restore is in the command, it must be conditionally driven via a property,
        // never hardcoded statically into the Command attribute string.
        Assert.False(
            commandString.Contains("--no-restore", StringComparison.Ordinal) && !commandString.Contains("$(UIPublishArgs)", StringComparison.Ordinal),
            "PublishUI command must not hardcode static --no-restore; it must be conditional to allow fresh CI restores.");

        // DOTNET_CLI_HOME must not be statically overridden to a local-only folder
        Assert.DoesNotContain(".dotnet-home", environmentVariables);
    }

    [Fact]
    public void AllProjectReferences_PointToExistingProjects()
    {
        var csprojFiles = Directory.GetFiles(RepoRoot, "*.csproj", SearchOption.AllDirectories)
            .Where(file => !file.Contains(Path.Combine("bin", "")) && !file.Contains(Path.Combine("obj", "")))
            .ToList();

        Assert.NotEmpty(csprojFiles);

        foreach (var csprojFile in csprojFiles)
        {
            var document = XDocument.Load(csprojFile);
            var projectDirectory = Path.GetDirectoryName(csprojFile)!;

            var projectReferences = document.Descendants("ProjectReference")
                .Select(reference => (string?)reference.Attribute("Include"))
                .Where(includePath => !string.IsNullOrEmpty(includePath));

            foreach (var relativePath in projectReferences)
            {
                var normalizedRelativePath = relativePath!.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
                var normalizedPath = Path.GetFullPath(Path.Combine(projectDirectory, normalizedRelativePath));
                Assert.True(File.Exists(normalizedPath),
                    $"ProjectReference '{relativePath}' in '{Path.GetFileName(csprojFile)}' does not exist at '{normalizedPath}'.");
            }
        }
    }

    [Fact]
    public void SolutionFile_AllListedProjectsExist()
    {
        var solutionPath = Path.Combine(RepoRoot, "Vessel3.slnx");
        Assert.True(File.Exists(solutionPath), "Vessel3.slnx must exist");

        var document = XDocument.Load(solutionPath);
        var projectPaths = document.Descendants("Project")
            .Select(project => (string?)project.Attribute("Path"))
            .Where(path => !string.IsNullOrEmpty(path));

        foreach (var projectPath in projectPaths)
        {
            var normalizedProjectPath = projectPath!.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
            var fullPath = Path.GetFullPath(Path.Combine(RepoRoot, normalizedProjectPath));
            Assert.True(File.Exists(fullPath),
                $"Project '{projectPath}' listed in Vessel3.slnx does not exist at '{fullPath}'.");
        }
    }

    [Fact]
    public void GitHubWorkflows_DotnetTestInvocations_AreProperlyConfigured()
    {
        var workflowsDirectory = Path.Combine(RepoRoot, ".github", "workflows");
        if (!Directory.Exists(workflowsDirectory))
        {
            return;
        }

        var workflowFiles = Directory.GetFiles(workflowsDirectory, "*.yml")
            .Concat(Directory.GetFiles(workflowsDirectory, "*.yaml"));

        foreach (var file in workflowFiles)
        {
            var lines = File.ReadAllLines(file);
            for (var lineIndex = 0; lineIndex < lines.Length; lineIndex++)
            {
                var lineContent = lines[lineIndex].Trim();
                if (!lineContent.StartsWith("run:", StringComparison.Ordinal) || !lineContent.Contains("dotnet test", StringComparison.Ordinal))
                {
                    continue;
                }

                // If --filter-class is used, it must appear after '--'
                if (lineContent.Contains("--filter-class", StringComparison.Ordinal))
                {
                    var doubleDashIndex = lineContent.IndexOf(" -- ", StringComparison.Ordinal);
                    var filterClassIndex = lineContent.IndexOf("--filter-class", StringComparison.Ordinal);

                    Assert.True(doubleDashIndex != -1 && filterClassIndex > doubleDashIndex,
                        $"Workflow '{Path.GetFileName(file)}' line {lineIndex + 1} passes '--filter-class' without '--' separator: '{lineContent}'.");
                }
            }
        }
    }

    private static string FindRepoRoot()
    {
        var currentDirectory = AppContext.BaseDirectory;
        while (currentDirectory is not null && !File.Exists(Path.Combine(currentDirectory, "Vessel3.slnx")))
        {
            currentDirectory = Directory.GetParent(currentDirectory)?.FullName;
        }

        return currentDirectory ?? throw new InvalidOperationException("Could not locate repository root directory");
    }
}
