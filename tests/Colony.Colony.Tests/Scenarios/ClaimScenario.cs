using Colony.Hashing;
using Colony.Testing;

namespace Colony.Jobs.Tests.Scenarios;

/// <summary>A worker agent: which site it prefers and how long its jobs last.</summary>
public struct ClaimWorker
{
    public int Preference;
    public int JobLength;
}

/// <summary>A claimable work site.</summary>
public struct WorkSite
{
    public int Kind;
}

/// <summary>
/// The Claims oracle (spec section 9.2, pattern of ColonistScenario): thousands of workers decide on a
/// hashed cadence under a budget, propose claims on a contended quarter of the work sites (falling back to
/// the other quarters when the policy refuses a target), hold a site for a job's length, release it, and
/// repeat; a tenth of them are "flappers" with one-Tick jobs. Sites and workers churn every Tick so claims
/// are lost and claimants die mid-job. Every draw comes from the World RNG keyed by Entity or Tick; the World
/// hash at checkpoints is the determinism evidence, <see cref="Denied"/> (the denied counter summed over the
/// run) is the thrash measure. No test-framework dependency.
/// </summary>
public sealed class ClaimScenario : Testing.Scenario
{
    public const ulong DefaultSeed = 4242;
    public const string DecideSystemName = "claims.decide";

    private readonly ScenarioState _state = new();
    private ClaimSet? _claims;

    public override string Name => ReferenceEquals(Policy, ClaimPolicy.None) ? "claims-no-policy" : "claims";

    public override ulong Seed { get; init; } = DefaultSeed;

    public int Workers { get; init; } = 20_000;

    public int Sites { get; init; } = 400;

    public int Cadence { get; init; } = 2;

    /// <summary>Decisions per Tick for the decide System; 0 = unlimited.</summary>
    public int Budget { get; init; }

    public int JobLength { get; init; } = 6;

    public ClaimPolicy Policy { get; init; } = ClaimPolicy.Default;

    public override int InitialEntityCapacity => (Workers + Sites) * 2;

    /// <summary>Thrash measure: the resolve System's denied counter summed over every Tick advanced through this scenario.</summary>
    public long Denied { get; private set; }

    public long Granted { get; private set; }

    public long Lost { get; private set; }

    public override void Compose(WorldBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder
            .AddComponent<ClaimWorker>("claims.worker")
            .AddComponent<WorkSite>("claims.site")
            .DebugValidation(false);
        var claims = builder.AddClaims(new ClaimOptions { Policy = Policy, InitialProposalCapacity = Workers });
        _claims = claims;
        var decision = builder.AddDecisionState();
        var worker = builder.Component<ClaimWorker>();
        var site = builder.Component<WorkSite>();
        var state = _state;
        state.Reset(claims, worker, site, decision, Sites);

        builder.AddDecisionSystem(new DecisionSystemDefinition
        {
            Name = DecideSystemName,
            State = decision,
            Agents = ComponentMask.Of(worker).Union(claims.AgentComponents),
            Writes = ComponentMask.Of(worker).Union(claims.AgentComponents),
            Cadence = Cadence,
            BudgetPerTick = Budget,
            Decide = state.Decide,
        });
        // Begin, not Late: the site list is then a function of the World at the Tick boundary, so a World loaded
        // from a save (TASK-14) continues exactly like the unsaved run.
        builder.AddSystem(new SystemDefinition
        {
            Name = "claims.sites",
            Phase = Phase.Begin,
            Reads = ComponentMask.Of(site),
            Begin = state.RebuildSites,
        });
        builder.AddSystem(new SystemDefinition
        {
            Name = "claims.churn",
            Phase = Phase.Late,
            Reads = ComponentMask.Of(site, worker),
            Begin = state.Churn,
        });
    }

    public override void Populate(World world)
    {
        ArgumentNullException.ThrowIfNull(world);
        Denied = 0;
        Granted = 0;
        Lost = 0;
        var buffer = world.CreateCommandBuffer();
        var init = world.Random.Derive("claims-init");
        for (int i = 0; i < Sites; i++)
        {
            _state.CreateSite(buffer, i % 5 == 0 ? 2 : 1);
        }

        for (int i = 0; i < Workers; i++)
        {
            _state.CreateWorker(buffer, init.UInt32At((ulong)i), JobLength, i % 10 == 0);
        }

        world.Playback(buffer);
        world.ResetScratch();
    }

    public override void Advance(World world)
    {
        ArgumentNullException.ThrowIfNull(world);
        world.Tick();
        var counters = world.Diagnostics.Latest.Counters;
        Denied += counters[_claims!.DeniedCounter.Id];
        Granted += counters[_claims.GrantedCounter.Id];
        Lost += counters[_claims.LostCounter.Id];
    }

