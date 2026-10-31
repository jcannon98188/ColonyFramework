namespace Colony.Tools.DependencyCheck;

/// <summary>
/// One project in the repository, as the dependency check sees it.
/// Paths are relative to the repo root and use forward slashes.
/// </summary>
/// <param name="Name">Project name (file name without extension), e.g. <c>Colony.Core</c>.</param>
/// <param name="RelativePath">Path of the .csproj relative to the repo root.</param>
/// <param name="ProjectReferences">Referenced project paths, relative to the repo root.</param>
/// <param name="PackageReferences">NuGet package references declared by the project file itself.</param>
public sealed record ProjectNode(
    string Name,
    string RelativePath,
    IReadOnlyList<string> ProjectReferences,
    IReadOnlyList<PackageReference> PackageReferences)
{
    /// <summary>Top-level folder (<c>src</c>, <c>tests</c>, <c>tools</c>, ...) the project lives in.</summary>
    public string Area => AreaOf(RelativePath);

    internal static string AreaOf(string relativePath)
    {
        int slash = relativePath.IndexOf('/', StringComparison.Ordinal);
        return slash < 0 ? string.Empty : relativePath[..slash];
    }

    internal static string NameOf(string relativePath) => Path.GetFileNameWithoutExtension(relativePath);
}

/// <summary>A NuGet package reference declared in a project file.</summary>
/// <param name="Id">Package id.</param>
/// <param name="PrivateAssets">Value of the <c>PrivateAssets</c> metadata, or empty when absent.</param>
public sealed record PackageReference(string Id, string PrivateAssets)
{
    /// <summary>True when the package is build-time only (<c>PrivateAssets="all"</c>), e.g. an analyzer.</summary>
    public bool IsBuildOnly => string.Equals(PrivateAssets, "all", StringComparison.OrdinalIgnoreCase);
}

/// <summary>A dependency rule violation found in a project.</summary>
/// <param name="Project">Name of the offending project.</param>
/// <param name="Message">What it referenced and which rule that breaks.</param>
public sealed record Violation(string Project, string Message)
{
    /// <inheritdoc />
    public override string ToString() => $"{Project}: {Message}";
}
