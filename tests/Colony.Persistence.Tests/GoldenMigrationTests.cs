using System.Runtime.CompilerServices;
using Colony.Persistence;
using Colony.Testing;

namespace Colony.Persistence.Tests;

/// <summary>
/// TASK-37 AC #4: a committed golden save made with the old shapes (<see cref="MigrationScenario"/> version 1:
/// Resource <see cref="TallyV1"/>, ledger extension format 1) is migrated into today's schema (version 2) and the
/// loaded World continues identically on 1, 2, 4 and N workers. The golden itself stays byte-identical to what
/// the version 1 scenario saves today, like every other golden (accept with <c>COLONY_ACCEPT_GOLDEN=1</c>).
/// </summary>
public sealed class GoldenMigrationTests
{
    private const int SavedTicks = 3;
    private const int ContinueTicks = 8;

    [Fact]
    public void The_committed_old_shape_golden_matches_what_the_old_shapes_save_today()
    {
        string path = GoldenPath();
        byte[] current = SaveOldShapes();
        if (!File.Exists(path) || GoldenHashStore.AcceptFromEnvironment)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, current);
            Assert.True(GoldenHashStore.AcceptFromEnvironment, $"No golden save at {path}; it was written now, re-run with COLONY_ACCEPT_GOLDEN=1 to accept it knowingly.");
        }

        byte[] golden = File.ReadAllBytes(path);
        var manifest = WorldPersistence.ReadManifest(golden);
        Assert.Equal(SavedTicks, manifest.Tick);
        Assert.Equal(1, Assert.Single(manifest.Extensions).FormatVersion);
        if (!golden.AsSpan().SequenceEqual(current))
        {
            File.WriteAllBytes(Path.ChangeExtension(path, ".received.clnysave"), current);
            Assert.Fail($"Today's old-shape save differs from the golden at {path} ({current.Length} vs {golden.Length} bytes); the received bytes are beside it.");
        }
    }

    [Fact]
    public void The_old_shape_golden_without_migrations_fails_naming_the_resource()
    {
        var error = Assert.Throws<MigrationException>(() => WorldPersistence.Load(Golden(), ScenarioWorlds.Schema(new MigrationScenario { Version = 2 })));
        Assert.Contains($"Resource '{MigrationScenario.TallyName}'", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_old_shape_golden_migrates_into_todays_schema_and_continues_identically_on_1_2_4_and_N_workers()
    {
        byte[] golden = Golden();
        var manifest = WorldPersistence.ReadManifest(golden);
        int[] workerCounts = [1, 2, 4, Environment.ProcessorCount];
        var hashes = new ulong[workerCounts.Length][];
        for (int w = 0; w < workerCounts.Length; w++)
        {
            var scenario = new MigrationScenario { Version = 2 };
            var v2 = ScenarioWorlds.Schema(scenario);
            var migrations = MigrationScenario.MigrationsFromV1(ScenarioWorlds.Schema(new MigrationScenario { Version = 1 }), v2);
            using var loaded = WorldPersistence.Load(golden, v2, new LoadOptions { Migrations = migrations, WorldOptions = new WorldOptions { WorkerThreads = workerCounts[w] } });
            scenario.OnLoaded(loaded);

            Assert.Equal(SavedTicks, loaded.CurrentTick);
            ref readonly var tally = ref loaded.Resource<TallyV2>(v2.Resource<TallyV2>());
            Assert.True(tally.Total > 0);
            Assert.Equal(0, tally.Samples);
            Assert.Equal(SavedTicks, loaded.Extension<Ledger>().Entries.Count);
            // The schema changed, so the saved hash is not the loaded one; migrations are deterministic though.
            Assert.NotEqual(manifest.SchemaHash, v2.SchemaHash);

            hashes[w] = new ulong[ContinueTicks + 1];
            hashes[w][0] = loaded.Hash();
            for (int t = 1; t <= ContinueTicks; t++)
            {
                scenario.Advance(loaded);
                hashes[w][t] = loaded.Hash();
            }

            Assert.Equal(ContinueTicks, loaded.Resource<TallyV2>(v2.Resource<TallyV2>()).Samples);
        }

        for (int w = 1; w < workerCounts.Length; w++)
        {
            for (int t = 0; t <= ContinueTicks; t++)
            {
                Assert.True(hashes[0][t] == hashes[w][t], $"The migrated World diverged at Tick {SavedTicks + t} between 1 and {workerCounts[w]} workers.");
            }
        }
    }

    private static byte[] Golden()
    {
        string path = GoldenPath();
        return File.Exists(path) ? File.ReadAllBytes(path) : SaveOldShapes();
    }

    private static byte[] SaveOldShapes()
    {
        var scenario = new MigrationScenario { Version = 1 };
        using var world = ScenarioWorlds.Create(scenario);
        for (int i = 0; i < SavedTicks; i++)
        {
            scenario.Advance(world);
        }

        return world.SaveToBytes();
    }

    private static string GoldenPath() => Path.Combine(Path.GetDirectoryName(ThisFile())!, "Golden", "v" + SaveContainer.Version, "migration-old-shapes.clnysave");

    private static string ThisFile([CallerFilePath] string path = "") => path;
}
