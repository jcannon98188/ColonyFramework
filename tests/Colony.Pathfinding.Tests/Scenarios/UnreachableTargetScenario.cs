using Colony.Numerics;
using Colony.Fields;
using Colony.Testing;

namespace Colony.Pathfinding.Tests.Scenarios;

/// <summary>A wandering agent: where it stands, the goal it wants and how its Reachable queries went.</summary>
public struct WanderAgent
{
    public int X;
    public int Y;
    public int Z;
    public int GoalX;
    public int GoalY;
    public int GoalZ;
    public int Reached;
    public int Unreachable;
}

/// <summary>
/// The unreachable-target oracle (spec section 9.1, pattern of FieldScenario): agents on <see cref="PrototypeTerrain"/>
/// ask <c>Reachable(position, goal, walker)</c> from a parallel Simulate System every Tick; a reachable goal is
/// taken (the agent moves there), an unreachable one is counted and abandoned. A tenth of the goals lie in sealed
/// pockets (never reachable) and a Late System toggles doors before <see cref="PathfindingBuilder.SystemName"/>,
/// so regions merge and split every Tick. Agent state, and so the World hash, depends on every answer.
/// No test-framework dependency.
/// </summary>
public sealed class UnreachableTargetScenario : Testing.Scenario
{
    public const ulong DefaultSeed = 9191;
    public static readonly WorldSize DefaultSize = new(100, 72, 2); // 7 x 5 x 1 chunks with partial edges

    private readonly State _state = new();

    public override string Name => "pathfinding-unreachable";

    public override ulong Seed { get; init; } = DefaultSeed;

    public WorldSize Size { get; init; } = DefaultSize;

    public int Agents { get; init; } = 2000;

    public int DoorTogglesPerTick { get; init; } = 6;

    /// <summary>Per mille of new goals placed in a sealed pocket.</summary>
    public int PocketGoalsPerMille { get; init; } = 100;

    public override int InitialEntityCapacity => Agents * 2;

    public MovementClass Walker => _state.Walker;

    public ComponentType AgentType => _state.Agent;

    public bool IsPocket(int x, int y, int z) => _state.Pocket[PrototypeTerrain.Index(Size, x, y, z)];

    public override void Compose(WorldBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.DebugValidation(false);
        builder.AddComponent<WanderAgent>("pathfinding.agent");
        var fields = builder.AddFields(Size);
        var terrain = fields.AddField<byte>("terrain");
        var walker = builder.AddPathfinding(fields).AddMovementClass(new MovementClassDefinition("walker", Passability.OfField(terrain, PrototypeTerrain.Walkable))
        {
            Vertical = Passability.OfField(terrain, PrototypeTerrain.Climbable),
        }.DependsOn(terrain));
        var agent = builder.Component<WanderAgent>();
        _state.Reset(this, terrain, walker, agent);

        builder.AddSystem(new SystemDefinition
        {
            Name = "pathfinding.wander",
            Phase = Phase.Simulate,
            All = ComponentMask.Of(agent),
            Writes = ComponentMask.Of(agent),
            ForEachChunk = _state.Wander,
        });
        builder.AddSystem(new SystemDefinition
        {
            Name = "pathfinding.doors",
            Phase = Phase.Late,
            Before = [PathfindingBuilder.SystemName],
            Begin = _state.ToggleDoors,
        });
    }

    public override void Populate(World world)
    {
        ArgumentNullException.ThrowIfNull(world);
        PrototypeTerrain.Write(world, _state.Terrain, _state.Tiles);
        var random = world.Random.Derive("pathfinding-agents");
        var buffer = world.CreateCommandBuffer();
        for (int i = 0; i < Agents; i++)
        {
            var e = buffer.Create(ComponentMask.Of(_state.Agent));
            var agent = default(WanderAgent);
            _state.Place(ref agent, _state.Floor[random.Int32At((ulong)(2 * i), 0, _state.Floor.Length)]);
            _state.PickGoal(ref agent, random, (ulong)((2 * i) + 1));
            buffer.Add(e, _state.Agent, agent);
        }

        world.Playback(buffer);
        world.ResetScratch();
        world.Pathfinding().EnsureBuilt(_state.Walker);
    }

