using Colony.Fields;
using Colony.Testing;

namespace Colony.Testing.Tests.Scenarios;

/// <summary>
/// The determinism oracle for the Fields package (TASK-12 AC #2, pattern of TASK-7's ColonistScenario): a
/// double-buffered integer diffusion stencil over a "heat" Field and an in-place coloured cellular
/// automaton over a "life" Field, seeded from the World RNG, run for N Ticks on any thread count.
/// Integer-only kernels; the World hash at checkpoints is the evidence. No test-framework dependency:
/// the Native AOT host compiles it as well.
/// </summary>
public sealed class FieldScenario : Scenario
{
    public const ulong DefaultSeed = 777;
    public static readonly WorldSize Size = new(80, 64, 32); // 5 x 4 x 2 chunks, non-power-of-two

    public override string Name => "fields";

    public override ulong Seed { get; init; } = DefaultSeed;

    public override void Compose(WorldBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.DebugValidation(false);
        var fields = builder.AddFields(Size);
        var heat = fields.AddField<int>("heat");
        var life = fields.AddField<byte>("life");

        builder
            .AddSystem(Stencil.DoubleBuffered("Diffuse", heat, Diffuse, boundary: 0))
            .AddSystem(Stencil.InPlace("Life", life, LifeStep, boundary: (byte)0, phase: Phase.Late));
    }

    public override void Populate(World world)
    {
        ArgumentNullException.ThrowIfNull(world);
        var fields = world.Fields();
        var heat = fields.Accessor(fields.Field<int>("heat"));
        var life = fields.Accessor(fields.Field<byte>("life"));
        var random = world.Random.Derive("field-scenario");

        // A few hot spots and a random soup of life; most chunks stay uniform until the stencils reach them.
        for (int i = 0; i < 12; i++)
        {
            int x = random.Int32At((ulong)(i * 3), 0, Size.X);
            int y = random.Int32At((ulong)((i * 3) + 1), 0, Size.Y);
            int z = random.Int32At((ulong)((i * 3) + 2), 0, Size.Z);
            heat[x, y, z] = 1 << 20;
        }

        for (int i = 0; i < 2000; i++)
        {
            ulong counter = 1000 + ((ulong)i * 3);
            int x = random.Int32At(counter, 0, Size.X);
            int y = random.Int32At(counter + 1, 0, Size.Y);
            int z = random.Int32At(counter + 2, 0, Size.Z);
            life[x, y, z] = 1;
        }
    }

    /// <summary>Six-neighbour integer diffusion: new = old + (sum(neighbours) - 6 old) / 8, exact in ints.</summary>
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

    /// <summary>
    /// An in-place 3D Life variant: a cell is born with 4..5 live neighbours of the 26 and survives with
    /// 3..6. In-place ordered mode means earlier cells of this chunk and lower-colour chunks are already updated.
    /// </summary>
    private static void LifeStep(in StencilInput<byte> input, Span<byte> output)
    {
        for (int z = 0; z < FieldChunk.Size; z++)
        {
            for (int y = 0; y < FieldChunk.Size; y++)
            {
                for (int x = 0; x < FieldChunk.Size; x++)
                {
                    int alive = 0;
                    for (int dz = -1; dz <= 1; dz++)
                    {
                        for (int dy = -1; dy <= 1; dy++)
                        {
                            for (int dx = -1; dx <= 1; dx++)
                            {
                                if ((dx | dy | dz) != 0)
                                {
                                    alive += input.Read(x + dx, y + dy, z + dz);
                                }
                            }
                        }
                    }

                    int index = FieldChunk.CellIndex(x, y, z);
                    byte self = output[index];
                    output[index] = self != 0 ? (byte)(alive is >= 3 and <= 6 ? 1 : 0) : (byte)(alive is 4 or 5 ? 1 : 0);
                }
            }
        }
    }
}
