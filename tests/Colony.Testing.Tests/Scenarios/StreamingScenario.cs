using Colony.Fields;
using Colony.Numerics;
using Colony.Testing;

namespace Colony.Testing.Tests.Scenarios;

/// <summary>
/// TASK-26's streaming oracle (spec section 5.3): a World that starts entirely Unloaded and is generated on demand
/// through a pure <see cref="IChunkSource"/> as two scripted focus points walk over it (one is removed and re-added
/// every few Ticks). Creatures placed by the source wander across chunk borders and freeze when they step into the
/// Hibernating ring; one Pinned walker roams the unloaded space; an integer diffusion stencil erodes the "height"
/// Field and reads the frozen side as a fixed boundary. Every <see cref="RoundTripEvery"/> Ticks the driver forces a
/// resident chunk out and back in between Ticks and fails the run unless the World hash is unchanged (AC #2) and the
/// Hibernating ring still surrounds every Active chunk (AC #4). No test-framework dependency: the Native AOT host
/// compiles it as well.
/// </summary>
public sealed class StreamingScenario : Scenario
{
    public const ulong DefaultSeed = 0x57A3_A11E_0000_0026UL;
    public static readonly WorldSize Size = new(64, 48, 32); // 4 x 3 x 2 chunks

    public struct Creature
    {
        public int Energy;
        public int Heading;
    }

    /// <summary><c>streaming</c>, or <c>streaming-delayed</c> / <c>streaming-budget</c> for the TASK-27 variants (their own golden files).</summary>
    public override string Name => LoadDelayTicks > 0 ? "streaming-delayed" : ResidentChunkBudget > 0 ? "streaming-budget" : "streaming";

    public override ulong Seed { get; init; } = DefaultSeed;

    /// <summary>Focus commands are submitted only while the Tick is below this (the allocation test stops them after warm-up).</summary>
    public int FocusMovesUntilTick { get; init; } = int.MaxValue;

    /// <summary>Force a resident chunk out and back in after every this many Ticks; 0 disables the round trips.</summary>
    public int RoundTripEvery { get; init; } = 4;

    /// <summary>TASK-27 AC #1/#5: loads apply this many Ticks after their request (0 = synchronous, the TASK-26 golden).</summary>
    public int LoadDelayTicks { get; init; }

    /// <summary>Hibernating ring width; the delayed variant uses 2 so the moving focus finds its next ring resident.</summary>
    public int RingWidth { get; init; } = 1;

    /// <summary>TASK-27 AC #2: resident-chunk budget (0 = none).</summary>
    public int ResidentChunkBudget { get; init; }

    /// <summary>Milliseconds the chunk source sleeps per generated chunk: a slow loader whose stalls must not change the hashes.</summary>
    public int SourceDelayMs { get; init; }

    /// <summary>The streaming totals of the last World this scenario populated (the stall count of a slow run).</summary>
    public StreamingCounters LastCounters { get; private set; }

    public override int InitialEntityCapacity => 2048;

    public override void Compose(WorldBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.DebugValidation(false);
        var tilePos = builder.AddComponent<TilePos>("tile.pos").Component<TilePos>();
        var creature = builder.AddComponent<Creature>("creature").Component<Creature>();
        var fields = builder.AddFields(Size);
        var height = fields.AddField<int>("height");
        fields.AddField<byte>("soil");
        builder.AddResidency(fields, tilePos, new ResidencyOptions
        {
            InitialResidency = ChunkResidency.Unloaded,
            MaxFocusPoints = 4,
            LoadDelayTicks = LoadDelayTicks,
            RingWidth = RingWidth,
            ResidentChunkBudget = ResidentChunkBudget,
        });

        builder
            .AddSystem(Stencil.DoubleBuffered("Erode", height, Diffuse, boundary: 0))
            .AddSystem(new SystemDefinition
            {
                Name = "Wander",
                Phase = Phase.Simulate,
                Writes = ComponentMask.Of(tilePos, creature),
                ForEachChunk = (in SystemContext context, in ChunkView chunk) =>
                {
                    var positions = chunk.Column<TilePos>(tilePos);
                    var creatures = chunk.Column<Creature>(creature);
                    var entities = chunk.Entities;
                    foreach (int row in chunk.EnabledRows(creature))
                    {
                        ref var c = ref creatures[row];
                        ulong key = entities[row].Value * 2;
                        if (--c.Energy <= 0)
                        {
                            c.Energy = context.Random.Int32At(key, 4, 24);
                            c.Heading = context.Random.Int32At(key + 1, 0, 6);
                        }

                        positions[row] = Step(positions[row], ref c.Heading);
                    }
                },
            });
    }

