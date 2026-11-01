using Colony.Fields;
using Colony.Testing;

namespace Colony.Pathfinding.Tests.Scenarios;

/// <summary>An agent walking a flow Field toward one of the shared goal sets.</summary>
public struct FlowAgent
{
    public int X;
    public int Y;
    public int Z;
    public int GoalSet;
    public int Steps;
    public int Reached;

    /// <summary>Times the agent stood where no Field led anywhere (a door closed on it, an evicted Field) and was placed afresh.</summary>
    public int Lost;
}

/// <summary>
/// The flow Field scenario (spec section 9.1, TASK-21 AC #3): agents on <see cref="PrototypeTerrain"/> walk toward
/// a few shared goal sets through <see cref="PathfindingService.TryNextStep(FlowField, TilePos, out TilePos)"/>
/// from a parallel Simulate System (the Field inside its bound, HPA* outside), while a Late System toggles doors
/// every Tick so chunks go dirty and the Begin repair pass runs. The Fields are resolved once per Tick on the Tick
/// thread. With <see cref="TightBudget"/> the memory budget holds one Field fewer than the goal sets, so eviction
/// and rebuild happen every Tick. Agent state, and so the World hash, depends on every step.
/// </summary>
public sealed class FlowFieldScenario : Testing.Scenario
{
    public const ulong DefaultSeed = 2121;
    public static readonly WorldSize DefaultSize = new(100, 72, 2);

    private readonly State _state = new();

    public override string Name => TightBudget ? "pathfinding-flow-tight" : "pathfinding-flow";

    public override ulong Seed { get; init; } = DefaultSeed;

    public WorldSize Size { get; init; } = DefaultSize;

    public int Agents { get; init; } = 400;

    public int GoalSets { get; init; } = 4;

    public int GoalsPerSet { get; init; } = 3;

    public int RadiusChunks { get; init; } = 1;

    public int DoorTogglesPerTick { get; init; } = 6;

    /// <summary>Face moves an agent makes per Tick.</summary>
    public int Speed { get; init; } = 3;

    /// <summary>Budget for one Field fewer than <see cref="GoalSets"/>, so the least recently used one is evicted and rebuilt every Tick.</summary>
    public bool TightBudget { get; init; }

    public override int InitialEntityCapacity => Agents * 2;

    public MovementClass Walker => _state.Walker;

    public ComponentType AgentType => _state.Agent;

    public override void Compose(WorldBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.DebugValidation(false);
        builder.AddComponent<FlowAgent>("pathfinding.flow-agent");
        var fields = builder.AddFields(Size);
        var terrain = fields.AddField<byte>("terrain");
        // A Field spans its goal chunks plus the radius, clipped to the World; the budget holds GoalSets - 1 of the widest one a spread goal set can have.
        int widest = Math.Min(Size.ChunksX, (2 * RadiusChunks) + 1 + (Size.ChunksX / 2)) * Math.Min(Size.ChunksY, (2 * RadiusChunks) + 1 + (Size.ChunksY / 2)) * Size.ChunksZ;
        var options = TightBudget
            ? new PathfindingOptions { FlowFieldMemoryBudgetBytes = (GoalSets - 1) * FlowField.BytesFor(widest, GoalsPerSet) }
            : new PathfindingOptions();
        var pathfinding = builder.AddPathfinding(fields, options);
        var walker = pathfinding.AddMovementClass(new MovementClassDefinition("walker", Passability.OfField(terrain, PrototypeTerrain.Walkable))
        {
            Vertical = Passability.OfField(terrain, PrototypeTerrain.Climbable),
        }.DependsOn(terrain));
        var agent = builder.Component<FlowAgent>();
        _state.Reset(this, terrain, walker, agent);

        builder.AddSystem(new SystemDefinition
        {
            Name = "pathfinding.flow-walkers",
            Phase = Phase.Simulate,
            All = ComponentMask.Of(agent),
            Writes = ComponentMask.Of(agent),
            Begin = _state.ResolveFields,
            ForEachChunk = _state.Step,
        });
        builder.AddSystem(new SystemDefinition
        {
            Name = "pathfinding.flow-doors",
            Phase = Phase.Late,
            Before = [PathfindingBuilder.SystemName],
            Begin = _state.ToggleDoors,
        });
    }

    public override void Populate(World world)
    {
        ArgumentNullException.ThrowIfNull(world);
        PrototypeTerrain.Write(world, _state.Terrain, _state.Tiles);
        var random = world.Random.Derive("pathfinding-flow-agents");
        var buffer = world.CreateCommandBuffer();
        for (int i = 0; i < Agents; i++)
        {
            var e = buffer.Create(ComponentMask.Of(_state.Agent));
            var agent = default(FlowAgent);
            _state.Place(ref agent, random.Int32At((ulong)(2 * i), 0, _state.Floor.Length));
            agent.GoalSet = random.Int32At((ulong)((2 * i) + 1), 0, GoalSets);
            buffer.Add(e, _state.Agent, agent);
        }

        world.Playback(buffer);
        world.ResetScratch();
        world.Pathfinding().EnsureBuilt(_state.Walker);
    }

