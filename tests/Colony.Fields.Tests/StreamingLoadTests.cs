using System.Diagnostics;
using Colony.Fields;
using Colony.Persistence;

namespace Colony.Fields.Tests;

/// <summary>TASK-27: delayed background loads with deadline stalls (AC #1), the resident-chunk budget (AC #2), catch-up (AC #3) and the streaming counters (AC #5).</summary>
public sealed class StreamingLoadTests
{
    // 6 x 4 x 1 chunks (96 x 64 x 16 tiles); the focus at tile (20, 20, 3) sits in chunk (1, 1, 0).
    private static readonly WorldSize Size = new(96, 64, 16);
    private static readonly TilePos Focus = new(20, 20, 3);
    private static readonly ChunkCoord FocusChunk = new(1, 1, 0);

    private sealed record Schema(WorldSchema World, Field<int> Height, ComponentType TilePos, ComponentType Health, ResidencyBuilder Residency);

    private static Schema Build(ResidencyOptions options, Action<WorldBuilder, ResidencyBuilder>? extra = null)
    {
        var builder = new WorldBuilder().DebugValidation(false);
        var tilePos = builder.AddComponent<TilePos>("tile.pos").Component<TilePos>();
        var health = builder.AddComponent<Health>("health").Component<Health>();
        var fields = builder.AddFields(Size);
        var height = fields.AddField<int>("height");
        var residency = builder.AddResidency(fields, tilePos, options);
        builder.AddSystem(new SystemDefinition
        {
            Name = "heal",
            Phase = Phase.Simulate,
            Writes = ComponentMask.Of(health),
            ForEachChunk = (in SystemContext context, in ChunkView chunk) =>
            {
                var column = chunk.Column<Health>(health);
                foreach (int row in chunk.EnabledRows(health))
                {
                    column[row].Value++;
                }
            },
        });
        extra?.Invoke(builder, residency);
        return new Schema(builder.Build(), height, tilePos, health, residency);
    }

    private static World CreateStreaming(Schema schema, Terrain source, int workers = 2)
    {
        var world = schema.World.CreateWorld(new WorldOptions { WorkerThreads = workers, Seed = 11 });
        world.Fields().ChunkSource = source;
        return world;
    }

    private static void SubmitFocus(World world, TilePos centre, int radius) => world.Inputs.Submit(new InputSource(1), new FocusPoint(1, centre, radius));

    [Fact]
    public void Delayed_loads_apply_exactly_N_Ticks_after_the_request_and_the_ring_lands_before_anything_activates()
    {
        var schema = Build(new ResidencyOptions { InitialResidency = ChunkResidency.Unloaded, LoadDelayTicks = 2 });
        var source = new Terrain(schema);
        using var world = CreateStreaming(schema, source);
        var residency = world.Residency();
        var fields = world.Fields();
        var ids = schema.Residency.Counters;

        SubmitFocus(world, Focus, 1);
        world.Tick(); // Tick 0: the Input Phase stores the focus; Streaming ran before it.
        Assert.Equal(fields.ChunkCount, residency.Counts.Unloaded);
        Assert.Equal(0, residency.PendingLoadCount);

        world.Tick(); // Tick 1: Streaming requests the 16 desired chunks, due at Tick 3.
        Assert.Equal(fields.ChunkCount, residency.Counts.Unloaded);
        Assert.Equal(16, residency.PendingLoadCount);
        Assert.Equal(16, world.Diagnostics.Latest.Counters[ids.LoadsInFlight.Id]);
        Assert.Equal(0, world.Diagnostics.Latest.Counters[ids.Loads.Id]);

        world.Tick(); // Tick 2: not due yet, whatever the workers have finished.
        Assert.Equal(fields.ChunkCount, residency.Counts.Unloaded);
        Assert.Equal(16, residency.PendingLoadCount);

        world.Tick(); // Tick 3: every load applies; the ring is complete, so the 9 focus chunks activate in the same Phase.
        Assert.Equal(new ResidencyCounts(9, 7, 8), residency.Counts);
        Assert.Equal(0, residency.PendingLoadCount);
        Assert.True(residency.RingInvariantHolds());
        Assert.Equal(16, world.Diagnostics.Latest.Counters[ids.Loads.Id]);
        Assert.Equal(16, residency.Streaming.Loads); // stalls, if any, depend on the pool's warm-up: counted, never asserted here
        Assert.Equal(16, Enumerable.Range(0, fields.ChunkCount).Count(residency.HasBeenGenerated));
        Assert.Equal(16, source.Generated);

        // The source's Entities exist, awake in Active chunks and frozen in the ring, and the Active ones simulated Tick 3.
        foreach (var (id, coord) in Entities(world, schema.TilePos))
        {
            bool active = residency.ResidencyOf(coord) == ChunkResidency.Active;
            Assert.Equal(!active, residency.IsFrozen(id));
            Assert.Equal(active ? 1 : 0, world.Get<Health>(id, schema.Health).Value);
        }

        Assert.Contains("residency.loads", schema.World.Counters.ToArray());
        Assert.Contains("residency.load-stalls", schema.World.Counters.ToArray());
        Assert.Contains("residency.evictions", schema.World.Counters.ToArray());
        Assert.Contains("residency.catch-ups", schema.World.Counters.ToArray());
    }

