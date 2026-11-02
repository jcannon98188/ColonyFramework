using System.Runtime.CompilerServices;
using Colony.Fields;
using Colony.Numerics;
using Colony.Testing;

namespace Colony.Pathfinding.Tests.Scenarios;

/// <summary>Up to this many waypoints of a result are kept per agent; a longer path is followed to the last kept one and then re-requested.</summary>
[InlineArray(PathAgent.MaxWaypoints)]
public struct WaypointBuffer
{
    private TilePos _element0;
}

/// <summary>An agent that requests paths, follows the results and counts how each request ended.</summary>
public struct PathAgent
{
    public const int MaxWaypoints = 32;

    public PathFollowState Follow;
    public int GoalX;
    public int GoalY;
    public int GoalZ;

    /// <summary>Requests emitted so far; the latest one's tag.</summary>
    public uint Requests;

    /// <summary>1 while a request is out.</summary>
    public byte Pending;

    public int WaypointCount;
    public int Reached;
    public int Unreachable;
    public int Missed;
    public int Blocked;
    public WaypointBuffer Waypoints;
}

/// <summary>
/// The request-queue scenario (spec section 9.1, AC #5 "toggle and budget-exhaustion"): agents on
/// <see cref="PrototypeTerrain"/> emit <see cref="PathRequest"/>s from a parallel Simulate System, read their
/// <see cref="PathResult"/>s from the queue when they arrive N Ticks later and walk the path with the follow
/// helper; a Late System toggles doors every Tick, so paths break (Blocked) and goals seal (Unreachable). With
/// <see cref="Starved"/> the work budget is tiny, so most requests miss their deadline deterministically. Agent
/// state, and so the World hash, depends on every result and its delivery Tick. No test-framework dependency.
/// </summary>
public sealed class PathRequestScenario : Testing.Scenario
{
    public const ulong DefaultSeed = 2020;
    public static readonly WorldSize DefaultSize = new(100, 72, 2);

    private readonly State _state = new();

    public override string Name => Starved ? "pathfinding-requests-starved" : "pathfinding-requests";

    public override ulong Seed { get; init; } = DefaultSeed;

    public WorldSize Size { get; init; } = DefaultSize;

    public int Agents { get; init; } = 400;

    public int DoorTogglesPerTick { get; init; } = 6;

    /// <summary>Per mille of new goals placed in a sealed pocket.</summary>
    public int PocketGoalsPerMille { get; init; } = 50;

    /// <summary>Tiles an agent walks per Tick.</summary>
    public int Speed { get; init; } = 3;

    /// <summary>With a budget of a few hundred work units per Tick most requests miss their deadline.</summary>
    public bool Starved { get; init; }

    public long WorkBudgetPerTick => Starved ? 400 : 2_000_000;

    public int ResultDelayTicks { get; init; } = 2;

    /// <summary>The HPA* chunk graph budget; a tiny one keeps evicting and rebuilding chunks, which must not change any result.</summary>
    public long ChunkGraphBudgetBytes { get; init; } = new PathfindingOptions().ChunkGraphMemoryBudgetBytes;

    public override int InitialEntityCapacity => Agents * 2;

    public MovementClass Walker => _state.Walker;

    public ComponentType AgentType => _state.Agent;

