using System.Xml.Linq;
using Xunit;

namespace Vessel3.Tests;

public sealed class HexagonalArchitectureTests
{
    private static readonly string RepoRoot = FindRepoRoot();

    [Fact]
    public void Storage_ProjectReferences_OnlyDependsOnPrimitives()
    {
        var references = GetProjectReferences("Vessel3.Storage");
        Assert.Single(references);
        Assert.Equal("Vessel3.Primitives", GetProjectName(references[0]));
    }

    [Fact]
    public void Primitives_ProjectReferences_HasZeroDependencies()
    {
        var references = GetProjectReferences("Vessel3.Primitives");
        Assert.Empty(references);
    }

    [Fact]
    public void Client_ProjectReferences_OnlyDependsOnPrimitives()
    {
        var references = GetProjectReferences("Vessel3.Client");
        Assert.Single(references);
        Assert.Equal("Vessel3.Primitives", GetProjectName(references[0]));
    }

    [Fact]
    public void Protocols_ProjectReferences_DoNotReferenceEachOther()
    {
        var protocolProjects = new[]
        {
            "Vessel3.Protocols.S3",
            "Vessel3.Protocols.Azure",
            "Vessel3.Protocols.Native",
            "Vessel3.Protocols.WebDav",
            "Vessel3.Protocols.Oci"
        };

        foreach (var project in protocolProjects)
        {
            var references = GetProjectReferences(project);
            foreach (var reference in references)
            {
                var refName = GetProjectName(reference);
                Assert.False(
                    protocolProjects.Contains(refName) && !string.Equals(refName, project, StringComparison.Ordinal),
                    $"Protocol project '{project}' must not reference protocol '{refName}'.");
            }
        }
    }

    [Fact]
    public void Protocols_ProjectReferences_DoNotReferenceServer()
    {
        var protocolProjects = new[]
        {
            "Vessel3.Protocols.S3",
            "Vessel3.Protocols.Azure",
            "Vessel3.Protocols.Native",
            "Vessel3.Protocols.WebDav",
            "Vessel3.Protocols.Oci"
        };

        foreach (var project in protocolProjects)
        {
            var references = GetProjectReferences(project);
            Assert.DoesNotContain(references, r => string.Equals(GetProjectName(r), "Vessel3.Server", StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void Storage_ProjectReferences_DoNotReferenceProtocolsOrServer()
    {
        var references = GetProjectReferences("Vessel3.Storage");
        Assert.DoesNotContain(references, r => GetProjectName(r).StartsWith("Vessel3.Protocols", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(references, r => string.Equals(GetProjectName(r), "Vessel3.Server", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Operator_ProjectReferences_OnlyDependsOnPrimitivesAndClient()
    {
        var references = GetProjectReferences("Vessel3.Operator");
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Vessel3.Primitives", "Vessel3.Client" };
        foreach (var reference in references)
        {
            var refName = GetProjectName(reference);
            Assert.Contains(refName, allowed);
        }
    }

    private static string GetProjectName(string reference) =>
        Path.GetFileNameWithoutExtension(reference.Replace('\\', '/'));

    private static IReadOnlyList<string> GetProjectReferences(string projectName)
    {
        var csprojPath = Path.Combine(RepoRoot, projectName, $"{projectName}.csproj");
        Assert.True(File.Exists(csprojPath), $"Project file '{csprojPath}' must exist.");

        var document = XDocument.Load(csprojPath);
        return document.Descendants("ProjectReference")
            .Select(element => (string?)element.Attribute("Include"))
            .Where(includePath => !string.IsNullOrEmpty(includePath))
            .Select(includePath => includePath!)
            .ToList();
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
