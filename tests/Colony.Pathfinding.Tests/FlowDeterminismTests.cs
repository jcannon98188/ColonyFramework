using Colony.Fields;
using Colony.Pathfinding.Tests.Scenarios;
using Colony.Persistence;

namespace Colony.Pathfinding.Tests;

/// <summary>
/// Flow Field state that Systems observe (which Field is dirty, which was evicted, the eviction order) is a
/// function of Tick state only (spec section 6): host reads between Ticks change nothing, and a save carries it,
/// so load-then-continue equals never saving even while repairs are deferred and Fields evicted.
/// </summary>
public sealed class FlowDeterminismTests
{
    // A starved repair budget defers repairs (agents read dirty Fields); the tight memory budget evicts every Tick.
    private static FlowFieldScenario Scenario() => new() { TightBudget = true, RepairBudgetPerTick = 1 };

    [Fact]
    public void Host_queries_of_existing_fields_between_ticks_do_not_change_the_simulation()
    {
        var plain = Scenario();
        var watched = Scenario();
        using var a = Create(plain);
        using var b = Create(watched);
        var service = b.Pathfinding();
        long deferred = 0;
        for (int t = 0; t < 30; t++)
        {
            plain.Advance(a);
            watched.Advance(b);

            // A renderer looking at the goal sets that are still cached, and through ids, between Ticks.
            for (int set = 0; set < watched.GoalSets; set++)
            {
                var field = Existing(service, watched.GoalsOf(set), watched);
                if (field is not null)
                {
                    _ = service.FlowField(watched.GoalsOf(set), watched.Walker, watched.RadiusChunks);
                    _ = service.FlowField(field.Id);
                    _ = field.DistanceTo(watched.GoalsOf(set)[0]);
                }
            }

            Assert.Equal(a.Hash(), b.Hash());
            deferred = a.Pathfinding().FlowCounters.RepairsDeferred;
        }

        Assert.True(deferred > 0, "the scenario defers repairs, so a between-Tick repair would show");
    }

    [Fact]
    public void Load_then_continue_equals_never_saving_while_repairs_are_deferred_and_fields_evicted()
    {
        var scenario = Scenario();
        var builder = new WorldBuilder();
        scenario.Compose(builder);
        var schema = builder.Build();
        var options = new WorldOptions { Seed = scenario.Seed, WorkerThreads = 2, InitialEntityCapacity = scenario.InitialEntityCapacity };
        using var straight = schema.CreateWorld(options);
        scenario.Populate(straight);
        for (int t = 0; t < 12; t++)
        {
            scenario.Advance(straight);
        }

        Assert.True(straight.Pathfinding().FlowCounters.RepairsDeferred > 0);
        byte[] image = straight.SaveToBytes();
        using var resumed = WorldPersistence.Load(image, schema, new LoadOptions { WorldOptions = options });
        scenario.OnLoaded(resumed);
        Assert.Equal(straight.Pathfinding().FlowFieldCount, resumed.Pathfinding().FlowFieldCount);
        for (int t = 0; t < 20; t++)
        {
            scenario.Advance(straight);
            scenario.Advance(resumed);
            Assert.Equal(straight.Hash(), resumed.Hash());
        }
    }

    [Fact]
    public void A_host_read_of_a_dirty_field_between_ticks_neither_repairs_it_nor_changes_what_systems_see()
    {
        using var plain = ProbeWorld.Create();
        using var watched = ProbeWorld.Create();
        plain.Dirty();
        watched.Dirty();

        // A renderer looks at the last Field between Ticks: it gets the Field as it is, dirty.
        var looked = watched.Service.FlowField(ProbeWorld.Goals[2], watched.Walker, radiusChunks: 0);
        Assert.True(looked.IsDirty);

        for (int t = 0; t < 4; t++)
        {
            plain.World.Tick();
            watched.World.Tick();
            Assert.Equal(plain.World.Hash(), watched.World.Hash());
        }
    }

    [Fact]
    public void Load_then_continue_keeps_dirty_fields_dirty_and_the_repair_order()
    {
        using var straight = ProbeWorld.Create();
        straight.Dirty();
        straight.World.Tick(); // one Field repaired, two deferred: Systems read them stale
        Assert.Equal(2, ProbeWorld.Fields(straight).Count(f => f.IsDirty));

        byte[] image = straight.World.SaveToBytes();
        using var resumed = ProbeWorld.Load(image);
        Assert.Equal(2, ProbeWorld.Fields(resumed).Count(f => f.IsDirty));
        Assert.Equal(straight.World.Hash(), resumed.World.Hash());
        for (int t = 0; t < 4; t++)
        {
            straight.World.Tick();
            resumed.World.Tick();
            Assert.Equal(straight.World.Hash(), resumed.World.Hash());
        }
    }

    private static FlowField? Existing(PathfindingService service, ReadOnlySpan<TilePos> goals, FlowFieldScenario scenario)
    {
        for (int id = 0; id < 4096; id++)
        {
            if (service.FlowField(new FlowFieldId(id)) is { } field && field.RadiusChunks == scenario.RadiusChunks && field.Goals.SequenceEqual(Canonical(goals)))
            {
                return field;
            }
        }

        return null;
    }