    public override void Populate(World world)
    {
        ArgumentNullException.ThrowIfNull(world);
        world.Fields().ChunkSource = new ChunkSource(world, Seed, SourceDelayMs);
        var tilePos = world.Schema.Component<TilePos>();
        var creature = world.Schema.Component<Creature>();
        var pinned = world.Schema.Component<Pinned>();
        var commands = world.CreateCommandBuffer();
        var walker = commands.Create(ComponentMask.Of(tilePos, creature, pinned));
        commands.Add(walker, tilePos, new TilePos(8, 8, 8));
        commands.Add(walker, creature, new Creature { Energy = 3, Heading = 0 });
        commands.Add(walker, pinned, new Pinned(1));
        world.Playback(commands);
        world.ResetScratch();
    }

    public override void OnLoaded(World world)
    {
        ArgumentNullException.ThrowIfNull(world);
        world.Fields().ChunkSource = new ChunkSource(world, Seed, SourceDelayMs);
    }

    public override void Advance(World world)
    {
        ArgumentNullException.ThrowIfNull(world);
        long tick = world.CurrentTick;
        if (tick < FocusMovesUntilTick)
        {
            SubmitFocus(world, tick);
        }

        world.Tick();
        LastCounters = world.Residency().Streaming;
        if (RoundTripEvery > 0 && tick % RoundTripEvery == RoundTripEvery - 1)
        {
            RoundTrip(world, tick);
        }
    }

    /// <summary>The focus points of one Tick: a deterministic function of the Tick alone, independent of any World.</summary>
    public static void SubmitFocus(World world, long tick)
    {
        ArgumentNullException.ThrowIfNull(world);
        int t = (int)tick;
        var source = new InputSource(1);
        world.Inputs.Submit(source, new FocusPoint(1, new TilePos((t * 5) % Size.X, (t * 3) % Size.Y, (t * 2) % Size.Z), 0));
        bool second = (t / 6) % 2 == 0;
        var far = new TilePos(Size.X - 1 - ((t * 7) % Size.X), Size.Y - 1 - ((t * 2) % Size.Y), Size.Z - 1 - ((t * 3) % Size.Z));
        world.Inputs.Submit(source, new FocusPoint(2, far, second ? 1 : -1));
    }

    /// <summary>Forces the chunk under the first focus point (or, on alternate rounds, the next chunk in canonical order) out and back in; a changed hash or a broken ring fails the run.</summary>
    private static void RoundTrip(World world, long tick)
    {
        var fields = world.Fields();
        var residency = world.Residency();
        int ordinal = residency.FocusPoints.Length == 0 ? 0 : fields.OrdinalOf(ChunkCoord.OfTile(residency.FocusPoints[0].Center));
        if ((tick & 4) != 0)
        {
            ordinal = (ordinal + 1) % fields.ChunkCount;
        }

        var state = residency.ResidencyOf(ordinal);
        if (state == ChunkResidency.Unloaded)
        {
            return; // loading an unloaded chunk places its Entities: that is a change, not a round trip
        }

        ulong before = world.Hash();
        residency.ForceResidency(ordinal, ChunkResidency.Unloaded);
        if (residency.ResidencyOf(ordinal) != ChunkResidency.Unloaded)
        {
            throw new HarnessException($"Tick {tick}: chunk {ordinal} did not unload.");
        }

        residency.ForceResidency(ordinal, state);
        ulong after = world.Hash();
        if (before != after)
        {
            throw new HarnessException($"Tick {tick}: forcing chunk {ordinal} ({state}) out and back in changed the World hash {before:x16} -> {after:x16}.");
        }

        if (!residency.RingInvariantHolds())
        {
            throw new HarnessException($"Tick {tick}: an Active chunk borders unloaded space after the round trip of chunk {ordinal}.");
        }
    }

