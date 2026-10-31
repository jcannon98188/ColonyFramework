namespace Colony.Jobs;

/// <summary>Tunables of <see cref="ClaimPolicy.Default"/> (spec section 9.2 anti-thrash defaults). Durations are Ticks.</summary>
public sealed class ClaimPolicyOptions
{
    /// <summary>After a grant the agent proposes nothing else for this long, even if it releases early (commitment window).</summary>
    public int CommitmentWindow { get; init; } = 10;

    /// <summary>After a denial the agent does not propose the same target again for this long (per-agent cooldown on failed targets).</summary>
    public int CooldownTicks { get; init; } = 20;

    /// <summary>Job-start circuit breaker: more than this many grants inside <see cref="BreakerWindow"/> Ticks bars the agent for <see cref="BreakerBarTicks"/>.</summary>
    public int BreakerMaxStarts { get; init; } = 3;

    public int BreakerWindow { get; init; } = 30;

    public int BreakerBarTicks { get; init; } = 30;

    public static ClaimPolicyOptions Default { get; } = new();
}

/// <summary>
/// The replaceable anti-thrash policy of spec section 9.2. It owns the <see cref="ClaimAgentState"/> Component:
/// <see cref="MayPropose"/> gates <see cref="ClaimSet.TryPropose"/>, and the hooks are called by the resolve
/// System (grant, deny, lost) and by <see cref="ClaimSet.Release"/>. A Game subclasses it (or uses
/// <see cref="None"/>) and passes it through <see cref="ClaimOptions.Policy"/>. Implementations must be
/// deterministic and allocation-free: they run inside Systems.
/// </summary>
public abstract class ClaimPolicy
{
    /// <summary>Commitment window, per-target cooldown and job-start circuit breaker with <see cref="ClaimPolicyOptions.Default"/>.</summary>
    public static ClaimPolicy Default { get; } = new DefaultClaimPolicy(ClaimPolicyOptions.Default);

    /// <summary>No damping: every proposal is allowed, no state is kept. The baseline a thrash scenario measures against.</summary>
    public static ClaimPolicy None { get; } = new NoClaimPolicy();

    /// <summary>The default policy with custom tunables.</summary>
    public static ClaimPolicy WithOptions(ClaimPolicyOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new DefaultClaimPolicy(options);
    }

    /// <summary>Whether the agent may propose <paramref name="target"/> at <paramref name="tick"/>.</summary>
    public abstract bool MayPropose(in ClaimAgentState state, EntityId target, long tick);

    public abstract void OnGranted(ref ClaimAgentState state, EntityId target, long tick);

    public abstract void OnDenied(ref ClaimAgentState state, EntityId target, long tick);

    public abstract void OnReleased(ref ClaimAgentState state, EntityId target, long tick);

    public abstract void OnLost(ref ClaimAgentState state, EntityId target, long tick);

    private sealed class NoClaimPolicy : ClaimPolicy
    {
        public override bool MayPropose(in ClaimAgentState state, EntityId target, long tick) => true;

        public override void OnGranted(ref ClaimAgentState state, EntityId target, long tick)
        {
        }

        public override void OnDenied(ref ClaimAgentState state, EntityId target, long tick)
        {
        }

        public override void OnReleased(ref ClaimAgentState state, EntityId target, long tick)
        {
        }

        public override void OnLost(ref ClaimAgentState state, EntityId target, long tick)
        {
        }
    }

    private sealed class DefaultClaimPolicy : ClaimPolicy
    {
        private readonly ClaimPolicyOptions _options;

        public DefaultClaimPolicy(ClaimPolicyOptions options)
        {
            _options = options;
        }

        public override bool MayPropose(in ClaimAgentState state, EntityId target, long tick)
            => tick >= state.CommittedUntil && tick >= state.BarredUntil && !state.IsCooledDown(target, tick);

        public override void OnGranted(ref ClaimAgentState state, EntityId target, long tick)
        {
            long windowStart = state.WindowStart;
            int starts = state.RecentStarts;
            if (tick - windowStart >= _options.BreakerWindow)
            {
                windowStart = tick;
                starts = 0;
            }

            starts++;
            long barredUntil = state.BarredUntil;
            if (starts > _options.BreakerMaxStarts)
            {
                barredUntil = tick + _options.BreakerBarTicks;
                starts = 0;
                windowStart = tick;
            }

            state = state with
            {
                CommittedUntil = tick + _options.CommitmentWindow,
                BarredUntil = barredUntil,
                WindowStart = windowStart,
                RecentStarts = starts,
            };
        }

        public override void OnDenied(ref ClaimAgentState state, EntityId target, long tick)
        {
            // Replace an entry for the same target, else an expired one, else the one expiring soonest.
            int victim = 0;
            long soonest = long.MaxValue;
            for (int i = 0; i < ClaimAgentState.CooldownEntries; i++)
            {
                var entry = state.CooldownAt(i);
                if (entry.Target == target)
                {
                    victim = i;
                    break;
                }

                if (entry.Until < soonest)
                {
                    soonest = entry.Until;
                    victim = i;
                }
            }

            state = state.WithCooldown(victim, new ClaimCooldown(target, tick + _options.CooldownTicks));
        }

        public override void OnReleased(ref ClaimAgentState state, EntityId target, long tick)
        {
        }

        public override void OnLost(ref ClaimAgentState state, EntityId target, long tick)
        {
            // The target is gone: nothing to cool down, and the agent may look again at once.
            state = state with { CommittedUntil = Math.Min(state.CommittedUntil, tick) };
        }
    }
}
