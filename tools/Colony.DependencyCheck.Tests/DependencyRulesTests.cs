namespace Colony.Tools.DependencyCheck.Tests;

public sealed class DependencyRulesTests
{
    private static ProjectNode Src(string name, params string[] projectReferences) =>
        new(name, $"src/{name}/{name}.csproj", projectReferences, []);

    private static string SrcPath(string name) => $"src/{name}/{name}.csproj";

    [Fact]
    public void Core_referencing_another_package_is_a_violation()
    {
        // Spec section 3: Core depends only on the BCL.
        var projects = new[] { Src("Colony.Core", SrcPath("Colony.Fields")), Src("Colony.Fields") };

        var violations = DependencyRules.Check(projects);

        var violation = Assert.Single(violations);
        Assert.Equal("Colony.Core", violation.Project);
        Assert.Contains("Colony.Fields", violation.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Core_with_a_runtime_package_reference_is_a_violation()
    {
        // "Depends only on the BCL" covers NuGet packages too. Build-only analyzers (PrivateAssets=all) are fine.
        var core = new ProjectNode(
            "Colony.Core",
            SrcPath("Colony.Core"),
            [],
            [new PackageReference("System.Collections.Immutable", ""), new PackageReference("SomeAnalyzer", "all")]);

        var violation = Assert.Single(DependencyRules.Check([core]));
        Assert.Contains("System.Collections.Immutable", violation.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Colony.Fields")]
    [InlineData("Colony.Persistence")]
    [InlineData("Colony.Plugins")]
    public void Fields_Persistence_and_Plugins_may_reference_Core(string package)
    {
        var projects = new[] { Src("Colony.Core"), Src(package, SrcPath("Colony.Core")) };

        Assert.Empty(DependencyRules.Check(projects));
    }

    [Fact]
    public void Fields_referencing_Persistence_is_a_violation()
    {
        var projects = new[] { Src("Colony.Core"), Src("Colony.Persistence", SrcPath("Colony.Core")), Src("Colony.Fields", SrcPath("Colony.Persistence")) };

        var violation = Assert.Single(DependencyRules.Check(projects));
        Assert.Equal("Colony.Fields", violation.Project);
    }

    [Theory]
    [InlineData("Colony.Pathfinding")]
    [InlineData("Colony.Colony")]
    [InlineData("Colony.ProcGen")]
    public void Building_block_packages_may_reference_Core_Fields_and_Persistence(string package)
    {
        var projects = new[]
        {
            Src("Colony.Core"),
            Src("Colony.Fields", SrcPath("Colony.Core")),
            Src("Colony.Persistence", SrcPath("Colony.Core")),
            Src(package, SrcPath("Colony.Core"), SrcPath("Colony.Fields"), SrcPath("Colony.Persistence")),
        };

        Assert.Empty(DependencyRules.Check(projects));
    }

    [Fact]
    public void Colony_may_reference_Pathfinding_for_its_reachability_filters()
    {
        // ADR 0004 (TASK-41): the work index and hauling matcher ship filters over Pathfinding.Reachable.
        var projects = new[]
        {
            Src("Colony.Core"),
            Src("Colony.Fields", SrcPath("Colony.Core")),
            Src("Colony.Pathfinding", SrcPath("Colony.Core"), SrcPath("Colony.Fields")),
            Src("Colony.Colony", SrcPath("Colony.Core"), SrcPath("Colony.Fields"), SrcPath("Colony.Pathfinding")),
        };

        Assert.Empty(DependencyRules.Check(projects));
    }

    [Fact]
    public void Pathfinding_referencing_Colony_is_a_violation()
    {
        // ADR 0004: the edge points one way only; Pathfinding knows nothing of work items or hauling.
        var projects = new[]
        {
            Src("Colony.Core"),
            Src("Colony.Fields", SrcPath("Colony.Core")),
            Src("Colony.Colony", SrcPath("Colony.Core"), SrcPath("Colony.Fields")),
            Src("Colony.Pathfinding", SrcPath("Colony.Core"), SrcPath("Colony.Fields"), SrcPath("Colony.Colony")),
        };

        var violation = Assert.Single(DependencyRules.Check(projects));
        Assert.Equal("Colony.Pathfinding", violation.Project);
        Assert.Contains("Colony.Colony", violation.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Diagnostics_may_reference_Core_Fields_and_Persistence_and_nothing_else()
    {
        // TASK-29 / spec section 3: the inspection server reads Fields and the replay log; Plugin inspectors reach it through Core's schema.
        var projects = new[]
        {
            Src("Colony.Core"),
            Src("Colony.Fields", SrcPath("Colony.Core")),
            Src("Colony.Persistence", SrcPath("Colony.Core")),
            Src("Colony.Abstractions"),
            Src("Colony.Diagnostics", SrcPath("Colony.Core"), SrcPath("Colony.Fields"), SrcPath("Colony.Persistence"), SrcPath("Colony.Abstractions")),
        };

        var violation = Assert.Single(DependencyRules.Check(projects));
        Assert.Equal("Colony.Diagnostics", violation.Project);
        Assert.Contains("Colony.Abstractions", violation.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Colony.Diagnostics")]
    [InlineData("Colony.Testing")]
    [InlineData("Colony.Plugins")]
    public void No_package_may_reference_Diagnostics_Testing_or_Plugins(string forbidden)
    {
        // Colony.ProcGen's allow list permits Core only, so the global rule and the allow list both object; one violation per reference.
        var projects = new[] { Src(forbidden), Src("Colony.ProcGen", SrcPath(forbidden)) };

        var violation = Assert.Single(DependencyRules.Check(projects));
        Assert.Equal("Colony.ProcGen", violation.Project);
        Assert.Contains(forbidden, violation.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Colony.Core", "Colony.Plugins")]
    [InlineData("Colony.Core", "Colony.Abstractions")]
    [InlineData("Colony.Fields", "Colony.Plugins")]
    [InlineData("Colony.Fields", "Colony.Abstractions")]
    public void Core_and_Fields_never_reference_the_Plugin_packages(string package, string forbidden)
    {
        // TASK-16 AC #6: a Game builds on Core and Fields alone; Colony.Plugins and Colony.Abstractions sit on top, never underneath.
        var projects = new List<ProjectNode> { Src(forbidden), Src(package, SrcPath(forbidden)) };
        if (package != "Colony.Core")
        {
            projects.Add(Src("Colony.Core"));
        }

        var violation = Assert.Single(DependencyRules.Check(projects));
        Assert.Equal(package, violation.Project);
        Assert.Contains(forbidden, violation.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Abstractions_references_nothing_in_the_repository()
    {
        // Spec section 8: Colony.Abstractions is BCL-only so a Plugin can be compiled against it alone.
        var projects = new[] { Src("Colony.Core"), Src("Colony.Abstractions", SrcPath("Colony.Core")) };

        var violation = Assert.Single(DependencyRules.Check(projects));
        Assert.Equal("Colony.Abstractions", violation.Project);
    }

    [Fact]
    public void Plugins_may_reference_Abstractions_Core_Fields_and_Persistence()
    {
        var projects = new[]
        {
            Src("Colony.Abstractions"),
            Src("Colony.Core"),
            Src("Colony.Fields", SrcPath("Colony.Core")),
            Src("Colony.Persistence", SrcPath("Colony.Core")),
            Src("Colony.Plugins", SrcPath("Colony.Abstractions"), SrcPath("Colony.Core"), SrcPath("Colony.Fields"), SrcPath("Colony.Persistence")),
        };

        Assert.Empty(DependencyRules.Check(projects));
    }

    [Fact]
    public void Core_never_references_Diagnostics_even_through_the_global_rule()
    {
        var projects = new[] { Src("Colony.Diagnostics"), Src("Colony.Core", SrcPath("Colony.Diagnostics")) };

        // One violation per offending reference, not one per rule it breaks.
        Assert.Single(DependencyRules.Check(projects));
    }

    [Fact]
    public void A_shipped_package_referencing_a_tool_is_a_violation()
    {
        // Spec section 14: tools/ is never referenced by src/.
        var abstractions = Src("Colony.Abstractions", "tools/Colony.DependencyCheck/Colony.DependencyCheck.csproj");

        var violation = Assert.Single(DependencyRules.Check([abstractions]));
        Assert.Contains("tools/", violation.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_shipped_package_referencing_a_test_project_is_a_violation()
    {
        var abstractions = Src("Colony.Abstractions", "tests/Colony.Core.Tests/Colony.Core.Tests.csproj");

        Assert.Single(DependencyRules.Check([abstractions]));
    }

    [Fact]
    public void Anything_referencing_a_prototype_is_a_violation()
    {
        // Spec section 14: prototypes are never referenced or built by the solution.
        var tests = new ProjectNode("Colony.Core.Tests", "tests/Colony.Core.Tests/Colony.Core.Tests.csproj", ["prototypes/ecs-bench/src/Colony.Ecs/Colony.Ecs.csproj"], []);

        var violation = Assert.Single(DependencyRules.Check([tests]));
        Assert.Contains("prototypes/", violation.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Test_projects_may_reference_tools_and_any_package()
    {
        var tests = new ProjectNode(
            "Colony.Core.Tests",
            "tests/Colony.Core.Tests/Colony.Core.Tests.csproj",
            [SrcPath("Colony.Core"), SrcPath("Colony.Testing"), "tools/Colony.DependencyCheck/Colony.DependencyCheck.csproj"],
            [new PackageReference("xunit.v3", "")]);

        Assert.Empty(DependencyRules.Check([tests]));
    }
}
