using System.Reflection;
using System.Text;
using Xunit;

namespace Vessel3.Tests;

public sealed class TestNamingConventionTests
{
    [Fact]
    public void AllTests_FollowNamingConvention()
    {
        var assembly = typeof(TestNamingConventionTests).Assembly;
        var testTypes = assembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract)
            .Where(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
                .Any(m => m.GetCustomAttributes().Any(a => a.GetType().Name is "FactAttribute" or "TheoryAttribute")))
            .ToList();

        var violations = new List<string>();

        foreach (var type in testTypes)
        {
            if (!type.Name.EndsWith("Tests", StringComparison.Ordinal))
            {
                violations.Add($"Test class '{type.FullName}' must end with 'Tests'.");
            }

            var testMethods = type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
                .Where(m => m.GetCustomAttributes().Any(a => a.GetType().Name is "FactAttribute" or "TheoryAttribute"));

            foreach (var method in testMethods)
            {
                var name = method.Name;
                var parts = name.Split('_');

                if (parts.Length is not (2 or 3))
                {
                    violations.Add($"{type.Name}.{name}: expected 2 or 3 segments separated by '_' (found {parts.Length}). Pattern: [UnitOrFeature]_[StateUnderTest]_[ExpectedOutcome] or [UnitOrFeature]_[ExpectedOutcome].");
                    continue;
                }

                for (var i = 0; i < parts.Length; i++)
                {
                    var part = parts[i];
                    if (string.IsNullOrEmpty(part))
                    {
                        violations.Add($"{type.Name}.{name}: segment {i + 1} is empty (consecutive underscores).");
                    }
                    else if (!char.IsAsciiLetterUpper(part[0]))
                    {
                        violations.Add($"{type.Name}.{name}: segment '{part}' must start with an uppercase ASCII letter (PascalCase).");
                    }
                    else if (part.Any(c => !char.IsAsciiLetterOrDigit(c)))
                    {
                        violations.Add($"{type.Name}.{name}: segment '{part}' must contain only alphanumeric ASCII characters.");
                    }
                }
            }
        }

        if (violations.Count > 0)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Found {violations.Count} test naming policy violation(s):");
            foreach (var v in violations)
            {
                sb.AppendLine($"  - {v}");
            }
            Assert.Fail(sb.ToString());
        }
    }
}
