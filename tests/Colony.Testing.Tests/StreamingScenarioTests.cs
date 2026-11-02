using Colony.Fields;
using Colony.Persistence;
using Colony.Testing;
using Colony.Testing.Tests.Scenarios;

namespace Colony.Testing.Tests;

/// <summary>TASK-26 AC #1 and #6: streaming residency, on-demand generation and forced evict/reload round trips run through the determinism harness.</summary>
public sealed class StreamingScenarioTests
{
    private static readonly DeterminismOptions Options = new() { Ticks = 36, CheckpointEvery = 1 };

    [Fact]
    public void Streaming_with_evict_reload_round_trips_hashes_identically_on_1_2_4_and_N_threads_and_matches_the_golden_hashes()
    {
        var result = DeterminismHarness.Run(new StreamingScenario(), Options);

        Assert.True(result.Passed, result.Describe());
        Assert.Equal(36, result.Hashes.ToArray().Distinct().Count()); // the World really evolves
        var golden = TestGolden.Store.Compare(result);
        Assert.True(golden.Passed, golden.Message);
    }

    [Fact]
    public void The_round_trips_do_not_change_the_hash_sequence_of_a_run_without_them()
    {
        var withRoundTrips = DeterminismHarness.Run(new StreamingScenario(), Options);
        var without = DeterminismHarness.Run(new StreamingScenario { RoundTripEvery = 0 }, Options);

        Assert.True(withRoundTrips.Passed, withRoundTrips.Describe());
        Assert.True(without.Passed, without.Describe());
        Assert.Null(withRoundTrips.FirstDivergenceFrom(without));
    }

    [Fact]
    public void A_replayed_focus_schedule_reproduces_the_live_run_hashes_on_every_worker_count()
    {
        var live = DeterminismHarness.Run(new StreamingScenario(), Options);
        Assert.True(live.Passed, live.Describe());

        var log = ReplayPlaybackScenario.Record(new StreamingScenario(), ticks: 36, workers: 2, new ReplayOptions { CheckpointEvery = 4 });
        var playback = DeterminismHarness.Run(new ReplayPlaybackScenario(ReplayLog.Open(log.Encode()), new StreamingScenario { RoundTripEvery = 0 }), Options);

        Assert.True(playback.Passed, playback.Describe());
        Assert.Null(playback.FirstDivergenceFrom(live));
    }

