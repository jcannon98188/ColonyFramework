using System.Runtime.InteropServices;
using Colony.Hashing;

namespace Colony.Jobs;

/// <summary>
/// Per-agent decision flags (spec section 9.2 decision scheduling), registered once with
/// <see cref="DecisionBuilderExtensions.AddDecisionState"/> and carried by every agent a decision System
/// visits. <see cref="Triggered"/> is set by Game Systems to wake the agent at its next decision System run
/// whatever the cadence says; <see cref="Pending"/> is owned by the decision System: due agents that the
/// budget could not serve stay pending and go first next Tick. Plain saved Component.
/// </summary>
public readonly record struct DecisionState(byte Flags)
{
    public const byte TriggeredFlag = 1;
    public const byte PendingFlag = 2;

    public bool Triggered => (Flags & TriggeredFlag) != 0;

    public bool Pending => (Flags & PendingFlag) != 0;

    /// <summary>What a Game System writes to wake the agent next decision.</summary>
    public static DecisionState Trigger => new(TriggeredFlag);
}

/// <summary>
/// The saved budget state of one decision System (Resource named <c>&lt;system&gt;.budget</c>): the remainder
/// carried into the next Tick and last Tick's tallies. Hashed with the World.
/// </summary>
public readonly record struct DecisionBudget(long Carry, long LastDue, long LastDecided, long LastDeferred, long TotalDecided);

/// <summary>Per-agent decision body: <paramref name="row"/> of <paramref name="chunk"/> is awake and inside the budget.</summary>
public delegate void DecisionAction(in SystemContext context, in ChunkView chunk, int row);

/// <summary>
/// A decision System (spec section 9.2): wakes agents on a hashed cadence or a trigger and runs
/// <see cref="Decide"/> for each, under a per-Tick budget whose remainder carries forward. Registered with
/// <see cref="DecisionBuilderExtensions.AddDecisionSystem"/>; the ordinary System fields describe the body's
/// access and ordering. Agents are the Entities with <see cref="Agents"/> and <see cref="State"/>.
/// </summary>
public sealed class DecisionSystemDefinition
{
    public required string Name { get; init; }

    public Phase Phase { get; init; } = Phase.Simulate;

    /// <summary>The <see cref="DecisionState"/> Component from <see cref="DecisionBuilderExtensions.AddDecisionState"/>.</summary>
    public required ComponentType State { get; init; }

    /// <summary>Components an agent chunk must have (besides <see cref="State"/>).</summary>
    public required ComponentMask Agents { get; init; }

    public ComponentMask None { get; init; }

    /// <summary>Components the body reads (<see cref="State"/> is added for you).</summary>
    public ComponentMask Reads { get; init; }

    /// <summary>Components the body writes (<see cref="State"/> is added for you).</summary>
    public ComponentMask Writes { get; init; }

    public ResourceMask ReadsResources { get; init; }

    public ResourceMask WritesResources { get; init; }

    public IReadOnlyList<string> After { get; init; } = [];

    public IReadOnlyList<string> Before { get; init; } = [];

    /// <summary>An agent is due every this many Ticks, on a Tick derived from its id (<see cref="DecisionCadence.IsDue"/>). 1 = every Tick.</summary>
    public int Cadence { get; init; } = 8;

    /// <summary>Decisions this System may run per Tick; 0 = unlimited.</summary>
    public int BudgetPerTick { get; init; }

    /// <summary>Cap on the unused budget carried into the next Tick; -1 = one Tick's budget.</summary>
    public int MaxCarry { get; init; } = -1;

    public required DecisionAction Decide { get; init; }
}

/// <summary>The hashed wake cadence of spec section 9.2: deterministic, keyed by the Entity id and the System, never by thread or time.</summary>
public static class DecisionCadence
{
    private const ulong SaltSeed = 0x4443_534EUL; // "DCSN"

    /// <summary>The per-System salt: XXH64 of the System name.</summary>
    public static ulong Salt(string systemName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(systemName);
        return Hash64.Compute(MemoryMarshal.AsBytes(systemName.AsSpan()), SaltSeed);
    }

    /// <summary>True on exactly one Tick in every window of <paramref name="cadence"/> consecutive Ticks, the offset hashed from the Entity id and the salt.</summary>
    public static bool IsDue(EntityId entity, long tick, int cadence, ulong salt)
    {
        if (cadence <= 1)
        {
            return true;
        }

        ulong offset = Hash64.Combine(salt, entity.Value) % (ulong)cadence;
        return (ulong)(tick % cadence) == offset;
    }
}

public static class DecisionBuilderExtensions
{
    public const string StateComponentName = "colony.decision";

    /// <summary>Registers the <see cref="DecisionState"/> Component once per schema; pass it to every <see cref="DecisionSystemDefinition.State"/>.</summary>
    public static ComponentType AddDecisionState(this WorldBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.AddComponent<DecisionState>(StateComponentName);
        return builder.Component<DecisionState>();
    }

