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

    /// <summary>
    /// Six-neighbour integer diffusion: new = old + (sum(neighbours) - 6 old) / 8, exact in ints. Row-span kernel
    /// (TASK-43): five halo rows per output row, element <c>x + 1</c> of a halo row is local x.
    /// </summary>
    private static void Diffuse(in StencilHalo<int> halo)
    {
        for (int z = 0; z < FieldChunk.Size; z++)
        {
            for (int y = 0; y < FieldChunk.Size; y++)
            {
                var row = halo.Row(y, z);
                var north = halo.Row(y - 1, z);
                var south = halo.Row(y + 1, z);
                var below = halo.Row(y, z - 1);
                var above = halo.Row(y, z + 1);
                var output = halo.OutputRow(y, z);
                for (int x = 0; x < output.Length; x++)
                {
                    int old = row[x + 1];
                    long sum = (long)row[x] + row[x + 2] + north[x + 1] + south[x + 1] + below[x + 1] + above[x + 1];
                    output[x] = old + (int)((sum - (6L * old)) >> 3);
                }
            }
        }
    }

    /// <summary>
    /// An in-place 3D Life variant: a cell is born with 4..5 live neighbours of the 26 and survives with
    /// 3..6. In-place ordered mode means earlier cells of this chunk and lower-colour chunks are already updated.
    /// Row-span kernel (TASK-43): the eight rows around the current one do not change while it is computed, so
    /// their per-column sums are taken once per row; the current row is read live (its output aliases it), so
    /// the -x neighbour is already updated exactly as in the per-cell formulation.
    /// </summary>
    private static void LifeStep(in StencilHalo<byte> halo)
    {
        Span<int> columns = stackalloc int[StencilHalo.Side];
        for (int z = 0; z < FieldChunk.Size; z++)
        {
            for (int y = 0; y < FieldChunk.Size; y++)
            {
                var a0 = halo.Row(y - 1, z - 1);
                var a1 = halo.Row(y, z - 1);
                var a2 = halo.Row(y + 1, z - 1);
                var b0 = halo.Row(y - 1, z);
                var b2 = halo.Row(y + 1, z);
                var c0 = halo.Row(y - 1, z + 1);
                var c1 = halo.Row(y, z + 1);
                var c2 = halo.Row(y + 1, z + 1);
                for (int i = 0; i < columns.Length; i++)
                {
                    columns[i] = a0[i] + a1[i] + a2[i] + b0[i] + b2[i] + c0[i] + c1[i] + c2[i];
                }

                var current = halo.Row(y, z);
                var output = halo.OutputRow(y, z);
                for (int x = 0; x < output.Length; x++)
                {
                    int alive = columns[x] + columns[x + 1] + columns[x + 2] + current[x] + current[x + 2];
                    byte self = current[x + 1];
                    output[x] = self != 0 ? (byte)(alive is >= 3 and <= 6 ? 1 : 0) : (byte)(alive is 4 or 5 ? 1 : 0);
                }
            }
        }
    }
}