    private sealed class ScenarioState
    {
        private ClaimSet _claims = null!;
        private ComponentType _worker;
        private ComponentType _site;
        private ComponentType _decision;
        private ComponentMask _workerMask;
        private ComponentMask _siteMask;
        private EntityId[] _sites = [];
        private int _siteCount;
        private int _targetSites;

        public void Reset(ClaimSet claims, ComponentType worker, ComponentType site, ComponentType decision, int sites)
        {
            _claims = claims;
            _worker = worker;
            _site = site;
            _decision = decision;
            _workerMask = ComponentMask.Of(worker, decision).Union(claims.AgentComponents);
            _siteMask = ComponentMask.Of(site, claims.Capacity);
            _sites = new EntityId[sites * 2];
            _siteCount = 0;
            _targetSites = sites;
        }

        public void CreateSite(CommandBuffer buffer, int slots)
        {
            var e = buffer.Create(_siteMask);
            buffer.Add(e, _claims.Capacity, new ClaimCapacity(slots, 0));
        }

        public void CreateWorker(CommandBuffer buffer, uint random, int jobLength, bool flapper)
        {
            var e = buffer.Create(_workerMask);
            // A quarter of the sites is preferred by everyone: 4x contention on those, the rest are the fallbacks.
            buffer.Add(e, _worker, new ClaimWorker
            {
                Preference = (int)(random % (uint)Math.Max(1, _targetSites / 4)),
                JobLength = flapper ? 1 : jobLength + (int)(random % 3),
            });
        }

        /// <summary>Simulate (decision System body): hold until the job ends, then release; otherwise propose, preferred site first.</summary>
        public void Decide(in SystemContext context, in ChunkView chunk, int row)
        {
            var proposals = chunk.Column<ClaimProposal>(_claims.Proposal);
            var grants = chunk.Column<ClaimGrant>(_claims.Grant);
            var states = chunk.Column<ClaimAgentState>(_claims.Agent);
            var workers = chunk.Column<ClaimWorker>(_worker);
            var entity = chunk.Entities[row];
            long tick = context.Tick;

            if (grants[row].IsHeld)
            {
                if (tick >= grants[row].GrantedTick + workers[row].JobLength)
                {
                    _claims.Release(context.Commands, entity, ref grants[row], ref states[row], tick);
                }

                return;
            }

            if (_siteCount == 0)
            {
                return;
            }

            int quarter = Math.Max(1, _siteCount / 4);
            for (int k = 0; k < 4; k++)
            {
                var target = _sites[(workers[row].Preference + (k * quarter)) % _siteCount];
                long priority = (long)(Hash64.Combine((ulong)tick, entity.Value) & 0xFF);
                if (_claims.TryPropose(ref proposals[row], in grants[row], in states[row], target, priority, 0, tick))
                {
                    break;
                }
            }
        }

        /// <summary>Begin (serial): the live sites in chunk order, the list this Tick's decisions pick from.</summary>
        public void RebuildSites(in SystemContext context)
        {
            int count = 0;
            foreach (var chunk in context.World.Query(ComponentMask.Of(_site)))
            {
                var entities = chunk.Entities;
                if (count + entities.Length > _sites.Length)
                {
                    Array.Resize(ref _sites, Math.Max(_sites.Length * 2, count + entities.Length));
                }

                entities.CopyTo(_sites.AsSpan(count));
                count += entities.Length;
            }

            _siteCount = count;
        }

        /// <summary>Late (serial): destroy a few sites and workers picked by Tick-keyed RNG, create replacements.</summary>
        public void Churn(in SystemContext context)
        {
            var commands = context.Commands;
            var random = context.Random;
            int siteChurn = Math.Max(1, _siteCount / 100);
            for (int i = 0; i < siteChurn; i++)
            {
                if (_siteCount > 0)
                {
                    commands.Destroy(_sites[random.Int32At((ulong)i, 0, _siteCount)]);
                }

                CreateSite(commands, i % 5 == 0 ? 2 : 1);
            }

            int workerCount = context.World.Query(ComponentMask.Of(_worker)).EntityCount();
            int workerChurn = Math.Max(1, workerCount / 200);
            int picked = 0;
            foreach (var chunk in context.World.Query(ComponentMask.Of(_worker)))
            {
                var entities = chunk.Entities;
                for (int i = 0; i < entities.Length && picked < workerChurn; i++)
                {
                    // Every Tick a different stripe of workers dies (by slot), replaced by newborns.
                    if (entities[i].Index % 200 == (int)(context.Tick % 200))
                    {
                        commands.Destroy(entities[i]);
                        CreateWorker(commands, random.UInt32At((ulong)(1000 + picked)), 6, picked % 10 == 0);
                        picked++;
                    }
                }
            }
        }
    }
}