    /// <summary>Totals over every agent: goals reached and goals found unreachable.</summary>
    public (long Reached, long Unreachable) Totals(World world)
    {
        ArgumentNullException.ThrowIfNull(world);
        long reached = 0;
        long unreachable = 0;
        foreach (var chunk in world.Query(ComponentMask.Of(_state.Agent)))
        {
            foreach (ref readonly var agent in chunk.Column<WanderAgent>(_state.Agent))
            {
                reached += agent.Reached;
                unreachable += agent.Unreachable;
            }
        }

        return (reached, unreachable);
    }

    private sealed class State
    {
        private UnreachableTargetScenario _owner = null!;
        private int[] _doors = [];

        public Field<byte> Terrain { get; private set; }

        public MovementClass Walker { get; private set; }

        public ComponentType Agent { get; private set; }

        public byte[] Tiles { get; private set; } = [];

        public bool[] Pocket { get; private set; } = [];

        /// <summary>Flat indices of open floor outside pockets (agent starts, ordinary goals).</summary>
        public int[] Floor { get; private set; } = [];

        public int[] PocketTiles { get; private set; } = [];

        public void Reset(UnreachableTargetScenario owner, Field<byte> terrain, MovementClass walker, ComponentType agent)
        {
            _owner = owner;
            Terrain = terrain;
            Walker = walker;
            Agent = agent;
            var size = owner.Size;
            Pocket = new bool[size.X * size.Y * size.Z];
            Tiles = PrototypeTerrain.Generate(size, Pocket);
            var floor = new List<int>();
            var pockets = new List<int>();
            var doors = new List<int>();
            for (int i = 0; i < Tiles.Length; i++)
            {
                if (Pocket[i])
                {
                    pockets.Add(i);
                }
                else if (Tiles[i] == PrototypeTerrain.Floor)
                {
                    floor.Add(i);
                }
                else if (Tiles[i] is PrototypeTerrain.OpenDoor or PrototypeTerrain.ClosedDoor)
                {
                    doors.Add(i);
                }
            }

            Floor = [.. floor];
            PocketTiles = [.. pockets];
            _doors = [.. doors];
        }

        public void Place(ref WanderAgent agent, int index)
        {
            var size = _owner.Size;
            agent.X = index % size.X;
            agent.Y = index / size.X % size.Y;
            agent.Z = index / (size.X * size.Y);
        }

        public void PickGoal(ref WanderAgent agent, RandomSource random, ulong key)
        {
            var size = _owner.Size;
            bool pocket = PocketTiles.Length > 0 && random.Int32At(key << 2, 0, 1000) < _owner.PocketGoalsPerMille;
            int index = pocket
                ? PocketTiles[random.Int32At((key << 2) + 1, 0, PocketTiles.Length)]
                : Floor[random.Int32At((key << 2) + 2, 0, Floor.Length)];
            agent.GoalX = index % size.X;
            agent.GoalY = index / size.X % size.Y;
            agent.GoalZ = index / (size.X * size.Y);
        }

        /// <summary>Simulate (parallel): one Reachable query per agent; take the goal or count it unreachable, then pick the next.</summary>
        public void Wander(in SystemContext context, in ChunkView chunk)
        {
            var pathfinding = context.World.Pathfinding();
            var agents = chunk.Column<WanderAgent>(Agent);
            var entities = chunk.Entities;
            for (int row = 0; row < agents.Length; row++)
            {
                ref var agent = ref agents[row];
                var goal = new TilePos(agent.GoalX, agent.GoalY, agent.GoalZ);
                if (pathfinding.Reachable(new TilePos(agent.X, agent.Y, agent.Z), goal, Walker))
                {
                    agent.X = goal.X;
                    agent.Y = goal.Y;
                    agent.Z = goal.Z;
                    agent.Reached++;
                }
                else
                {
                    agent.Unreachable++;
                }

                PickGoal(ref agent, context.Random, entities[row].Value);
            }
        }

        /// <summary>Late, before region upkeep: flip a few doors chosen by the Tick's random stream.</summary>
        public void ToggleDoors(in SystemContext context)
        {
            if (_doors.Length == 0)
            {
                return;
            }

            var size = _owner.Size;
            var accessor = context.World.Fields().Accessor(Terrain);
            for (int i = 0; i < _owner.DoorTogglesPerTick; i++)
            {
                int index = _doors[context.Random.Int32At((ulong)i, 0, _doors.Length)];
                var tile = new TilePos(index % size.X, index / size.X % size.Y, index / (size.X * size.Y));
                accessor[tile] = accessor[tile] == PrototypeTerrain.OpenDoor ? PrototypeTerrain.ClosedDoor : PrototypeTerrain.OpenDoor;
            }
        }
    }
}