    [Fact]
    public void The_in_flight_bound_spreads_requests_over_Ticks_in_canonical_chunk_order()
    {
        var schema = Build(new ResidencyOptions { InitialResidency = ChunkResidency.Unloaded, LoadDelayTicks = 1, MaxLoadsInFlight = 4 });
        using var world = CreateStreaming(schema, new Terrain(schema));
        var residency = world.Residency();
        var fields = world.Fields();
        var ids = schema.Residency.Counters;

        SubmitFocus(world, Focus, 1);
        world.Tick(); // Tick 0
        world.Tick(); // Tick 1: the first four desired chunks (ascending ordinal) are requested.
        var desired = Enumerable.Range(0, fields.ChunkCount).Where(o => Distance(fields.ChunkAt(o)) <= 2).ToArray();
        Assert.Equal(16, desired.Length);
        Assert.Equal(desired.Take(4), Enumerable.Range(0, fields.ChunkCount).Where(residency.IsLoadPending));
        Assert.Equal(4, world.Diagnostics.Latest.Counters[ids.LoadsInFlight.Id]);

        for (int t = 2; t <= 5; t++)
        {
            world.Tick(); // each Tick applies four and requests the next four
            Assert.Equal(4, world.Diagnostics.Latest.Counters[ids.Loads.Id]);
            Assert.True(residency.RingInvariantHolds());
        }

        Assert.Equal(new ResidencyCounts(9, 7, 8), residency.Counts);
        Assert.Equal(0, residency.PendingLoadCount);
    }