    private static TilePos[] Canonical(ReadOnlySpan<TilePos> goals)
    {
        var sorted = goals.ToArray();
        Array.Sort(sorted, static (x, y) => x.Z != y.Z ? x.Z.CompareTo(y.Z) : x.Y != y.Y ? x.Y.CompareTo(y.Y) : x.X.CompareTo(y.X));
        return [.. sorted.Distinct()];
    }

    private static World Create(FlowFieldScenario scenario)
    {
        var builder = new WorldBuilder();
        scenario.Compose(builder);
        var world = builder.Build().CreateWorld(new WorldOptions { Seed = scenario.Seed, WorkerThreads = 2, InitialEntityCapacity = scenario.InitialEntityCapacity });
        scenario.Populate(world);
        return world;
    }

    /// <summary>A plane with three single-chunk Fields and a Simulate System that adds the distances it reads next to each goal into a Component (so the hash sees stale reads).</summary>
    private sealed class ProbeWorld : IDisposable
    {
        public static readonly TilePos[][] Goals = [[new(10, 10, 0)], [new(100, 50, 0)], [new(60, 30, 0)]];

        private static readonly TilePos[] Probes = [new(12, 10, 0), new(102, 50, 0), new(62, 30, 0)];

        private ProbeWorld(World world, MovementClass walker, Field<byte> terrain)
        {
            World = world;
            Walker = walker;
            Terrain = terrain;
        }

        public World World { get; }

        public MovementClass Walker { get; }

        public Field<byte> Terrain { get; }

        public PathfindingService Service => World.Pathfinding();

        public static ProbeWorld Create()
        {
            var schema = Schema(out var walker, out var terrain);
            var world = schema.CreateWorld(new WorldOptions { WorkerThreads = 1 });
            var buffer = world.CreateCommandBuffer();
            var probe = schema.Component("probe");
            var e = buffer.Create(ComponentMask.Of(probe));
            buffer.Add(e, probe, default(Probe));
            world.Playback(buffer);
            world.ResetScratch();
            var service = world.Pathfinding();
            foreach (var goals in Goals)
            {
                _ = service.FlowField(goals, walker, radiusChunks: 0);
            }

            return new ProbeWorld(world, walker, terrain);
        }

        public static ProbeWorld Load(byte[] image)
        {
            var schema = Schema(out var walker, out var terrain);
            return new ProbeWorld(WorldPersistence.Load(image, schema, new LoadOptions { WorldOptions = new WorldOptions { WorkerThreads = 1 } }), walker, terrain);
        }

        public static FlowField[] Fields(ProbeWorld world)
        {
            var service = world.Service;
            var found = new List<FlowField>();
            for (int id = 0; id < 64; id++)
            {
                if (service.FlowField(new FlowFieldId(id)) is { } field)
                {
                    found.Add(field);
                }
            }

            return [.. found];
        }

        /// <summary>A wall tile beside every goal: all three Fields go dirty.</summary>
        public void Dirty()
        {
            foreach (var goals in Goals)
            {
                TestWorlds.Set(World, Terrain, goals[0].X + 1, goals[0].Y, 0, 1);
            }

            Service.Sync();
        }

        public void Dispose() => World.Dispose();

        private static WorldSchema Schema(out MovementClass walker, out Field<byte> terrain)
        {
            var builder = new WorldBuilder();
            builder.DebugValidation(false);
            builder.AddComponent<Probe>("probe");
            var probe = builder.Component<Probe>();
            var fields = builder.AddFields(new WorldSize(128, 64, 1));
            terrain = fields.AddField<byte>("terrain");
            var pathfinding = builder.AddPathfinding(fields, new PathfindingOptions { FlowRepairBudgetPerTick = 1, FlowStalenessTicks = 100 });
            var cls = pathfinding.AddMovementClass(new MovementClassDefinition("walker", Passability.OfField(terrain, static (byte t) => t == 0)).DependsOn(terrain));
            walker = cls;
            var resolved = new FlowField[Goals.Length];
            builder.AddSystem(new SystemDefinition
            {
                Name = "probe.read",
                Phase = Phase.Simulate,
                All = ComponentMask.Of(probe),
                Writes = ComponentMask.Of(probe),
                Begin = (in SystemContext context) =>
                {
                    for (int i = 0; i < Goals.Length; i++)
                    {
                        resolved[i] = context.World.Pathfinding().FlowField(Goals[i], cls, radiusChunks: 0);
                    }
                },
                ForEachChunk = (in SystemContext context, in ChunkView chunk) =>
                {
                    foreach (ref var p in chunk.Column<Probe>(probe))
                    {
                        p.Sum = (p.Sum * 31) + resolved[0].DistanceTo(Probes[0]);
                        p.Sum = (p.Sum * 31) + resolved[1].DistanceTo(Probes[1]);
                        p.Sum = (p.Sum * 31) + resolved[2].DistanceTo(Probes[2]);
                    }
                },
            });
            return builder.Build();
        }
    }

    public struct Probe
    {
        public long Sum;
    }
}
