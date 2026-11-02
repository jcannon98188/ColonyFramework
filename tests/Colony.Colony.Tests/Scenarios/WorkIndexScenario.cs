using Colony.Fields;
using Colony.Testing;

namespace Colony.Jobs.Tests.Scenarios;

/// <summary>A worker that seeks the nearest available work item of its skill.</summary>
public struct SeekingWorker
{
    public int Skill;
    public int Score;
    public int Found;
}

/// <summary>
/// The work index oracle (spec section 9.2; TASK-42 AC #3): workers query <see cref="WorkIndex.QueryNearest(TilePos, int, int, Span{WorkHit})"/>
/// from a parallel Simulate System, take the hit (marking it taken), walk onto its tile and score its distance; a
/// Begin System makes taken items available again on a fresh tile and replaces a few destroyed ones, so the Late
/// update sees moves, availability flips, destroys and creates every Tick. The index itself is derived state; the
/// World hash depends on it only through the hits, which is the determinism evidence. No test-framework
/// dependency: the Native AOT host compiles it as well.
/// </summary>
public sealed class WorkIndexScenario : Testing.Scenario
{
    public const ulong DefaultSeed = 0x0042_0003_0000_0001UL;
    public const int Types = 3;
    public const int Radius = 20;
    public static readonly WorldSize Size = new(64, 64, 16);

    private ComponentType _tilePos;
    private ComponentType _workItem;
    private ComponentType _worker;
    private ComponentMask _itemMask;
    private ComponentMask _workerMask;

    public override string Name => "work-index";

    public override ulong Seed { get; init; } = DefaultSeed;

    public int Workers { get; init; } = 2_000;

    public int Items { get; init; } = 600;

    public override int InitialEntityCapacity => (Workers + Items) * 2;

    public override void Compose(WorldBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.DebugValidation(false);
        var fields = builder.AddFields(Size);
        _tilePos = builder.AddComponent<TilePos>("work.tile-pos").Component<TilePos>();
        _worker = builder.AddComponent<SeekingWorker>("work.worker").Component<SeekingWorker>();
        var index = builder.AddWorkIndex(fields, _tilePos, new WorkIndexOptions { WorkTypeCount = Types, InitialCapacity = InitialEntityCapacity });
        _workItem = index.WorkItem;
        _itemMask = ComponentMask.Of(_tilePos, _workItem);
        _workerMask = ComponentMask.Of(_tilePos, _worker);

        var tilePos = _tilePos;
        var workItem = _workItem;
        var worker = _worker;
        var itemMask = _itemMask;
        builder.AddSystem(new SystemDefinition
        {
            Name = "work.respawn",
            Phase = Phase.Begin,
            Reads = ComponentMask.Of(workItem),
            All = itemMask,
            ForEachChunk = (in SystemContext context, in ChunkView chunk) =>
            {
                var items = chunk.ReadColumn<WorkItem>(workItem);
                var ids = chunk.Entities;
                for (int row = 0; row < chunk.Count; row++)
                {
                    var draw = context.Random.Derive(ids[row].Value);
                    uint roll = draw.UInt32At(0);
                    if (roll % 64u == 0u)
                    {
                        context.Commands.Destroy(ids[row]);
                        var replacement = context.Commands.Create(itemMask);
                        context.Commands.Add(replacement, tilePos, RandomTile(draw, 1));
                        context.Commands.Add(replacement, workItem, WorkItem.Available((int)(draw.UInt32At(8) % Types)));
                    }
                    else if (!items[row].IsAvailable && roll % 3u == 0u)
                    {
                        context.Commands.Add(ids[row], tilePos, RandomTile(draw, 1));
                        context.Commands.Add(ids[row], workItem, items[row].WithAvailable(true));
                    }
                }
            },
        });
        builder.AddSystem(new SystemDefinition
        {
            Name = "work.seek",
            Phase = Phase.Simulate,
            Reads = ComponentMask.Of(tilePos),
            Writes = ComponentMask.Of(worker),
            ForEachChunk = (in SystemContext context, in ChunkView chunk) =>
            {
                var index = context.World.WorkIndex();
                var positions = chunk.ReadColumn<TilePos>(tilePos);
                var workers = chunk.Column<SeekingWorker>(worker);
                var ids = chunk.Entities;
                Span<WorkHit> hits = stackalloc WorkHit[1];
                for (int row = 0; row < chunk.Count; row++)
                {
                    ref var w = ref workers[row];
                    if (index.QueryNearest(positions[row], w.Skill, Radius, hits) == 0)
                    {
                        continue;
                    }

                    var hit = hits[0];
                    w.Found++;
                    w.Score += (int)hit.DistanceSquared + (int)(hit.Entity.Value & 0xFF);
                    context.Commands.Add(hit.Entity, workItem, WorkItem.Taken(hit.Type));
                    context.Commands.Add(ids[row], tilePos, hit.Position);
                }
            },
        });
    }

    public override void Populate(World world)
    {
        ArgumentNullException.ThrowIfNull(world);
        var buffer = world.CreateCommandBuffer();
        var init = world.Random.Derive("work-init");
        for (int i = 0; i < Items; i++)
        {
            var draw = init.Derive((ulong)i);
            var e = buffer.Create(_itemMask);
            buffer.Add(e, _tilePos, RandomTile(draw, 0));
            buffer.Add(e, _workItem, WorkItem.Available(i % Types));
        }

        for (int i = 0; i < Workers; i++)
        {
            var draw = init.Derive(0x1_0000_0000UL + (ulong)i);
            var e = buffer.Create(_workerMask);
            buffer.Add(e, _tilePos, RandomTile(draw, 0));
            buffer.Add(e, _worker, new SeekingWorker { Skill = i % Types });
        }

        world.Playback(buffer);
        world.ResetScratch();
    }

    private static TilePos RandomTile(Colony.Numerics.RandomSource draw, ulong block)
    {
        ulong c = block * 4;
        return new TilePos((int)(draw.UInt32At(c) % (uint)Size.X), (int)(draw.UInt32At(c + 1) % (uint)Size.Y), (int)(draw.UInt32At(c + 2) % (uint)Size.Z));
    }
}
