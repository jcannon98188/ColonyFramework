using Colony.Persistence;
using Colony.Testing;
using Colony.Testing.Tests.Scenarios;

namespace Colony.Testing.Tests;

/// <summary>TASK-15 AC #5: replay equivalence and a simulated two-peer lockstep run through the determinism harness.</summary>
public sealed class ReplayScenarioTests
{
    private static readonly DeterminismOptions Options = new() { Ticks = 30, CheckpointEvery = 1 };

    [Fact]
    public void Playing_a_recorded_log_back_reproduces_the_live_run_hashes_on_every_worker_count()
    {
        var live = DeterminismHarness.Run(new InputScenario { Concurrent = true }, Options);
        Assert.True(live.Passed, live.Describe());

        var log = ReplayPlaybackScenario.Record(new InputScenario(), ticks: 30, workers: 2, new ReplayOptions { CheckpointEvery = 3 });
        Assert.Equal(30, log.FrameCount);
        var playback = DeterminismHarness.Run(new ReplayPlaybackScenario(ReplayLog.Open(log.Encode())), Options);

        Assert.True(playback.Passed, playback.Describe());
        Assert.Null(playback.FirstDivergenceFrom(live));
    }

    [Fact]
    public void Two_peers_in_lockstep_hash_identically_every_tick_whatever_the_submission_order_or_thread_count()
    {
        using var scenario = new LockstepScenario();
        var lockstep = DeterminismHarness.Run(scenario, Options);
        Assert.True(lockstep.Passed, lockstep.Describe());

        // The host peer runs the same script as the input scenario, so the two agree hash for hash.
        var live = DeterminismHarness.Run(new InputScenario(), Options);
        Assert.Null(lockstep.FirstDivergenceFrom(live));
    }

    [Fact]
    public void A_peer_fed_a_different_command_is_caught_at_the_end_of_the_tick()
    {
        using var scenario = new SabotagedLockstepScenario();
        var result = DeterminismHarness.Run(scenario, new DeterminismOptions { Ticks = 3, CheckpointEvery = 1, WorkerCounts = [1] });

        Assert.False(result.Passed);
        Assert.IsType<HarnessException>(result.Runs[0].Error);
    }

    /// <summary>The lockstep scenario with one extra command slipped into the host's Tick 1 only.</summary>
    private sealed class SabotagedLockstepScenario : Scenario, IDisposable
    {
        private readonly LockstepScenario _inner = new();

        public override string Name => "lockstep-sabotaged";

        public override ulong Seed { get => _inner.Seed; init => _ = value; }

        public override int InitialEntityCapacity => _inner.InitialEntityCapacity;

        public override void Compose(WorldBuilder builder) => _inner.Compose(builder);

        public override void Populate(World world) => _inner.Populate(world);

        public override void Advance(World world)
        {
            if (world.CurrentTick == 1)
            {
                world.Inputs.Submit(new InputSource(99), new InputScenario.Deposit { Wood = 1 });
            }

            _inner.Advance(world);
        }

        public void Dispose() => _inner.Dispose();
    }
}