    /// <summary>
    /// Registers a decision System and its <c>&lt;name&gt;.budget</c> Resource. Each Tick the System marks due
    /// agents pending (cadence or trigger) in chunk order, grants the budget in that order, runs
    /// <see cref="DecisionSystemDefinition.Decide"/> for the granted ones in parallel chunk jobs, and carries
    /// the unused budget forward; agents left pending go first next Tick.
    /// </summary>
    public static ResourceType AddDecisionSystem(this WorldBuilder builder, DecisionSystemDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentException.ThrowIfNullOrWhiteSpace(definition.Name, nameof(definition));
        ArgumentNullException.ThrowIfNull(definition.Decide, nameof(definition));
        ArgumentOutOfRangeException.ThrowIfLessThan(definition.Cadence, 1, nameof(definition));
        ArgumentOutOfRangeException.ThrowIfNegative(definition.BudgetPerTick, nameof(definition));
        ArgumentOutOfRangeException.ThrowIfLessThan(definition.MaxCarry, -1, nameof(definition));

        string budgetName = definition.Name + ".budget";
        builder.AddResource<DecisionBudget>(budgetName);
        var budget = builder.Resource(budgetName);
        var runner = new DecisionRunner(definition, budget);
        builder.AddSystem(new SystemDefinition
        {
            Name = definition.Name,
            Phase = definition.Phase,
            All = definition.Agents.With(definition.State),
            None = definition.None,
            Reads = definition.Reads,
            Writes = definition.Writes.With(definition.State),
            ReadsResources = definition.ReadsResources,
            WritesResources = definition.WritesResources.With(budget),
            After = definition.After,
            Before = definition.Before,
            IgnoreEnableBits = true,
            Begin = runner.Begin,
            ForEachChunk = runner.ForEachChunk,
            End = runner.End,
        });
        return budget;
    }
}

/// <summary>
/// Runtime of one decision System. Begin (serial): walk the agent chunks in job order, mark due agents
/// pending, and hand each chunk the slice of the budget it gets, in that order (so the split never depends
/// on which worker runs what). Chunk jobs: run the body for pending rows until the chunk's slice is spent.
/// End: carry the remainder forward into the budget Resource.
/// </summary>
internal sealed class DecisionRunner
{
    private readonly DecisionSystemDefinition _definition;
    private readonly ResourceType _budget;
    private readonly ComponentMask _all;
    private readonly ulong _salt;
    private int[] _allowed = new int[64];
    private EntityId[] _firstEntity = new EntityId[64];
    private int[] _decided = new int[64];
    private long _due;
    private long _remaining;
    private bool _unlimited;

    public DecisionRunner(DecisionSystemDefinition definition, ResourceType budget)
    {
        _definition = definition;
        _budget = budget;
        _all = definition.Agents.With(definition.State);
        _salt = DecisionCadence.Salt(definition.Name);
    }

    public void Begin(in SystemContext context)
    {
        int jobs = context.JobCount;
        if (_allowed.Length < jobs)
        {
            Array.Resize(ref _allowed, Math.Max(jobs, _allowed.Length * 2));
            Array.Resize(ref _firstEntity, _allowed.Length);
            Array.Resize(ref _decided, _allowed.Length);
        }

        var state = _definition.State;
        long tick = context.Tick;
        int cadence = _definition.Cadence;
        _unlimited = _definition.BudgetPerTick == 0;
        _remaining = _unlimited ? long.MaxValue : _definition.BudgetPerTick + context.ReadResource<DecisionBudget>(_budget).Carry;
        _due = 0;

        // Same chunk order as the System's jobs (archetype id, then chunk; enable bits ignored on both sides).
        int k = 0;
        foreach (var chunk in context.World.Query(_all, _definition.None, ignoreEnableBits: true))
        {
            var entities = chunk.Entities;
            var states = chunk.Column<DecisionState>(state);
            int due = 0;
            for (int i = 0; i < entities.Length; i++)
            {
                var flags = states[i];
                if (flags.Pending || flags.Triggered || DecisionCadence.IsDue(entities[i], tick, cadence, _salt))
                {
                    states[i] = new DecisionState(DecisionState.PendingFlag);
                    due++;
                }
            }

            if (k >= jobs)
            {
                throw new InvalidOperationException($"Decision System '{_definition.Name}': the chunk query visited more chunks than the scheduler gathered jobs.");
            }

            int allowed = (int)Math.Min(due, _remaining);
            _allowed[k] = allowed;
            _firstEntity[k] = entities.Length > 0 ? entities[0] : EntityId.Null;
            _decided[k] = 0;
            _remaining -= allowed;
            _due += due;
            k++;
        }

        if (k != jobs)
        {
            throw new InvalidOperationException($"Decision System '{_definition.Name}': the chunk query visited {k} chunks but the scheduler gathered {jobs} jobs.");
        }
    }

    public void ForEachChunk(in SystemContext context, in ChunkView chunk)
    {
        int job = context.JobIndex;
        var entities = chunk.Entities;
        if (entities.Length == 0 || entities[0] != _firstEntity[job])
        {
            throw new InvalidOperationException($"Decision System '{_definition.Name}': job {job} received a chunk the budget pass did not see in that position.");
        }

        int quota = _allowed[job];
        if (quota == 0)
        {
            return;
        }

        var states = chunk.Column<DecisionState>(_definition.State);
        var decide = _definition.Decide;
        int decided = 0;
        for (int i = 0; i < states.Length && decided < quota; i++)
        {
            if (!states[i].Pending)
            {
                continue;
            }

            states[i] = default;
            decide(in context, in chunk, i);
            decided++;
        }

        _decided[job] = decided;
    }

    public void End(in SystemContext context)
    {
        long decided = 0;
        for (int j = 0; j < context.JobCount; j++)
        {
            decided += _decided[j];
        }

        long carry = 0;
        if (!_unlimited)
        {
            long maxCarry = _definition.MaxCarry < 0 ? _definition.BudgetPerTick : _definition.MaxCarry;
            carry = Math.Min(_remaining, maxCarry);
        }

        ref var budget = ref context.Resource<DecisionBudget>(_budget);
        budget = new DecisionBudget(carry, _due, decided, _due - decided, budget.TotalDecided + decided);
    }
}