    public override void Compose(WorldBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.DebugValidation(false);
        builder.AddComponent<PathAgent>("pathfinding.requester");
        var fields = builder.AddFields(Size);
        var terrain = fields.AddField<byte>("terrain");
        var pathfinding = builder.AddPathfinding(fields, new PathfindingOptions { WorkBudgetPerTick = WorkBudgetPerTick, ResultDelayTicks = ResultDelayTicks, ChunkGraphMemoryBudgetBytes = ChunkGraphBudgetBytes });
        var walker = pathfinding.AddMovementClass(new MovementClassDefinition("walker", Passability.OfField(terrain, PrototypeTerrain.Walkable))
        {
            Vertical = Passability.OfField(terrain, PrototypeTerrain.Climbable),
        }.DependsOn(terrain));
        var agent = builder.Component<PathAgent>();
        _state.Reset(this, terrain, walker, agent, pathfinding.RequestQueue, pathfinding.ResultQueue);

        builder.AddSystem(new SystemDefinition
        {
            Name = "pathfinding.requesters",
            Phase = Phase.Simulate,
            All = ComponentMask.Of(agent),
            Writes = ComponentMask.Of(agent),
            ReadsResources = ResourceMask.Of(pathfinding.ResultQueue),
            WritesResources = ResourceMask.Of(pathfinding.RequestQueue),
            ForEachChunk = _state.Step,
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
        var random = world.Random.Derive("pathfinding-requesters");
        var buffer = world.CreateCommandBuffer();
        for (int i = 0; i < Agents; i++)
        {
            var e = buffer.Create(ComponentMask.Of(_state.Agent));
            var agent = default(PathAgent);
            agent.Follow = PathFollowState.At(_state.TileAt(_state.Floor[random.Int32At((ulong)(2 * i), 0, _state.Floor.Length)]));
            _state.PickGoal(ref agent, random, (ulong)((2 * i) + 1));
            buffer.Add(e, _state.Agent, agent);
        }

        world.Playback(buffer);
        world.ResetScratch();
        world.Pathfinding().EnsureBuilt(_state.Walker);
    }

    /// <summary>Totals over every agent.</summary>
    public (long Reached, long Unreachable, long Missed, long Blocked) Totals(World world)
    {
        ArgumentNullException.ThrowIfNull(world);
        long reached = 0;
        long unreachable = 0;
        long missed = 0;
        long blocked = 0;
        foreach (var chunk in world.Query(ComponentMask.Of(_state.Agent)))
        {
            foreach (ref readonly var agent in chunk.Column<PathAgent>(_state.Agent))
            {
                reached += agent.Reached;
                unreachable += agent.Unreachable;
                missed += agent.Missed;
                blocked += agent.Blocked;
            }
        }

        return (reached, unreachable, missed, blocked);
    }

    private sealed class State
    {
        private PathRequestScenario _owner = null!;
        private int[] _doors = [];
        private ResourceType _requests;
        private ResourceType _results;

        public Field<byte> Terrain { get; private set; }

        public MovementClass Walker { get; private set; }

        public ComponentType Agent { get; private set; }

        public byte[] Tiles { get; private set; } = [];

        public int[] Floor { get; private set; } = [];

        public int[] PocketTiles { get; private set; } = [];

        public void Reset(PathRequestScenario owner, Field<byte> terrain, MovementClass walker, ComponentType agent, ResourceType requests, ResourceType results)
        {
            _owner = owner;
            Terrain = terrain;
            Walker = walker;
            Agent = agent;
            _requests = requests;
            _results = results;
            var size = owner.Size;
            var pocket = new bool[size.X * size.Y * size.Z];
            Tiles = PrototypeTerrain.Generate(size, pocket);
            var floor = new List<int>();
            var pockets = new List<int>();
            var doors = new List<int>();
            for (int i = 0; i < Tiles.Length; i++)
            {
                if (pocket[i])
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

        public TilePos TileAt(int index)
        {
            var size = _owner.Size;
            return new TilePos(index % size.X, index / size.X % size.Y, index / (size.X * size.Y));
        }

        public void PickGoal(ref PathAgent agent, RandomSource random, ulong key)
        {
            bool pocket = PocketTiles.Length > 0 && random.Int32At(key << 2, 0, 1000) < _owner.PocketGoalsPerMille;
            var goal = TileAt(pocket
                ? PocketTiles[random.Int32At((key << 2) + 1, 0, PocketTiles.Length)]
                : Floor[random.Int32At((key << 2) + 2, 0, Floor.Length)]);
            agent.GoalX = goal.X;
            agent.GoalY = goal.Y;
            agent.GoalZ = goal.Z;
        }

        /// <summary>Simulate (parallel): take this Tick's result if one came, walk the path, request when idle.</summary>
        public void Step(in SystemContext context, in ChunkView chunk)
        {
            var pathfinding = context.World.Pathfinding();
            var results = context.Events<PathResult>(_results);
            var agents = chunk.Column<PathAgent>(Agent);
            var entities = chunk.Entities;
            var speed = Fixed32.FromInt32(_owner.Speed);
            for (int row = 0; row < agents.Length; row++)
            {
                ref var agent = ref agents[row];
                var entity = entities[row];
                if (agent.Pending != 0)
                {
                    Receive(ref agent, entity, results, pathfinding, context.Random);
                }

                if (agent.Pending != 0)
                {
                    continue; // still waiting
                }

                if (agent.WaypointCount > 0)
                {
                    ReadOnlySpan<TilePos> path = ((ReadOnlySpan<TilePos>)agent.Waypoints)[..agent.WaypointCount];
                    switch (pathfinding.Follow(ref agent.Follow, path, Walker, speed))
                    {
                        case PathFollowStatus.Moving:
                            continue;
                        case PathFollowStatus.Arrived:
                            agent.WaypointCount = 0;
                            if (agent.Follow.Tile == new TilePos(agent.GoalX, agent.GoalY, agent.GoalZ))
                            {
                                agent.Reached++;
                                PickGoal(ref agent, context.Random, entity.Value);
                            }

                            break;
                        default:
                            agent.Blocked++;
                            agent.WaypointCount = 0;
                            break;
                    }
                }

                Request(ref agent, entity, context);
            }
        }

        private void Receive(ref PathAgent agent, EntityId entity, ReadOnlySpan<PathResult> results, PathfindingService pathfinding, RandomSource random)
        {
            for (int i = 0; i < results.Length; i++)
            {
                ref readonly var result = ref results[i];
                if (result.Requester != entity || result.Tag != agent.Requests)
                {
                    continue;
                }

                agent.Pending = 0;
                switch (result.Status)
                {
                    case PathStatus.Found:
                        var waypoints = pathfinding.Waypoints(in result);
                        int count = Math.Min(waypoints.Length, PathAgent.MaxWaypoints);
                        waypoints[..count].CopyTo(agent.Waypoints);
                        agent.WaypointCount = count;
                        agent.Follow = PathFollowState.At(agent.Follow.Tile);
                        break;
                    case PathStatus.Unreachable:
                        agent.Unreachable++;
                        PickGoal(ref agent, random, entity.Value);
                        break;
                    case PathStatus.DeadlineMissed:
                        agent.Missed++;
                        break;
                    default:
                        break; // NeedsLoading never happens in v1 (every chunk is Active); ask again
                }

                return;
            }
        }

        private void Request(ref PathAgent agent, EntityId entity, in SystemContext context)
        {
            agent.Requests++;
            agent.Pending = 1;
            var request = new PathRequest(entity, agent.Follow.Tile, new TilePos(agent.GoalX, agent.GoalY, agent.GoalZ), Walker, (byte)(entity.Index % 3), agent.Requests);
            context.Emit(_requests, in request);
        }

        /// <summary>Late, before region upkeep: flip a few doors chosen by the Tick's random stream.</summary>
        public void ToggleDoors(in SystemContext context)
        {
            if (_doors.Length == 0)
            {
                return;
            }

            var accessor = context.World.Fields().Accessor(Terrain);
            for (int i = 0; i < _owner.DoorTogglesPerTick; i++)
            {
                var tile = TileAt(_doors[context.Random.Int32At((ulong)i, 0, _doors.Length)]);
                accessor[tile] = accessor[tile] == PrototypeTerrain.OpenDoor ? PrototypeTerrain.ClosedDoor : PrototypeTerrain.OpenDoor;
            }
        }
    }
}