    /// <summary>One tile along the heading (±x, ±y, ±z), bouncing off the World bounds.</summary>
    private static TilePos Step(TilePos position, ref int heading)
    {
        int axis = heading >> 1;
        int delta = (heading & 1) == 0 ? 1 : -1;
        int x = position.X, y = position.Y, z = position.Z;
        switch (axis)
        {
            case 0: x += delta; break;
            case 1: y += delta; break;
            default: z += delta; break;
        }

        if (x < 0 || x >= Size.X || y < 0 || y >= Size.Y || z < 0 || z >= Size.Z)
        {
            heading ^= 1;
            return position;
        }

        return new TilePos(x, y, z);
    }

    /// <summary>Six-neighbour integer diffusion, as in the Fields scenario.</summary>
    private static void Diffuse(in StencilInput<int> input, Span<int> output)
    {
        for (int z = 0; z < FieldChunk.Size; z++)
        {
            for (int y = 0; y < FieldChunk.Size; y++)
            {
                for (int x = 0; x < FieldChunk.Size; x++)
                {
                    int old = input.Read(x, y, z);
                    long sum = (long)input.Read(x - 1, y, z) + input.Read(x + 1, y, z)
                             + input.Read(x, y - 1, z) + input.Read(x, y + 1, z)
                             + input.Read(x, y, z - 1) + input.Read(x, y, z + 1);
                    output[FieldChunk.CellIndex(x, y, z)] = old + (int)((sum - (6L * old)) >> 3);
                }
            }
        }
    }

    /// <summary>A pure function of (seed, chunk coordinate): a bumpy height map, a per-chunk soil value and three creatures per chunk. Thread-safe (no shared scratch), as delayed loads require.</summary>
    private sealed class ChunkSource : IChunkSource
    {
        private const ulong Key = 0x4348_554E_4B53_5243UL; // "CHUNKSRC"
        private const int CreaturesPerChunk = 3;

        private readonly ulong _seed;
        private readonly int _delayMs;
        private readonly Field<int> _height;
        private readonly Field<byte> _soil;
        private readonly ComponentType _tilePos;
        private readonly ComponentType _creature;

        public ChunkSource(World world, ulong seed, int delayMs)
        {
            _seed = seed;
            _delayMs = delayMs;
            var fields = world.Fields();
            _height = fields.Field<int>("height");
            _soil = fields.Field<byte>("soil");
            _tilePos = world.Schema.Component<TilePos>();
            _creature = world.Schema.Component<Creature>();
        }

        public void GenerateCells(ChunkCoord coord, ChunkCellBuffer cells)
        {
            if (_delayMs > 0)
            {
                Thread.Sleep(_delayMs); // a slow loader: its stalls are counted, the state is the same
            }

            var random = Stream(coord);
            var height = cells.Cells(_height);
            for (int i = 0; i < height.Length; i++)
            {
                height[i] = random.Int32At((ulong)i, 0, 1 << 12);
            }

            cells.Cells(_soil).Fill((byte)(coord.X + (3 * coord.Y) + (7 * coord.Z)));
        }

        public void PlaceEntities(ChunkCoord coord, CommandBuffer commands)
        {
            var random = Stream(coord).Derive(1);
            var tilePos = _tilePos;
            var creature = _creature;
            for (int i = 0; i < CreaturesPerChunk; i++)
            {
                ulong at = (ulong)i * 4;
                var entity = commands.Create(ComponentMask.Of(tilePos, creature));
                var local = new TilePos(random.Int32At(at, 0, FieldChunk.Size), random.Int32At(at + 1, 0, FieldChunk.Size), random.Int32At(at + 2, 0, FieldChunk.Size));
                commands.Add(entity, tilePos, coord.Origin + local);
                commands.Add(entity, creature, new Creature { Energy = random.Int32At(at + 3, 1, 12), Heading = i });
            }
        }

        private RandomSource Stream(ChunkCoord coord)
            => new RandomSource(_seed, Key).Derive((ulong)(uint)coord.X).Derive((ulong)(uint)coord.Y).Derive((ulong)(uint)coord.Z);
    }
}
