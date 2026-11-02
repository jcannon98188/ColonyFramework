using Colony.Scenario;

namespace Colony.Diagnostics.Tests;

/// <summary>
/// TASK-17 AC #5: with the hash trail on, two runs that diverge report the first differing Tick, Phase and
/// System. The colonist scenario's sabotage switch keys the Wander System's RNG by worker count, so a 1-worker
/// and a 4-worker run part ways in Wander, Simulate Phase, the first Tick.
/// </summary>
public sealed class HashTrailTests
{
    private const int Colonists = 5_000;
    private const int Food = 500;

    [Fact]
    public void Two_identical_runs_have_identical_trails()
    {
        var a = RunTrail(workers: 1, sabotage: false, ticks: 4);
        var b = RunTrail(workers: 4, sabotage: false, ticks: 4);

        Assert.Equal(4, a.Ticks.Count);
        Assert.Null(HashTrail.Compare(a.Ticks, b.Ticks, Schema(false)));
        Assert.NotEqual(0UL, a.Ticks[0].Hash);
        Assert.All(a.Ticks, t => Assert.NotEqual(0UL, t.SystemHashes[0]));
    }

    [Fact]
    public void A_thread_keyed_system_is_reported_as_the_first_diverging_system()
    {
        var schema = Schema(sabotage: true);
        var a = RunTrail(workers: 1, sabotage: true, ticks: 3);
        var b = RunTrail(workers: 4, sabotage: true, ticks: 3);

        var divergence = HashTrail.Compare(a.Ticks, b.Ticks, schema);

        Assert.NotNull(divergence);
        Assert.Equal(0, divergence.Value.Tick);
        Assert.Equal(Phase.Simulate.Id, divergence.Value.PhaseId);
        Assert.Equal(schema.System("Wander").Id, divergence.Value.SystemId);
        Assert.False(divergence.Value.AtPhaseBoundary);
        Assert.Contains("System Wander", divergence.Value.Describe(schema), StringComparison.Ordinal);
    }

    [Fact]
    public void The_trail_records_archetype_hashes_at_every_phase_boundary()
    {
        var a = RunTrail(workers: 2, sabotage: false, ticks: 2);

        var tick = a.Ticks[1];
        Assert.True(tick.ArchetypeStride >= 2);
        ulong colonists = tick.ArchetypeHashes[(Phase.Simulate.Id * tick.ArchetypeStride) + 0];
        ulong food = tick.ArchetypeHashes[(Phase.Simulate.Id * tick.ArchetypeStride) + 1];
        Assert.NotEqual(0UL, colonists);
        Assert.NotEqual(0UL, food);
        Assert.NotEqual(colonists, food);
    }

    [Fact]
    public void The_recorder_refuses_a_world_without_a_trail()
    {
        var recorder = new HashTrailRecorder();
        using var world = Schema(false).CreateWorld(new WorldOptions
        {
            WorkerThreads = 1,
            Diagnostics = new DiagnosticsOptions { Listener = recorder },
        });

        Assert.Throws<InvalidOperationException>(() => world.Tick());
    }

    private static WorldSchema Schema(bool sabotage) => ColonistScenario.BuildSchema(sabotage, Food);

    private static HashTrailRecorder RunTrail(int workers, bool sabotage, int ticks)
    {
        var recorder = new HashTrailRecorder();
        var schema = Schema(sabotage);
        using var world = schema.CreateWorld(new WorldOptions
        {
            WorkerThreads = workers,
            InitialEntityCapacity = (Colonists + Food) * 2,
            Diagnostics = new DiagnosticsOptions { HashTrail = true, Listener = recorder },
        });
        Populate(world, schema);
        for (int t = 0; t < ticks; t++)
        {
            world.Tick();
        }

        return recorder;
    }

    private static void Populate(World world, WorldSchema schema)
    {
        var position = schema.Component<ColonistPosition>();
        var target = schema.Component<ColonistTarget>();
        var hunger = schema.Component<ColonistHunger>();
        var health = schema.Component<ColonistHealth>();
        var foodType = schema.Component<FoodItem>();
        var init = world.Random.Derive("init");
        var buffer = world.CreateCommandBuffer();
        for (int i = 0; i < Colonists; i++)
        {
            var block = init.BlockAt((ulong)i);
            var e = buffer.Create(ComponentMask.Of(position, target, hunger, health));
            var at = ColonistScenario.RandomTile(block[0], block[1]);
            buffer.Add(e, position, at);
            buffer.Add(e, target, new ColonistTarget { X = at.X, Y = at.Y });
            buffer.Add(e, hunger, new ColonistHunger { Value = (block[2] % 500) * 0.1f });
            buffer.Add(e, health, new ColonistHealth { Value = 100 });
        }

        for (int i = 0; i < Food; i++)
        {
            var block = init.BlockAt((ulong)(Colonists + i));
            var e = buffer.Create(ComponentMask.Of(position, foodType));
            buffer.Add(e, position, ColonistScenario.RandomTile(block[0], block[1]));
            buffer.Add(e, foodType, new FoodItem { Nutrition = 50 });
        }

        world.Playback(buffer);
        world.ResetScratch();
    }
}
