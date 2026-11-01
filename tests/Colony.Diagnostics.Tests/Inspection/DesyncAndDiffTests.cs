using Colony.Diagnostics.Inspection;
using Colony.Persistence;
using Colony.Scenario;

namespace Colony.Diagnostics.Tests.Inspection;

/// <summary>TASK-29 AC #3: the desync finder over hash trail files, and the World / save diff.</summary>
public sealed class DesyncAndDiffTests
{
    [Fact]
    public void A_hash_trail_file_round_trips_and_two_identical_runs_agree()
    {
        var (schema, a) = Trail(workers: 1, sabotage: false);
        var (_, b) = Trail(workers: 4, sabotage: false);
        var file = HashTrailFile.From(schema, a.Ticks);
        var decoded = HashTrailFile.Decode(file.Encode());

        Assert.Equal(schema.SchemaHash, decoded.SchemaHash);
        Assert.Equal(file.Ticks.Count, decoded.Ticks.Count);
        Assert.Equal(a.Ticks[2].SystemHashes.ToArray(), decoded.Ticks[2].SystemHashes.ToArray());
        Assert.Equal(a.Ticks[2].ArchetypeHashes.ToArray(), decoded.Ticks[2].ArchetypeHashes.ToArray());
        Assert.Contains(decoded.Systems, s => s.Name == "Wander");

        var comparison = HashTrailFile.Compare(decoded, HashTrailFile.From(schema, b.Ticks));
        Assert.False(comparison.Diverged);
        Assert.Equal("The trails agree over 3 Ticks.", comparison.Description);
    }

    [Fact]
    public void The_desync_finder_names_the_first_diverging_tick_phase_and_system_without_the_schema()
    {
        var (schema, a) = Trail(workers: 1, sabotage: true);
        var (_, b) = Trail(workers: 4, sabotage: true);
        string left = Path.GetTempFileName();
        string right = Path.GetTempFileName();
        try
        {
            HashTrailFile.From(schema, a.Ticks).WriteFile(left);
            HashTrailFile.From(schema, b.Ticks).WriteFile(right);

            var comparison = HashTrailFile.Compare(HashTrailFile.ReadFile(left), HashTrailFile.ReadFile(right));

            Assert.True(comparison.Diverged);
            Assert.Equal(0, comparison.Divergence!.Value.Tick);
            Assert.Equal(schema.System("Wander").Id, comparison.Divergence.Value.SystemId);
            Assert.StartsWith("Tick 0, Phase Simulate, System Wander", comparison.Description, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(left);
            File.Delete(right);
        }
    }

    [Fact]
    public void Trails_from_different_schemas_are_refused()
    {
        var (schema, a) = Trail(workers: 1, sabotage: false);
        var other = HashTrailFile.From(InspectFixture.BuildSchema(), []);
        Assert.Throws<ArgumentException>(() => HashTrailFile.Compare(HashTrailFile.From(schema, a.Ticks), other));
        Assert.Throws<FormatException>(() => HashTrailFile.Decode(InspectCodec.Encode(InspectValue.Map(new KeyValuePair<string, InspectValue>("format", "nope")))));
    }

    [Fact]
    public void Two_saves_diff_entity_by_entity_and_identical_saves_report_identical()
    {
        using var fx = InspectFixture.Create(entities: 4);
        byte[] before = fx.World.SaveToBytes();
        var vitals = fx.Schema.Component<Vitals>();
        var victim = WorldDiffTestHelpers.FirstEntity(fx.World);
        var buffer = fx.World.CreateCommandBuffer();
        buffer.Add(victim, vitals, new Vitals { Health = 1 });
        fx.World.Playback(buffer);
        byte[] after = fx.World.SaveToBytes();

        using var a = WorldPersistence.Load(before, fx.Schema);
        using var b = WorldPersistence.Load(after, fx.Schema);
        using var c = WorldPersistence.Load(before, fx.Schema);

        Assert.True(WorldDiff.Compare(a, c).Identical);
        var report = WorldDiff.Compare(a, b);
        var difference = Assert.Single(report.Differences);
        Assert.Equal(new WorldDifference("component", victim, "vitals"), difference);
        Assert.NotEqual(report.LeftHash, report.RightHash);
    }

    [Fact]
    public void The_save_manifest_diff_needs_no_schema()
    {
        using var fx = InspectFixture.Create(entities: 4);
        byte[] before = fx.World.SaveToBytes();
        fx.Run(2);
        byte[] after = fx.World.SaveToBytes();

        var diff = SaveDiff.CompareManifests(WorldPersistence.ReadManifest(before), WorldPersistence.ReadManifest(after));

        Assert.False(diff["identical"].AsBoolean);
        Assert.Equal(0, diff["left"]["tick"].AsWhole);
        Assert.Equal(2, diff["right"]["tick"].AsWhole);
        Assert.True(diff["sameSchema"].AsBoolean);
        Assert.Contains(diff["changed"].Items, c => c.AsText == "tick");
        Assert.Contains(diff["changed"].Items, c => c.AsText == "worldHash");
        Assert.True(SaveDiff.CompareManifests(WorldPersistence.ReadManifest(before), WorldPersistence.ReadManifest(before))["identical"].AsBoolean);
    }

    private static (WorldSchema Schema, HashTrailRecorder Trail) Trail(int workers, bool sabotage)
    {
        var recorder = new HashTrailRecorder();
        var schema = ColonistScenario.BuildSchema(sabotage, 500);
        using var world = ColonistScenario.CreateWorld(schema, workers, colonists: 5_000, food: 500, options: new DiagnosticsOptions { HashTrail = true, Listener = recorder });
        for (int t = 0; t < 3; t++)
        {
            world.Tick();
        }

        return (schema, recorder);
    }
}

internal static class WorldDiffTestHelpers
{
    public static EntityId FirstEntity(World world)
    {
        for (int a = 0; a < world.ArchetypeCount; a++)
        {
            if (world.DescribeArchetype(a).EntityCount > 0)
            {
                return world.ArchetypeEntities(a, 0)[0];
            }
        }

        throw new InvalidOperationException("The World has no Entities.");
    }
}
