namespace Colony.Jobs;

/// <summary>
/// An agent's pending proposal (spec section 9.2): the target it wants to claim, its priority key
/// (ascending: the smallest key wins, ties by claimant <see cref="EntityId"/>) and the amount it asks for
/// (0 when the target does not meter an amount). Written by the agent's own Simulate job through
/// <see cref="ClaimSet.TryPropose"/>; read and cleared by the Begin-Phase resolve System of the next Tick. A
/// <see cref="EntityId.Null"/> target means "no proposal". Plain saved Component.
/// </summary>
public readonly record struct ClaimProposal(EntityId Target, long Priority, int Amount)
{
    public bool IsEmpty => Target.IsNull;
}

/// <summary>
/// What an agent currently holds (spec section 9.2): the claimed target (mirrors the Claim Relation's
/// link for cheap reads from chunk jobs), the slot granted out of the target's <see cref="ClaimCapacity.Slots"/>,
/// the amount reserved and the Tick of the grant. Zeroed when nothing is held. Plain saved Component.
/// </summary>
public readonly record struct ClaimGrant(EntityId Target, int Slot, int Amount, long GrantedTick)
{
    public bool IsHeld => !Target.IsNull;
}

/// <summary>
/// Per-target capacity (spec section 9.2): how many claimants may hold it at once (slots 0..<see cref="Slots"/>-1)
/// and, when <see cref="Amount"/> is positive, the total amount their grants may reserve. A target without
/// this Component has one slot and no metered amount. At most <see cref="MaxSlots"/> slots are tracked.
/// </summary>
public readonly record struct ClaimCapacity(int Slots, int Amount)
{
    /// <summary>Slots are tracked in a 64-bit mask; larger values are clamped.</summary>
    public const int MaxSlots = 64;

    /// <summary>What a target without the Component gets: one slot, no metered amount.</summary>
    public static ClaimCapacity OneSlot => new(1, 0);
}

/// <summary>One cooled-down target of an agent: proposals to <see cref="Target"/> are refused until Tick <see cref="Until"/>.</summary>
public readonly record struct ClaimCooldown(EntityId Target, long Until);

/// <summary>
/// The anti-thrash state of one agent (spec section 9.2), owned by the <see cref="ClaimPolicy"/>: the Tick
/// until which it is committed after a grant, the Tick until which the job-start circuit breaker bars it,
/// the breaker's start counter and window, and four cooled-down targets. Plain saved Component; a Game policy
/// may interpret the fields as it likes.
/// </summary>
public readonly record struct ClaimAgentState(
    long CommittedUntil,
    long BarredUntil,
    long WindowStart,
    int RecentStarts,
    ClaimCooldown Cooldown0,
    ClaimCooldown Cooldown1,
    ClaimCooldown Cooldown2,
    ClaimCooldown Cooldown3)
{
    /// <summary>Number of cooldown entries kept per agent.</summary>
    public const int CooldownEntries = 4;

    public ClaimCooldown CooldownAt(int index) => index switch
    {
        0 => Cooldown0,
        1 => Cooldown1,
        2 => Cooldown2,
        3 => Cooldown3,
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };

    public ClaimAgentState WithCooldown(int index, ClaimCooldown value) => index switch
    {
        0 => this with { Cooldown0 = value },
        1 => this with { Cooldown1 = value },
        2 => this with { Cooldown2 = value },
        3 => this with { Cooldown3 = value },
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };

    /// <summary>True when <paramref name="target"/> is cooled down at <paramref name="tick"/>.</summary>
    public bool IsCooledDown(EntityId target, long tick)
    {
        for (int i = 0; i < CooldownEntries; i++)
        {
            var entry = CooldownAt(i);
            if (entry.Target == target && entry.Until > tick)
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>Why a proposal was denied (<see cref="ClaimDenied"/>).</summary>
public enum ClaimDenialReason : byte
{
    /// <summary>Every slot of the target is held or was granted earlier in the same round.</summary>
    Capacity = 1,

    /// <summary>The requested amount exceeds what the target has left.</summary>
    Amount = 2,

    /// <summary>The target died between the proposal and the resolve step.</summary>
    TargetGone = 3,

    /// <summary>The claimant already holds a claim; release it before proposing.</summary>
    Holding = 4,
}

/// <summary>Event: a proposal was granted in the Begin Phase; the Claim Relation and <see cref="ClaimGrant"/> are set.</summary>
public readonly record struct ClaimGranted(EntityId Claimant, EntityId Target, int Slot, int Amount);

/// <summary>Event: a proposal was denied; the agent rescans at its next decision.</summary>
public readonly record struct ClaimDenied(EntityId Claimant, EntityId Target, ClaimDenialReason Reason);

/// <summary>Event: a held claim vanished because its target was destroyed; the claimant's <see cref="ClaimGrant"/> was cleared.</summary>
public readonly record struct ClaimLost(EntityId Claimant, EntityId Target);

/// <summary>Running totals of the resolve System, saved and hashed with the World.</summary>
public readonly record struct ClaimStats(long Proposals, long Granted, long Denied, long Lost);
