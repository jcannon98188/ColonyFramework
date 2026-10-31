using System.Xml.Linq;

namespace Colony.Tools.DependencyCheck;

/// <summary>
/// Finds every <c>.csproj</c> in the solution areas of the repository and reads its project and package
/// references straight from the XML. It deliberately does not evaluate MSBuild: the rules are about what a
/// project file declares, and plain XML keeps the tool independent of the SDK that builds the solution.
/// </summary>
public static class ProjectScanner
{
    /// <summary>Top-level folders the solution builds from (spec section 14). <c>prototypes/</c> is excluded on purpose.</summary>
    public static readonly string[] ScannedAreas = ["src", "tests", "tools", "benchmarks", "samples"];

    private const string SolutionFileName = "Colony.slnx";

    /// <summary>Scans the repository rooted at <paramref name="repoRoot"/>; results are sorted by relative path.</summary>
    public static IReadOnlyList<ProjectNode> Scan(string repoRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoRoot);
        string root = Path.GetFullPath(repoRoot);

        var projects = new List<ProjectNode>();
        foreach (string area in ScannedAreas)
        {
            string areaPath = Path.Combine(root, area);
            if (!Directory.Exists(areaPath))
            {
                continue;
            }

            foreach (string file in Directory.EnumerateFiles(areaPath, "*.csproj", SearchOption.AllDirectories))
            {
                projects.Add(Read(root, file));
            }
        }

        projects.Sort((a, b) => string.CompareOrdinal(a.RelativePath, b.RelativePath));
        return projects;
    }

    /// <summary>Walks up from <paramref name="start"/> until the directory holding <c>Colony.slnx</c>.</summary>
    public static string FindRepoRoot(string start)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(start);

        for (var dir = new DirectoryInfo(Path.GetFullPath(start)); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, SolutionFileName)))
            {
                return dir.FullName;
            }
        }

        throw new DirectoryNotFoundException($"No {SolutionFileName} found above {start}.");
    }

    private static ProjectNode Read(string root, string projectFile)
    {
        var document = XDocument.Load(projectFile);
        string projectDir = Path.GetDirectoryName(projectFile)!;

        var projectReferences = document.Descendants()
            .Where(e => e.Name.LocalName == "ProjectReference")
            .Select(e => Relative(root, Path.GetFullPath(Path.Combine(projectDir, NormalizeSeparators(Include(e))))))
            .ToArray();

        var packageReferences = document.Descendants()
            .Where(e => e.Name.LocalName == "PackageReference")
            .Select(e => new PackageReference(Include(e), (string?)e.Attribute("PrivateAssets") ?? e.Element("PrivateAssets")?.Value ?? string.Empty))
            .ToArray();

        return new ProjectNode(Path.GetFileNameWithoutExtension(projectFile), Relative(root, projectFile), projectReferences, packageReferences);
    }

    private static string Include(XElement element) =>
        (string?)element.Attribute("Include") ?? throw new InvalidDataException($"{element.Name.LocalName} without Include in {element.Document?.BaseUri}");

    private static string NormalizeSeparators(string path) => path.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);

    private static string Relative(string root, string fullPath) => Path.GetRelativePath(root, fullPath).Replace('\\', '/');
}
