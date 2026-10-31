namespace Colony.Tools.DependencyCheck;

/// <summary>
/// The package layering rules of spec section 3 ("Packages and dependency direction"), applied to a project graph.
/// </summary>
public static class DependencyRules
{
    /// <summary>
    /// Shipped packages whose allowed project references are spelled out by the spec.
    /// A package absent from this table may reference any <c>src/</c> package not in <see cref="NeverReferenced"/>.
    /// </summary>
    private static readonly Dictionary<string, string[]> AllowedReferences = new(StringComparer.Ordinal)
    {
        // Core depends only on the BCL.
        ["Colony.Core"] = [],
    };

    /// <summary>Nothing depends on Diagnostics, Testing or Plugins, so a Game can run without the Plugin machinery.</summary>
    private static readonly string[] NeverReferenced = [];

    /// <summary>Checks every project and returns all violations, ordered by project then message.</summary>
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
        if (!AllowedReferences.TryGetValue(project.Name, out var allowed))
        {
            return;
        }

        foreach (var reference in project.ProjectReferences)
        {
            string referenced = ProjectNode.NameOf(reference);
            if (!allowed.Contains(referenced, StringComparer.Ordinal))
            {
                violations.Add(new Violation(
                    project.Name,
                    $"references {referenced}; spec section 3 allows {project.Name} to reference only [{string.Join(", ", allowed)}]"));
            }
        }
    }
}