    [Fact]
    public void The_scenario_streams_chunks_in_and_out_freezes_creatures_in_the_ring_and_keeps_the_Pinned_walker_awake()
    {
        var scenario = new StreamingScenario();
        using var world = DeterminismHarness.CreateWorld(scenario, DeterminismHarness.BuildSchema(scenario, null), workers: 2, listener: null);
        var residency = world.Residency();
        var fields = world.Fields();
        var tilePos = world.Schema.Component<TilePos>();
        var pinned = world.Schema.Component<Pinned>();
        Assert.Equal(fields.ChunkCount, residency.Counts.Unloaded);

        int generatedMax = 0, storedSeen = 0;
        for (int i = 0; i < 36; i++)
        {
            scenario.Advance(world);
            Assert.True(residency.RingInvariantHolds());
            generatedMax = Math.Max(generatedMax, Enumerable.Range(0, fields.ChunkCount).Count(residency.HasBeenGenerated));
            storedSeen += Enumerable.Range(0, fields.ChunkCount).Count(residency.HasStoredCells);
        }

        var counts = residency.Counts;
        Assert.True(counts.Active > 0 && counts.Hibernating > 0 && counts.Unloaded > 0, counts.ToString());
        Assert.True(generatedMax > counts.Active + counts.Hibernating, "chunks the focus left behind were generated and unloaded again");
        Assert.True(storedSeen > 0, "unloaded chunks the stencil changed are kept as their save sections");

        int frozen = 0, awake = 0;
        EntityId? walker = null;
        foreach (var chunk in world.Query(ComponentMask.Of(tilePos), default, ignoreEnableBits: true))
        {
            var ids = chunk.Entities;
            for (int row = 0; row < ids.Length; row++)
            {
                if (world.Has(ids[row], pinned))
                {
                    walker = ids[row];
                }
                else if (residency.IsFrozen(ids[row]))
                {
                    frozen++;
                }
                else
                {
                    awake++;
                }
            }
        }

        Assert.True(frozen > 0, "creatures in non-Active chunks are frozen");
        Assert.True(awake > 0, "creatures in Active chunks keep wandering");
        Assert.NotNull(walker);
        Assert.False(residency.IsFrozen(walker.Value));
        Assert.NotEqual(new TilePos(8, 8, 8), world.Get<TilePos>(walker.Value, tilePos));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void A_steady_state_tick_with_a_settled_focus_allocates_nothing(int workers)
    {
        var result = AllocationHarness.Run(new StreamingScenario { FocusMovesUntilTick = 12, RoundTripEvery = 0 }, new AllocationOptions { Workers = workers, WarmupTicks = 20, MeasuredTicks = 10 });

        Assert.True(result.Passed, result.Describe());
    }

    // ---- TASK-27: delayed loads, deadline stalls and the resident-chunk budget through the harness (AC #1, #2, #5) ----

    private static StreamingScenario Delayed(bool holdLoadsUntilStall = false) => new() { LoadDelayTicks = 2, RingWidth = 2, HoldLoadsUntilStall = holdLoadsUntilStall };

    private static StreamingScenario Budgeted() => new() { ResidentChunkBudget = 10 };

    [Fact]
    public void Delayed_background_loads_hash_identically_on_every_worker_count_and_match_the_golden_hashes()
    {
        var result = DeterminismHarness.Run(Delayed(), Options);

        Assert.True(result.Passed, result.Describe());
        Assert.Equal(36, result.Hashes.ToArray().Distinct().Count());
        var golden = TestGolden.Store.Compare(result);
        Assert.True(golden.Passed, golden.Message);
    }

    [Fact]
    public void A_slow_chunk_source_stalls_Ticks_but_produces_the_hashes_of_a_fast_one()
    {
        // TASK-44: the slow source holds every background load back until a Tick has stalled on one, so the stall is
        // certain on any machine (a fixed sleep raced the deadline and lost on a fast Linux runner).
        var fast = DeterminismHarness.Run(Delayed(), Options);
        var slow = Delayed(holdLoadsUntilStall: true);
        var slowResult = DeterminismHarness.Run(slow, Options);

        Assert.True(fast.Passed, fast.Describe());
        Assert.True(slowResult.Passed, slowResult.Describe());
        Assert.Null(slowResult.FirstDivergenceFrom(fast));
        Assert.True(slow.LastCounters.Loads > 0, "the slow run loaded chunks");
        Assert.True(slow.LastCounters.LoadStalls > 0, $"a source that finishes only once a Tick waits for it must stall: {slow.LastCounters}");
    }

    [Fact]
    public void The_resident_chunk_budget_hashes_identically_on_every_worker_count_and_matches_the_golden_hashes()
    {
        var scenario = Budgeted();
        var result = DeterminismHarness.Run(scenario, Options);

        Assert.True(result.Passed, result.Describe());
        var golden = TestGolden.Store.Compare(result);
        Assert.True(golden.Passed, golden.Message);
        Assert.True(scenario.LastCounters.Evictions > 0, "the two focus points demanded more than 10 resident chunks at some point");

        using var world = DeterminismHarness.CreateWorld(scenario, DeterminismHarness.BuildSchema(scenario, null), workers: 2, listener: null);
        for (int i = 0; i < 36; i++)
        {
            scenario.Advance(world);
            var counts = world.Residency().Counts;
            Assert.True(counts.Active + counts.Hibernating <= 10, $"Tick {i}: {counts}");
            Assert.True(world.Residency().RingInvariantHolds());
        }
    }

    [Fact]
    public void A_steady_state_tick_with_delayed_loading_and_a_settled_focus_allocates_nothing()
    {
        var result = AllocationHarness.Run(new StreamingScenario { LoadDelayTicks = 2, RingWidth = 2, FocusMovesUntilTick = 12, RoundTripEvery = 0 }, new AllocationOptions { Workers = 1, WarmupTicks = 20, MeasuredTicks = 10 });

        Assert.True(result.Passed, result.Describe());
    }
}