    /// <summary>Totals over every agent.</summary>
    public (long Steps, long Reached, long Lost) Totals(World world)
    {
        ArgumentNullException.ThrowIfNull(world);
        long steps = 0;
        long reached = 0;
        long lost = 0;
        foreach (var chunk in world.Query(ComponentMask.Of(_state.Agent)))
        {
            foreach (ref readonly var agent in chunk.Column<FlowAgent>(_state.Agent))
            {
                steps += agent.Steps;
                reached += agent.Reached;
                lost += agent.Lost;
            }
        }

        return (steps, reached, lost);
    }

    private sealed class State
    {
        private FlowFieldScenario _owner = null!;
        private int[] _doors = [];
        private TilePos[][] _goals = [];
        private FlowField[] _fields = [];

        public Field<byte> Terrain { get; private set; }

        public MovementClass Walker { get; private set; }

        public ComponentType Agent { get; private set; }

        public byte[] Tiles { get; private set; } = [];

        public int[] Floor { get; private set; } = [];

        public void Reset(FlowFieldScenario owner, Field<byte> terrain, MovementClass walker, ComponentType agent)
        {
            _owner = owner;
            Terrain = terrain;
            Walker = walker;
            Agent = agent;
            var size = owner.Size;
            var pocket = new bool[size.X * size.Y * size.Z];
            Tiles = PrototypeTerrain.Generate(size, pocket);
            var floor = new List<int>();
            var doors = new List<int>();
            for (int i = 0; i < Tiles.Length; i++)
            {
                if (!pocket[i] && Tiles[i] == PrototypeTerrain.Floor)
                {
                    floor.Add(i);
                }
                else if (Tiles[i] is PrototypeTerrain.OpenDoor or PrototypeTerrain.ClosedDoor)
                {
                    doors.Add(i);
                }
            }

            Floor = [.. floor];
            _doors = [.. doors];

            // Goal sets are a function of the layout: spread over the floor by a fixed stride.
            _goals = new TilePos[owner.GoalSets][];
            _fields = new FlowField[owner.GoalSets];
            int stride = Floor.Length / (owner.GoalSets * owner.GoalsPerSet + 1);
            for (int set = 0; set < owner.GoalSets; set++)
            {
                _goals[set] = new TilePos[owner.GoalsPerSet];
                for (int g = 0; g < owner.GoalsPerSet; g++)
                {
                    _goals[set][g] = TileAt(Floor[((set * owner.GoalsPerSet) + g + 1) * stride]);
                }
            }
        }

        public TilePos TileAt(int index)
        {
            var size = _owner.Size;
            return new TilePos(index % size.X, index / size.X % size.Y, index / (size.X * size.Y));
        }

        public void Place(ref FlowAgent agent, int floorIndex)
        {
            var tile = TileAt(Floor[floorIndex]);
            agent.X = tile.X;
            agent.Y = tile.Y;
            agent.Z = tile.Z;
        }

        /// <summary>Begin (Tick thread): the Field of every goal set, built or found; a dirty one was repaired by the Begin repair pass or waits for its Tick.</summary>
        public void ResolveFields(in SystemContext context)
        {
            var pathfinding = context.World.Pathfinding();
            for (int set = 0; set < _fields.Length; set++)
            {
                _fields[set] = pathfinding.FlowField(_goals[set], Walker, _owner.RadiusChunks);
            }
        }

        /// <summary>Simulate (parallel): walk up to Speed face moves along the goal set's Field.</summary>
        public void Step(in SystemContext context, in ChunkView chunk)
        {
            var pathfinding = context.World.Pathfinding();
            var agents = chunk.Column<FlowAgent>(Agent);
            var entities = chunk.Entities;
            for (int row = 0; row < agents.Length; row++)
            {
                ref var agent = ref agents[row];
                ulong key = entities[row].Value << 2;
                for (int move = 0; move < _owner.Speed; move++)
                {
                    var field = _fields[agent.GoalSet];
                    var at = new TilePos(agent.X, agent.Y, agent.Z);
                    switch (pathfinding.TryNextStep(field, at, out var next))
                    {
                        case FlowStepStatus.Step:
                            agent.X = next.X;
                            agent.Y = next.Y;
                            agent.Z = next.Z;
                            agent.Steps++;
                            continue;
                        case FlowStepStatus.AtGoal:
                            agent.Reached++;
                            agent.GoalSet = (agent.GoalSet + 1 + context.Random.Int32At(key | 1, 0, _fields.Length - 1)) % _fields.Length;
                            break;
                        default:
                            agent.Lost++;
                            Place(ref agent, context.Random.Int32At(key | 2, 0, Floor.Length));
                            agent.GoalSet = context.Random.Int32At(key | 3, 0, _fields.Length);
                            break;
                    }

                    break;
                }
            }
        }

        /// <summary>Late, before region upkeep: flip a few doors chosen by the Tick's random stream.</summary>
        public void ToggleDoors(in SystemContext context)
        {
            var accessor = context.World.Fields().Accessor(Terrain);
            for (int i = 0; i < _owner.DoorTogglesPerTick; i++)
            {
                var tile = TileAt(_doors[context.Random.Int32At((ulong)i, 0, _doors.Length)]);
                accessor[tile] = accessor[tile] == PrototypeTerrain.OpenDoor ? PrototypeTerrain.ClosedDoor : PrototypeTerrain.OpenDoor;
            }
        }
    }
}
