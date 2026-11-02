namespace Colony.Tools.DependencyCheck;

/// <summary>
/// The package layering rules of spec section 3 ("Packages and dependency direction") and the folder rules of
/// section 14, applied to a project graph. Each offending reference yields exactly one <see cref="Violation"/>.
/// </summary>
public static class DependencyRules
{
    private const string Src = "src";
    private const string Tests = "tests";
    private const string Prototypes = "prototypes";

    /// <summary>
    /// Shipped packages whose allowed project references are spelled out by the spec.
    /// A <c>src/</c> package absent from this table may reference any other <c>src/</c> package
    /// except those in <see cref="NeverReferenced"/>.
    /// </summary>
    private static readonly Dictionary<string, string[]> AllowedReferences = new(StringComparer.Ordinal)
    {
        // Core depends only on the BCL.
        ["Colony.Core"] = [],
        // Abstractions holds only contracts and depends on the BCL alone.
        ["Colony.Abstractions"] = [],
        // Fields, Persistence depend on Core; Plugins on Core, Fields, Persistence (shared Fields registration,
        // manifest) and the Abstractions it implements.
        ["Colony.Fields"] = ["Colony.Core"],
        ["Colony.Persistence"] = ["Colony.Core"],
        ["Colony.Plugins"] = ["Colony.Abstractions", "Colony.Core", "Colony.Fields", "Colony.Persistence"],
        // Testing exercises Core, Fields, Persistence and Plugins (spec section 3, implementation exception).
        ["Colony.Testing"] = ["Colony.Core", "Colony.Fields", "Colony.Persistence", "Colony.Plugins"],
        // Native hosts the C ABI whose host lives in Plugins, and takes whatever Plugins takes.
        ["Colony.Native"] = ["Colony.Abstractions", "Colony.Core", "Colony.Fields", "Colony.Persistence", "Colony.Plugins"],
        // Pathfinding, Colony, ProcGen depend on Core and Fields (and Persistence only where they save state).
        ["Colony.Pathfinding"] = ["Colony.Core", "Colony.Fields", "Colony.Persistence"],
        // Colony also takes Pathfinding: its work index and hauling matcher ship reachability filters over
        // PathfindingService.Reachable (docs/adr/0004-colony-references-pathfinding.md, TASK-41). One way only.
        ["Colony.Colony"] = ["Colony.Core", "Colony.Fields", "Colony.Pathfinding", "Colony.Persistence"],
        ["Colony.ProcGen"] = ["Colony.Core", "Colony.Fields", "Colony.Persistence"],
        // Diagnostics depends on Core, Fields and Persistence (it reads their counters and the replay log) and on nothing else.
        ["Colony.Diagnostics"] = ["Colony.Core", "Colony.Fields", "Colony.Persistence"],
    };

    /// <summary>Nothing depends on Diagnostics, Testing or Plugins, so a Game can run without the Plugin machinery.</summary>
    private static readonly string[] NeverReferenced = ["Colony.Diagnostics", "Colony.Testing", "Colony.Plugins"];

    /// <summary>Checks every project and returns all violations in input order.</summary>
    public static IReadOnlyList<Violation> Check(IEnumerable<ProjectNode> projects)
    {
        ArgumentNullException.ThrowIfNull(projects);

        var violations = new List<Violation>();
        foreach (var project in projects)
        {
            CheckProject(project, violations);
        }

        return violations;
    }

    private static void CheckProject(ProjectNode project, List<Violation> violations)
    {
        bool isShipped = project.Area == Src;

        foreach (var reference in project.ProjectReferences)
        {
            string? message = CheckProjectReference(project, isShipped, reference);
            if (message is not null)
            {
                violations.Add(new Violation(project.Name, message));
            }
        }

        if (project.Name == "Colony.Core")
        {
            foreach (var package in project.PackageReferences.Where(p => !p.IsBuildOnly))
            {
                violations.Add(new Violation(
                    project.Name,
                    $"references package {package.Id}; Core depends only on the BCL (spec section 3). Build-only analyzers need PrivateAssets=\"all\"."));
            }
        }
    }

    private static string? CheckProjectReference(ProjectNode project, bool isShipped, string reference)
    {
        string area = ProjectNode.AreaOf(reference);
        string referenced = ProjectNode.NameOf(reference);

        // Spec section 14: prototypes are never referenced or built by the solution.
        if (area == Prototypes)
        {
            return $"references {reference}; nothing may reference {Prototypes}/ (spec section 14)";
        }

        if (!isShipped)
        {
            return null;
        }

        // Spec section 14: tools/ (and tests/, benchmarks/, samples/) are never referenced by src/.
        if (area != Src)
        {
            return $"references {reference}; shipped packages under src/ may only reference other src/ packages, never {area}/ (spec section 14)";
        }

        if (AllowedReferences.TryGetValue(project.Name, out var allowed))
        {
            if (!allowed.Contains(referenced, StringComparer.Ordinal))
            {
                string allowedText = allowed.Length == 0 ? "nothing but the BCL" : $"only [{string.Join(", ", allowed)}]";
                return $"references {referenced}; spec section 3 allows {project.Name} to reference {allowedText}";
            }

            return null;
        }

        if (NeverReferenced.Contains(referenced, StringComparer.Ordinal))
        {
            return $"references {referenced}; nothing depends on Diagnostics, Testing or Plugins (spec section 3)";
        }

        return null;
    }
}
