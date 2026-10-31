using Colony.Fields;
using Colony.Hashing;
using Colony.Numerics;

namespace Colony.Fields.Tests.Scenario;

/// <summary>
/// The spatial index oracle scenario: a Simulate "Mover" System that moves, destroys and spawns grid
/// Entities from the System RNG (some spawn outside the World), and a Simulate "Prober" System that
/// queries the index from every chunk job in parallel and reduces the results to one hash per Tick in job
/// order. The Late-Phase index update follows both.
/// </summary>
public sealed class SpatialScenario
{
    public const int TargetPopulation = 600;
    public static readonly WorldSize Size = new(80, 48, 32); // 5 x 3 x 2 chunks

    private readonly ulong[][] _probeHashes = new ulong[1][];
    private readonly List<ulong> _tickHashes = new(capacity: 1024); // pre-sized so the zero-alloc test measures the index, not this list

    private SpatialScenario()
    {
    }

    public ComponentType TilePos { get; private set; }

    public WorldSchema Schema { get; private set; } = null!;

    /// <summary>One hash per Tick of every Prober job's query results, combined in job order.</summary>
    public IReadOnlyList<ulong> ProbeHashes => _tickHashes;

    public static SpatialScenario Build(bool probe)
    {
        var scenario = new SpatialScenario();
        var builder = new WorldBuilder().AddComponent<TilePos>("tile.pos").DebugValidation(false);
        var tilePos = builder.Component<TilePos>();
        scenario.TilePos = tilePos;
        var fields = builder.AddFields(Size);
        builder.AddSpatialIndex(fields, tilePos);
        builder.AddSystem(new SystemDefinition
        {
            Name = "Mover",
            Writes = ComponentMask.Of(tilePos),
            Begin = (in SystemContext context) => Spawn(in context, tilePos),
            ForEachChunk = (in SystemContext context, in ChunkView chunk) => Move(in context, in chunk, tilePos),
        });
        if (probe)
        {
            builder.AddSystem(new SystemDefinition
            {
                Name = "Prober",
                Reads = ComponentMask.Of(tilePos),
                After = ["Mover"],
                Begin = scenario.BeginProbe,
                ForEachChunk = (in SystemContext context, in ChunkView chunk) => scenario.Probe(in context, in chunk, tilePos),
                End = scenario.EndProbe,
            });
        }

        scenario.Schema = builder.Build();
        return scenario;
    }

    public World CreateWorld(int workers, ulong seed)
    {
        var world = Schema.CreateWorld(new WorldOptions { WorkerThreads = workers, Seed = seed, InitialEntityCapacity = 2048 });
        var buffer = world.CreateCommandBuffer();
        var random = world.Random.Derive("spatial-seed");
        for (int i = 0; i < TargetPopulation / 2; i++)
        {
            var e = buffer.Create(ComponentMask.Of(TilePos));
            buffer.Add(e, TilePos, RandomTile(random, (ulong)i * 4, inside: true));
        }

        world.Playback(buffer);
        world.ResetScratch();
        return world;
    }

    private static TilePos RandomTile(RandomSource random, ulong counter, bool inside)
    {
        int margin = inside ? 0 : 3;
        return new TilePos(
            random.Int32At(counter, -margin, Size.X + margin),
            random.Int32At(counter + 1, -margin, Size.Y + margin),
            random.Int32At(counter + 2, -margin, Size.Z + margin));
    }

    private static void Spawn(in SystemContext context, ComponentType tilePos)
    {
        int alive = context.World.Query(tilePos).EntityCount();
        int spawns = alive < TargetPopulation ? 20 : 5;
        for (int i = 0; i < spawns; i++)
        {
            ulong counter = 1_000_000 + ((ulong)i * 4);
            var e = context.Commands.Create(ComponentMask.Of(tilePos));
            context.Commands.Add(e, tilePos, RandomTile(context.Random, counter, inside: i % 7 != 0));
        }
    }

    private static void Move(in SystemContext context, in ChunkView chunk, ComponentType tilePos)
    {
        var entities = chunk.Entities;
        var positions = chunk.Column<TilePos>(tilePos);
        for (int i = 0; i < entities.Length; i++)
        {
            ulong counter = (ulong)entities[i].Index * 4;
            uint roll = context.Random.UInt32At(counter);
            switch (roll % 16)
            {
                case 0:
                    context.Commands.Destroy(entities[i]);
                    break;
                case 1:
                case 2:
                case 3:
                    positions[i] = RandomTile(context.Random, counter + 1, inside: roll % 32 == 1);
                    break;
                case 4:
                case 5:
                case 6:
                case 7:
                case 8:
                case 9:
                    positions[i] += new TilePos(
                        context.Random.Int32At(counter + 1, -2, 3),
                        context.Random.Int32At(counter + 2, -2, 3),
                        context.Random.Int32At(counter + 3, -1, 2));
                    break;
                default:
                    break;
            }
        }
    }

    private void BeginProbe(in SystemContext context)
    {
        if (_probeHashes[0] is null || _probeHashes[0].Length < context.JobCount)
        {
            _probeHashes[0] = new ulong[Math.Max(context.JobCount, 16)];
        }
    }

    private void Probe(in SystemContext context, in ChunkView chunk, ComponentType tilePos)
    {
        var index = context.World.SpatialIndex();
        var positions = chunk.Column<TilePos>(tilePos);
        var entities = chunk.Entities;
        Span<SpatialHit> hits = stackalloc SpatialHit[6];
        var state = new Hash64State((ulong)context.JobIndex);
        for (int i = 0; i < entities.Length; i++)
        {
            int n = index.QueryNearest(positions[i], hits[..4]);
            Append(ref state, hits[..n]);
            n = index.QueryRadius(positions[i], 5, hits);
            Append(ref state, hits[..n]);
            n = index.QueryBox(TileBox.Around(positions[i], 3), positions[i], hits, new EvenIndex());
            Append(ref state, hits[..n]);
            n = index.QueryTile(positions[i], hits);
            Append(ref state, hits[..n]);
        }

        _probeHashes[0][context.JobIndex] = state.Finish();
    }

    private static void Append(ref Hash64State state, ReadOnlySpan<SpatialHit> hits)
    {
        state.Append(hits.Length);
        for (int i = 0; i < hits.Length; i++)
        {
            state.Append(hits[i].Entity.Value);
            state.Append(hits[i].DistanceSquared);
        }
    }

    private void EndProbe(in SystemContext context)
    {
        var state = new Hash64State((ulong)context.Tick);
        for (int job = 0; job < context.JobCount; job++)
        {
            state.Append(_probeHashes[0][job]);
        }

        if (_tickHashes.Count <= context.Tick)
        {
            _tickHashes.Add(state.Finish());
        }
        else
        {
            _tickHashes[(int)context.Tick] = state.Finish();
        }
    }

    /// <summary>Accepts Entities with an even slot index.</summary>
    public readonly struct EvenIndex : ISpatialPredicate
    {
        public bool Matches(EntityId entity, in TilePos position) => (entity.Index & 1) == 0;
    }
}
