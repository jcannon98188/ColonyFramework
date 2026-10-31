using System.Runtime.CompilerServices;
using Colony.Jobs.Tests.Scenarios;
using Colony.Persistence;
using Colony.Testing;
using Colony.Testing.Tests.Scenarios;

namespace Colony.Persistence.Tests;

/// <summary>
/// TASK-14 AC #4: golden saves, one folder per container version under <c>Golden/</c>, committed and loaded
/// here. Each test proves two things: the committed file still loads into a World that hashes as its manifest
/// says (format compatibility), and saving the same small World today produces the committed bytes
/// (format stability). Accept a new golden with <c>COLONY_ACCEPT_GOLDEN=1</c>, as for the hash goldens.
/// </summary>
public sealed class GoldenSaveTests
{
    private const int Ticks = GoldenWorlds.Ticks;

    public static TheoryData<string> Names => [.. GoldenWorlds.Names];

    [Theory]
    [MemberData(nameof(Names))]
    public void The_committed_golden_save_loads_and_matches_todays_bytes(string name)
    {
        string path = GoldenPath(name);
        byte[] current = GoldenWorlds.SaveToday(name);

        if (!File.Exists(path) || GoldenHashStore.AcceptFromEnvironment)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, current);
            Assert.True(GoldenHashStore.AcceptFromEnvironment, $"No golden save at {path}; it was written now, re-run with COLONY_ACCEPT_GOLDEN=1 to accept it knowingly.");
        }

        byte[] golden = File.ReadAllBytes(path);
        var manifest = WorldPersistence.ReadManifest(golden);
        Assert.Equal(SaveContainer.Version, manifest.ContainerVersion);
        Assert.Equal(Ticks, manifest.Tick);

        using var loaded = GoldenWorlds.Load(name, golden);
        Assert.Equal(manifest.WorldHash, loaded.Hash());
        Assert.Equal(Ticks, loaded.CurrentTick);

        if (!golden.AsSpan().SequenceEqual(current))
        {
            File.WriteAllBytes(Path.ChangeExtension(path, ".received.clnysave"), current);
            Assert.Fail($"Today's save of '{name}' differs from the golden at {path} ({current.Length} vs {golden.Length} bytes); the received bytes are beside it. Accept with COLONY_ACCEPT_GOLDEN=1 only with a container version bump or a deliberate scenario change.");
        }
    }

    [Fact]
    public void Every_golden_folder_is_a_known_container_version()
    {
        string root = Path.GetDirectoryName(GoldenPath("x"))!;
        Assert.Equal("v" + SaveContainer.Version, Path.GetFileName(root));
    }

    private static string GoldenPath(string name) => Path.Combine(Path.GetDirectoryName(ThisFile())!, "Golden", "v" + SaveContainer.Version, name + ".clnysave");

    private static string ThisFile([CallerFilePath] string path = "") => path;
}

/// <summary>Small Worlds that keep the committed saves a few kilobytes each.</summary>
internal static class GoldenWorlds
{
    public const int Ticks = 3;

    public static readonly string[] Names = ["colonist-small", "claims-small", "tables"];

    public static byte[] SaveToday(string name)
    {
        if (name == "tables")
        {
            using var tables = TablesWorld.Compose().Create();
            for (int i = 0; i < Ticks; i++)
            {
                tables.Tick();
            }

            return tables.SaveToBytes();
        }

        var scenario = Scenario(name);
        using var world = ScenarioWorlds.Create(scenario);
        for (int i = 0; i < Ticks; i++)
        {
            scenario.Advance(world);
        }

        return world.SaveToBytes();
    }

    public static World Load(string name, byte[] golden)
        => name == "tables"
            ? WorldPersistence.Load(golden, TablesWorld.Compose().Schema)
            : WorldPersistence.Load(golden, ScenarioWorlds.Schema(Scenario(name)));

    private static Testing.Scenario Scenario(string name) => name switch
    {
        "colonist-small" => new ColonistHarnessScenario { Colonists = 40, Food = 4 },
        "claims-small" => new ClaimScenario { Workers = 40, Sites = 8 },
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "Unknown golden."),
    };
}
