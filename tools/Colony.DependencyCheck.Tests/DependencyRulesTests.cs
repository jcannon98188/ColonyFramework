namespace Colony.Tools.DependencyCheck.Tests;

public sealed class DependencyRulesTests
{
    private static ProjectNode Src(string name, params string[] projectReferences) =>
        new(name, $"src/{name}/{name}.csproj", projectReferences, []);

    [Fact]
    public void Core_referencing_another_package_is_a_violation()
    {
        // Spec section 3: Core depends only on the BCL.
        var projects = new[] { Src("Colony.Core", "src/Colony.Fields/Colony.Fields.csproj"), Src("Colony.Fields") };

        var violations = DependencyRules.Check(projects);

        var violation = Assert.Single(violations);
        Assert.Equal("Colony.Core", violation.Project);
        Assert.Contains("Colony.Fields", violation.Message, StringComparison.Ordinal);
    }
}
