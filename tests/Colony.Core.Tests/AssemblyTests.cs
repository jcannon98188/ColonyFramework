namespace Colony.Core.Tests;

public sealed class AssemblyTests
{
    [Fact]
    public void Core_assembly_carries_the_lockstep_framework_version()
    {
        // VersionPrefix is set once in Directory.Build.props (spec section 3: lockstep SemVer 0.x).
        var version = typeof(AssemblyMarker).Assembly.GetName().Version;

        Assert.Equal(new Version(0, 1, 0, 0), version);
    }
}
