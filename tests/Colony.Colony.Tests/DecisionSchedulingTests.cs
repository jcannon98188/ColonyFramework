namespace Colony.Jobs.Tests;

/// <summary>AC #3: the hashed cadence and triggers wake the expected agents; the per-System budget carries remainders forward.</summary>
public sealed class DecisionSchedulingTests
{
    private const string SystemName = "test.decide";

    private sealed class Harness : IDisposable
    {
        public Harness(int agents, int cadence, int budgetPerTick, int maxCarry = -1)
        {
            var builder = new WorldBuilder().AddComponent<AgentTag>("test.agent").DebugValidation(false);
            State = builder.AddDecisionState();
            var decided = Decided;
            Budget = builder.AddDecisionSystem(new DecisionSystemDefinition
            {
                Name = SystemName,
                State = State,
                Agents = ComponentMask.Of(builder.Component<AgentTag>()),
                Cadence = cadence,
                BudgetPerTick = budgetPerTick,
                MaxCarry = maxCarry,
                Decide = (in SystemContext context, in ChunkView chunk, int row) =>
                {
                    lock (decided)
                    {
                        decided.Add(chunk.Entities[row]);
                    }
                },
            });
            World = builder.Build().CreateWorld(new WorldOptions { Seed = 3, WorkerThreads = 2 });
            Agents = ClaimTestWorld.Spawn(World, ComponentMask.Of(World.Schema.Component<AgentTag>(), State), agents);
        }

        public World World { get; }

        public ComponentType State { get; }

        public ResourceType Budget { get; }

        public EntityId[] Agents { get; }

        public List<EntityId> Decided { get; } = [];

        public HashSet<EntityId> TickOnce()
        {
            Decided.Clear();
            World.Tick();
            return [.. Decided];
        }

        /// <summary>The independent model of who is due: the public cadence predicate over every agent.</summary>
        public HashSet<EntityId> Due(long tick)
        {
            ulong salt = DecisionCadence.Salt(SystemName);
            return Agents.Where(a => DecisionCadence.IsDue(a, tick, _cadence, salt)).ToHashSet();
        }

        private int _cadence;

        public Harness WithCadence(int cadence)
        {
            _cadence = cadence;
            return this;
        }

        public void Dispose() => World.Dispose();
    }

    [Fact]
    public void Every_agent_is_due_exactly_once_per_cadence_window_and_the_offsets_spread()
    {
        ulong salt = DecisionCadence.Salt(SystemName);
        var seen = new int[8];
        for (uint i = 1; i <= 800; i++)
        {
            var entity = new EntityId(((ulong)1 << 32) | i);
            int dueTicks = 0;
            for (long tick = 100; tick < 108; tick++)
            {
                if (DecisionCadence.IsDue(entity, tick, 8, salt))
                {
                    dueTicks++;
                    seen[tick - 100]++;
                }
            }

            Assert.Equal(1, dueTicks);
        }

        Assert.All(seen, n => Assert.InRange(n, 60, 140));
        Assert.NotEqual(DecisionCadence.Salt("a"), DecisionCadence.Salt("b"));
    }

    [Fact]
    public void Without_a_budget_exactly_the_due_agents_decide_each_tick_and_a_trigger_wakes_one_early()
    {
        using var h = new Harness(agents: 200, cadence: 8, budgetPerTick: 0).WithCadence(8);
        for (long tick = 0; tick < 8; tick++)
        {
            var expected = h.Due(tick);
            if (tick == 3)
            {
                // Trigger an agent that is not due this Tick: it decides now and its flag clears.
                var off = h.Agents.First(a => !expected.Contains(a) && !DecisionCadence.IsDue(a, 4, 8, DecisionCadence.Salt(SystemName)));
                h.World.Get<DecisionState>(off, h.State) = DecisionState.Trigger;
                expected.Add(off);
            }

            var decided = h.TickOnce();
            Assert.Equal(expected, decided);
            Assert.Equal(expected.Count, h.World.Resource<DecisionBudget>(h.Budget).LastDecided);
            Assert.Equal(0, h.World.Resource<DecisionBudget>(h.Budget).Carry);
        }

        Assert.All(h.Agents, a => Assert.False(h.World.Get<DecisionState>(a, h.State).Triggered));
    }

    [Fact]
    public void A_budget_defers_the_overflow_as_pending_and_carries_the_unused_remainder_forward()
    {
        const int budget = 6;
        using var h = new Harness(agents: 120, cadence: 4, budgetPerTick: budget, maxCarry: budget).WithCadence(4);
        var pending = new HashSet<EntityId>();
        long carry = 0;
        for (long tick = 0; tick < 24; tick++)
        {
            var due = h.Due(tick);
            due.UnionWith(pending);
            long available = budget + carry;
            int expectedDecided = (int)Math.Min(due.Count, available);

            var decided = h.TickOnce();

            Assert.Equal(expectedDecided, decided.Count);
            Assert.Subset(due, decided);
            carry = Math.Min(available - expectedDecided, budget);
            pending = due.Except(decided).ToHashSet();
            var state = h.World.Resource<DecisionBudget>(h.Budget);
            Assert.Equal(carry, state.Carry);
            Assert.Equal(due.Count, state.LastDue);
            Assert.Equal(pending.Count, state.LastDeferred);
            Assert.All(pending, p => Assert.True(h.World.Get<DecisionState>(p, h.State).Pending));
        }
    }

    [Fact]
    public void Remainders_accumulate_up_to_the_carry_cap_and_are_spent_on_a_burst()
    {
        using var h = new Harness(agents: 3, cadence: 1, budgetPerTick: 10, maxCarry: 15).WithCadence(1);
        h.TickOnce();
        Assert.Equal(7, h.World.Resource<DecisionBudget>(h.Budget).Carry);
        h.TickOnce();
        Assert.Equal(14, h.World.Resource<DecisionBudget>(h.Budget).Carry);
        h.TickOnce();
        Assert.Equal(15, h.World.Resource<DecisionBudget>(h.Budget).Carry);

        // 22 more agents arrive: 25 due, 10 + 15 available, everyone decides and nothing is left to carry.
        ClaimTestWorld.Spawn(h.World, ComponentMask.Of(h.World.Schema.Component<AgentTag>(), h.State), 22);
        Assert.Equal(25, h.TickOnce().Count);
        Assert.Equal(0, h.World.Resource<DecisionBudget>(h.Budget).Carry);
        Assert.Equal(10, h.TickOnce().Count);
        Assert.Equal(15, h.World.Resource<DecisionBudget>(h.Budget).LastDeferred);
    }
}
