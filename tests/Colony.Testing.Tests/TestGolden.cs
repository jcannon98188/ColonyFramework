using System.Runtime.CompilerServices;
using Colony.Testing;

namespace Colony.Testing.Tests;

/// <summary>The committed golden hashes of this test project live in <c>Golden/</c> beside the sources (located by source path, like the generator snapshots).</summary>
internal static class TestGolden
{
    public static GoldenHashStore Store { get; } = new(Path.Combine(Path.GetDirectoryName(ThisFile())!, "Golden"));

    private static string ThisFile([CallerFilePath] string path = "") => path;
}