    [Fact]
    public void A_load_not_ready_at_its_Tick_stalls_the_Tick_and_the_stall_is_counted()
    {
        var schema = Build(new ResidencyOptions { InitialResidency = ChunkResidency.Unloaded, LoadDelayTicks = 1 });
        using var gate = new ManualResetEventSlim(false);
        var source = new Terrain(schema) { Gate = gate };
        using var world = CreateStreaming(schema, source);
        var residency = world.Residency();
        var ids = schema.Residency.Counters;

        SubmitFocus(world, Focus, 1);
        world.Tick(); // Tick 0
        world.Tick(); // Tick 1: 16 loads requested; their workers block on the gate.
        Assert.Equal(16, residency.PendingLoadCount);

        var release = new Thread(() =>
        {
            Thread.Sleep(150);
            gate.Set();
        });
        release.Start();
        var stopwatch = Stopwatch.StartNew();
        world.Tick(); // Tick 2: due now; the Tick waits for the workers.
        stopwatch.Stop();
        release.Join();

        Assert.True(stopwatch.ElapsedMilliseconds >= 50, $"the Tick took {stopwatch.ElapsedMilliseconds} ms; it should have waited for the gate");
        Assert.Equal(new ResidencyCounts(9, 7, 8), residency.Counts);
        Assert.True(residency.Streaming.LoadStalls >= 1);
        Assert.Equal(residency.Streaming.LoadStalls, world.Diagnostics.Latest.Counters[ids.LoadStalls.Id]);
        Assert.Equal(16, world.Diagnostics.Latest.Counters[ids.Loads.Id]);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void Load_timing_never_changes_the_state_a_slow_source_hashes_like_a_fast_one(int workers)
    {
        var schema = Build(new ResidencyOptions { InitialResidency = ChunkResidency.Unloaded, LoadDelayTicks = 1, RingWidth = 2 });
        using var fast = CreateStreaming(schema, new Terrain(schema), workers);
        using var slow = CreateStreaming(schema, new Terrain(schema) { DelayMs = 3 }, workers);

        for (int t = 0; t < 14; t++)
        {
            var centre = new TilePos(8 + (16 * (t % 6)), 8 + (16 * (t % 4)), 3);
            SubmitFocus(fast, centre, 1);
            SubmitFocus(slow, centre, 1);
            fast.Tick();
            slow.Tick();
            Assert.Equal(fast.Hash(), slow.Hash());
            Assert.True(fast.Residency().RingInvariantHolds());
        }

        Assert.True(fast.Residency().Streaming.Loads > 16, "the moving focus kept loading chunks");
    }

    [Fact]
    public void Pending_loads_are_saved_and_a_loaded_World_continues_like_the_original()
    {
        var schema = Build(new ResidencyOptions { InitialResidency = ChunkResidency.Unloaded, LoadDelayTicks = 2 });
        using var world = CreateStreaming(schema, new Terrain(schema));
        SubmitFocus(world, Focus, 1);
        world.Tick();
        world.Tick(); // 16 loads pending, due at Tick 3
        Assert.Equal(16, world.Residency().PendingLoadCount);

        byte[] image = world.SaveToBytes();
        Assert.Contains("ext/residency/loads", WorldPersistence.SectionNames(image));
        // The source is host-set after loading, so the loader cannot verify the hash (unstored Unloaded chunks hash through the source).
        using var loaded = WorldPersistence.Load(image, schema.World, new LoadOptions { WorldOptions = new WorldOptions { WorkerThreads = 2, Seed = 11 }, VerifyWorldHash = false });
        loaded.Fields().ChunkSource = new Terrain(schema);
        Assert.Equal(16, loaded.Residency().PendingLoadCount);
        Assert.Equal(world.Hash(), loaded.Hash());

        for (int t = 0; t < 4; t++)
        {
            world.Tick();
            loaded.Tick();
            Assert.Equal(world.Hash(), loaded.Hash());
        }

        Assert.Equal(new ResidencyCounts(9, 7, 8), loaded.Residency().Counts);
    }

    [Fact]
    public void A_prefetch_hint_warms_the_buffer_without_changing_state_or_timing()
    {
        var schema = Build(new ResidencyOptions { InitialResidency = ChunkResidency.Unloaded, LoadDelayTicks = 2 });
        var hinted = new Terrain(schema);
        using var world = CreateStreaming(schema, hinted);
        using var plain = CreateStreaming(schema, new Terrain(schema));
        var fields = world.Fields();
        foreach (int ordinal in Enumerable.Range(0, fields.ChunkCount).Where(o => Distance(fields.ChunkAt(o)) <= 2))
        {
            world.Residency().Prefetch(fields.ChunkAt(ordinal));
        }

        SpinWait.SpinUntil(() => hinted.Generated == 16, 5000);
        Assert.Equal(16, hinted.Generated);

        for (int t = 0; t < 4; t++)
        {
            SubmitFocus(world, Focus, 1);
            SubmitFocus(plain, Focus, 1);
            world.Tick();
            plain.Tick();
            Assert.Equal(plain.Residency().Counts, world.Residency().Counts);
            Assert.Equal(plain.Residency().PendingLoadCount, world.Residency().PendingLoadCount);
        }

        Assert.Equal(16, hinted.Generated); // the requests reused the prefetched cells (hashing below regenerates Unloaded chunks)
        Assert.Equal(new ResidencyCounts(9, 7, 8), world.Residency().Counts);
        Assert.Equal(plain.Hash(), world.Hash());
    }

    [Fact]
    public void Eviction_under_the_budget_is_farthest_first_then_by_ordinal_demotes_the_rest_and_emits_the_pressure_event()
    {
        var pressures = new List<BudgetPressure>();
        var schema = Build(new ResidencyOptions { ResidentChunkBudget = 4 }, (builder, residency) =>
        {
            var queue = residency.BudgetPressureQueue!.Value;
            builder.AddSystem(new SystemDefinition
            {
                Name = "capture",
                Phase = Phase.Simulate,
                ReadsResources = ResourceMask.Of(queue),
                Begin = (in SystemContext context) =>
                {
                    foreach (var pressure in context.Events<BudgetPressure>(queue))
                    {
                        pressures.Add(pressure);
                    }
                },
            });
        });
        Assert.Equal("colony.budget-pressure", schema.World.DescribeResource(schema.Residency.BudgetPressureQueue!.Value).Name);

        using var world = schema.World.CreateWorld(new WorldOptions { WorkerThreads = 1 });
        using var parallel = schema.World.CreateWorld(new WorldOptions { WorkerThreads = 4 });
        var fields = world.Fields();
        foreach (var w in new[] { world, parallel })
        {
            Populate(w, schema.TilePos, schema.Health);
            SubmitFocus(w, Focus, 1);
            w.Tick(); // Tick 0: focus stored
            w.Tick(); // Tick 1: 16 chunks demanded against 4
        }

        var residency = world.Residency();
        Assert.Equal(new ResidencyCounts(0, 4, 20), residency.Counts);
        Assert.True(residency.RingInvariantHolds());

        // Farthest first takes the 7 ring chunks (distance 2), then distance-1 chunks by ascending ordinal; the centre and the
        // 3 highest-ordinal distance-1 chunks survive and are demoted to Hibernating because they border unloaded space.
        var survivors = Enumerable.Range(0, fields.ChunkCount).Where(o => Distance(fields.ChunkAt(o)) == 1).OrderDescending().Take(3)
            .Append(fields.OrdinalOf(FocusChunk)).Order().ToArray();
        Assert.Equal(survivors, Enumerable.Range(0, fields.ChunkCount).Where(o => residency.ResidencyOf(o) != ChunkResidency.Unloaded));
        // Tick 0 had no focus yet: everything Active was demanded (the never-unload policy) and trimmed by ordinal; Tick 1 trimmed
        // the focus set. Both Worlds share the capture list, so the sequence appears once per World.
        BudgetPressure[] perWorld = [new BudgetPressure(24, 4, 20), new BudgetPressure(16, 4, 12)];
        Assert.Equal([.. perWorld, .. perWorld], pressures);
        Assert.Equal(12, world.Diagnostics.Latest.Counters[schema.Residency.Counters.Evictions.Id]);
        Assert.Equal(world.Hash(), parallel.Hash());

        world.Tick();
        parallel.Tick();
        Assert.Equal(world.Hash(), parallel.Hash());
        Assert.Equal(residency.Counts, parallel.Residency().Counts);
    }

    [Fact]
    public void Catch_up_reproduces_the_state_of_running_every_Tick_and_the_replay_cap_bounds_it()
    {
        var schema = Build(new ResidencyOptions(), WithCatchUp);
        var capped = Build(new ResidencyOptions { CatchUpMaxTicks = 3 }, WithCatchUp);
        using var reference = schema.World.CreateWorld(new WorldOptions { WorkerThreads = 2 });
        using var world = schema.World.CreateWorld(new WorldOptions { WorkerThreads = 2 });
        using var cappedWorld = capped.World.CreateWorld(new WorldOptions { WorkerThreads = 2 });
        Populate(reference, schema.TilePos, schema.Health);
        Populate(world, schema.TilePos, schema.Health);
        Populate(cappedWorld, capped.TilePos, capped.Health);

        SubmitFocus(world, Focus, 0);
        SubmitFocus(cappedWorld, Focus, 0);
        for (int t = 0; t < 6; t++)
        {
            reference.Tick();
            world.Tick(); // Tick 1 freezes everything outside chunk (1, 1, 0)
            cappedWorld.Tick();
        }

        var far = Entities(world, schema.TilePos).Where(e => e.Coord != FocusChunk).Select(e => e.Id).ToArray();
        Assert.Equal(23, far.Length); // the 8 ring chunks and the 15 Unloaded ones are all frozen
        Assert.All(far, id => Assert.Equal(1, world.Get<Health>(id, schema.Health).Value));
        Assert.All(far, id => Assert.True(world.Has(id, schema.Residency.FrozenAt!.Value)));
        Assert.All(far, id => Assert.Equal(1, world.Get<FrozenAt>(id, schema.Residency.FrozenAt!.Value).Tick));

        world.Inputs.Submit(new InputSource(1), new FocusPoint(1, default, -1));
        cappedWorld.Inputs.Submit(new InputSource(1), new FocusPoint(1, default, -1));
        reference.Tick();
        world.Tick(); // Tick 6: focus removed
        cappedWorld.Tick();
        reference.Tick();
        world.Tick(); // Tick 7: thaw with elapsed 6, then Simulate
        cappedWorld.Tick();

        Assert.Equal(24, world.Residency().Counts.Active);
        Assert.Equal(HealthByChunk(reference, schema), HealthByChunk(world, schema));
        Assert.All(far, id => Assert.False(world.Has(id, schema.Residency.FrozenAt!.Value)));
        Assert.Equal(23, world.Diagnostics.Latest.Counters[schema.Residency.Counters.CatchUps.Id]);
        Assert.Equal(new StreamingCounters(15, 0, 0, 0, 23, 23 * 6), world.Residency().Streaming);

        var cappedHealth = HealthByChunk(cappedWorld, capped);
        var referenceHealth = HealthByChunk(reference, schema);
        foreach (var (coord, value) in referenceHealth)
        {
            Assert.Equal(coord == FocusChunk ? value : value - 3, cappedHealth[coord]);
        }

        for (int t = 0; t < 3; t++)
        {
            reference.Tick();
            world.Tick();
            Assert.Equal(HealthByChunk(reference, schema), HealthByChunk(world, schema));
        }
    }

    [Fact]
    public void The_catch_up_work_budget_spreads_thaws_over_Ticks_and_still_lands_on_the_reference_state()
    {
        var schema = Build(new ResidencyOptions { CatchUpWorkBudget = 1 }, WithCatchUp);
        using var reference = schema.World.CreateWorld(new WorldOptions { WorkerThreads = 2 });
        using var world = schema.World.CreateWorld(new WorldOptions { WorkerThreads = 2 });
        Populate(reference, schema.TilePos, schema.Health);
        Populate(world, schema.TilePos, schema.Health);

        SubmitFocus(world, Focus, 0);
        for (int t = 0; t < 4; t++)
        {
            reference.Tick();
            world.Tick();
        }

        world.Inputs.Submit(new InputSource(1), new FocusPoint(1, default, -1));
        reference.Tick();
        world.Tick(); // Tick 4: focus removed
        reference.Tick();
        world.Tick(); // Tick 5: one chunk thaws (budget 1 Entity), the others wait
        Assert.Equal(2, world.Residency().Counts.Active);
        Assert.Equal(1, world.Diagnostics.Latest.Counters[schema.Residency.Counters.CatchUps.Id]);

        for (int t = 0; t < 24; t++)
        {
            reference.Tick();
            world.Tick(); // one more chunk per Tick; 23 frozen chunks in all
        }

        Assert.Equal(24, world.Residency().Counts.Active);
        Assert.Equal(HealthByChunk(reference, schema), HealthByChunk(world, schema));
    }

    private static void WithCatchUp(WorldBuilder builder, ResidencyBuilder residency)
        => residency.AddCatchUp<Health>(builder.Component<Health>(), static (ref Health health, long elapsed) => health.Value += (int)elapsed);

    private static int Distance(ChunkCoord c) => Math.Max(Math.Abs(c.X - FocusChunk.X), Math.Max(Math.Abs(c.Y - FocusChunk.Y), Math.Abs(c.Z - FocusChunk.Z)));

    /// <summary>One Entity per chunk at the chunk's (4, 4, 3) tile.</summary>
    private static void Populate(World world, ComponentType tilePos, ComponentType health)
    {
        var commands = world.CreateCommandBuffer();
        var fields = world.Fields();
        for (int ordinal = 0; ordinal < fields.ChunkCount; ordinal++)
        {
            var e = commands.Create(ComponentMask.Of(tilePos, health));
            commands.Add(e, tilePos, fields.ChunkAt(ordinal).Origin + new TilePos(4, 4, 3));
        }

        world.Playback(commands);
        world.ResetScratch();
    }

    private static List<(EntityId Id, ChunkCoord Coord)> Entities(World world, ComponentType tilePos)
    {
        var result = new List<(EntityId, ChunkCoord)>();
        foreach (var chunk in world.Query(ComponentMask.Of(tilePos), default, ignoreEnableBits: true))
        {
            var positions = chunk.Column<TilePos>(tilePos);
            var ids = chunk.Entities;
            for (int row = 0; row < positions.Length; row++)
            {
                result.Add((ids[row], positions[row].Chunk));
            }
        }

        return result;
    }

    private static SortedDictionary<ChunkCoord, int> HealthByChunk(World world, Schema schema)
    {
        var result = new SortedDictionary<ChunkCoord, int>(Comparer<ChunkCoord>.Create(static (a, b) => (a.Z, a.Y, a.X).CompareTo((b.Z, b.Y, b.X))));
        foreach (var (id, coord) in Entities(world, schema.TilePos))
        {
            result[coord] = world.Get<Health>(id, schema.Health).Value;
        }

        return result;
    }

    private record struct Health(int Value);

    /// <summary>A pure chunk source: height = ordinal, one Entity per chunk; optionally slow or gated to exercise stalls.</summary>
    private sealed class Terrain(Schema schema) : IChunkSource
    {
        private int _generated;

        public int DelayMs { get; init; }

        public ManualResetEventSlim? Gate { get; init; }

        public int Generated => Volatile.Read(ref _generated);

        public void GenerateCells(ChunkCoord coord, ChunkCellBuffer cells)
        {
            Gate?.Wait();
            if (DelayMs > 0)
            {
                Thread.Sleep(DelayMs);
            }

            cells.Cells(schema.Height).Fill((coord.X * 100) + (coord.Y * 10) + coord.Z + 1);
            Interlocked.Increment(ref _generated);
        }

        public void PlaceEntities(ChunkCoord coord, CommandBuffer commands)
        {
            var entity = commands.Create(ComponentMask.Of(schema.TilePos, schema.Health));
            commands.Add(entity, schema.TilePos, coord.Origin + new TilePos(4, 4, 3));
        }
    }
}
