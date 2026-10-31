using Colony.Tools.DependencyCheck;

// Usage: Colony.DependencyCheck [repo-root]
// Exit code 0: every project respects spec sections 3 and 14. Exit code 1: violations listed on stderr.
string repoRoot = args.Length > 0
    ? Path.GetFullPath(args[0])
    : ProjectScanner.FindRepoRoot(Directory.GetCurrentDirectory());

var projects = ProjectScanner.Scan(repoRoot);
var violations = DependencyRules.Check(projects);

Console.WriteLine($"Dependency check: {projects.Count} project(s) under {repoRoot}");
foreach (var project in projects)
{
    Console.WriteLine($"  {project.RelativePath} -> [{string.Join(", ", project.ProjectReferences.Select(ProjectNode.NameOf))}]");
}

if (violations.Count == 0)
{
    Console.WriteLine("No dependency rule violations.");
    return 0;
}

Console.Error.WriteLine($"{violations.Count} dependency rule violation(s):");
foreach (var violation in violations)
{
    Console.Error.WriteLine($"  error DEP0001: {violation}");
}

return 1;
