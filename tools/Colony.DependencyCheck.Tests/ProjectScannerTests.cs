namespace Colony.Tools.DependencyCheck.Tests;

public sealed class ProjectScannerTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("colony-depcheck-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void Scans_project_references_and_package_references_relative_to_the_repo_root()
    {
        WriteProject("src/Colony.Core/Colony.Core.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="SomeAnalyzer" PrivateAssets="all" />
              </ItemGroup>
            </Project>
            """);
        WriteProject("tests/Colony.Core.Tests/Colony.Core.Tests.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <ProjectReference Include="..\..\src\Colony.Core\Colony.Core.csproj" />
                <ProjectReference Include="../../src/Colony.Generators/Colony.Generators.csproj" OutputItemType="Analyzer" ReferenceOutputAssembly="false" />
                <PackageReference Include="xunit.v3" />
              </ItemGroup>
            </Project>
            """);

        var projects = ProjectScanner.Scan(_root);

        var core = Assert.Single(projects, p => p.Name == "Colony.Core");
        Assert.Equal("src/Colony.Core/Colony.Core.csproj", core.RelativePath);
        Assert.Empty(core.ProjectReferences);
        Assert.Equal([new PackageReference("SomeAnalyzer", "all")], core.PackageReferences);

        var tests = Assert.Single(projects, p => p.Name == "Colony.Core.Tests");
        Assert.Equal(["src/Colony.Core/Colony.Core.csproj", "src/Colony.Generators/Colony.Generators.csproj"], tests.ProjectReferences);
        Assert.Equal([new PackageReference("xunit.v3", "")], tests.PackageReferences);
    }

    [Fact]
    public void Prototypes_are_not_scanned_but_references_into_them_are_kept()
    {
        WriteProject("prototypes/ecs-bench/src/Colony.Ecs/Colony.Ecs.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        WriteProject("samples/Hello/Hello.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <ProjectReference Include="..\..\prototypes\ecs-bench\src\Colony.Ecs\Colony.Ecs.csproj" />
              </ItemGroup>
            </Project>
            """);

        var projects = ProjectScanner.Scan(_root);

        var hello = Assert.Single(projects);
        Assert.Equal(["prototypes/ecs-bench/src/Colony.Ecs/Colony.Ecs.csproj"], hello.ProjectReferences);
    }

    [Fact]
    public void The_real_repository_has_no_violations()
    {
        // This is the local "fails the build" hook (AC #4): a bad reference anywhere in the repo fails `dotnet test`.
        string repoRoot = ProjectScanner.FindRepoRoot(AppContext.BaseDirectory);

        var violations = DependencyRules.Check(ProjectScanner.Scan(repoRoot));

        Assert.Empty(violations);
    }

    [Fact]
    public void A_Game_can_reference_Core_and_Fields_without_the_Plugin_packages()
    {
        // TASK-16 AC #6, asserted against the real csproj files: neither Core nor Fields pulls in Colony.Plugins or Colony.Abstractions,
        // directly or through anything they reference, so a Game that skips Plugins never compiles against them.
        string repoRoot = ProjectScanner.FindRepoRoot(AppContext.BaseDirectory);
        var projects = ProjectScanner.Scan(repoRoot).ToDictionary(p => p.RelativePath, StringComparer.Ordinal);

        var reachable = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>(["src/Colony.Core/Colony.Core.csproj", "src/Colony.Fields/Colony.Fields.csproj"]);
        while (pending.Count > 0)
        {
            string path = pending.Pop();
            Assert.True(projects.ContainsKey(path), $"{path} is not in the repository.");
            foreach (string reference in projects[path].ProjectReferences)
            {
                if (reachable.Add(reference))
                {
                    pending.Push(reference);
                }
            }
        }

        Assert.DoesNotContain("src/Colony.Plugins/Colony.Plugins.csproj", reachable);
        Assert.DoesNotContain("src/Colony.Abstractions/Colony.Abstractions.csproj", reachable);
        Assert.True(projects.ContainsKey("src/Colony.Plugins/Colony.Plugins.csproj"), "Colony.Plugins exists, so the assertion is not vacuous.");
    }

    private void WriteProject(string relativePath, string content)
    {
        string path = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }
}
