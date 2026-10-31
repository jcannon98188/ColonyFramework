using Colony.Fields;

namespace Colony.Fields.Tests.Scenario;

/// <summary>
/// Stencil semantics (AC #2). The cross-thread hash equality and zero-allocation evidence for the stencil
/// scenario runs through Colony.Testing's harnesses in tests/Colony.Testing.Tests (FieldScenarioTests).
/// </summary>
public sealed class StencilTests
{
    [Fact]
    public void A_double_buffered_stencil_reads_only_the_previous_state_and_marks_only_changed_chunks()
    {
        var builder = new WorldBuilder().DebugValidation(false);
        var registration = builder.AddFields(new WorldSize(48, 16, 16)); // 3 chunks in a row
        var value = registration.AddField<int>("value");
        // Shift every cell one step along +x; the boundary feeds zeros.
        builder.AddSystem(Stencil.DoubleBuffered("Shift", value, static (in StencilInput<int> input, Span<int> output) =>
        {
            for (int i = 0; i < FieldChunk.CellCount; i++)
            {
                var (x, y, z) = FieldChunk.CellCoordinates(i);
                output[i] = input.Read(x - 1, y, z);
            }
        }, boundary: 0));
        using var world = builder.Build().CreateWorld(new WorldOptions { WorkerThreads = 2 });
        var fields = world.Fields();
        var accessor = fields.Accessor(value);
        accessor[15, 0, 0] = 7; // last cell of chunk 0
        world.Tick();

        Assert.Equal(0, accessor[15, 0, 0]);
        Assert.Equal(7, accessor[16, 0, 0]); // crossed into chunk 1 using chunk 0's previous state
        Assert.Equal(0, accessor[17, 0, 0]); // not shifted twice
        Assert.True(accessor.IsUniform(fields.OrdinalOf(new ChunkCoord(2, 0, 0))), "an unchanged chunk stays uniform");
        var changed = fields.LastTickChanges.ToArray();
        Assert.Equal(2, changed.Length);
        Assert.Equal([0, 1], changed.Select(c => c.Ordinal).ToArray());

        for (int t = 0; t < 32; t++)
        {
            world.Tick();
        }

        Assert.Equal(0, accessor[47, 0, 0]); // shifted out of the World
        Assert.Equal(1, fields.LastTickChanges.Length); // the Tick that cleared x = 47
        world.Tick();
        Assert.Equal(0, fields.LastTickChanges.Length); // nothing left to move
    }

    [Fact]
    public void An_in_place_stencil_sees_lower_coloured_chunks_already_updated_in_a_fixed_order()
    {
        var builder = new WorldBuilder().DebugValidation(false);
        var registration = builder.AddFields(new WorldSize(64, 16, 16)); // chunks 0..3 along x: colours 0,1,0,1
        var value = registration.AddField<int>("value");
        // Each cell becomes max(self, left neighbour): a one-cell-per-pass sweep in double-buffered mode,
        // but in-place order lets a value race through a chunk and into the next chunk of a higher colour.
        builder.AddSystem(Stencil.InPlace("Sweep", value, static (in StencilInput<int> input, Span<int> output) =>
        {
            for (int i = 0; i < FieldChunk.CellCount; i++)
            {
                var (x, y, z) = FieldChunk.CellCoordinates(i);
                output[i] = Math.Max(output[i], input.Read(x - 1, y, z));
            }
        }, boundary: 0));
        using var world = builder.Build().CreateWorld(new WorldOptions { WorkerThreads = 4 });
        var fields = world.Fields();
        var accessor = fields.Accessor(value);
        accessor[0, 0, 0] = 1;
        world.Tick();

        // Chunk 0 (colour 0) sweeps to x=15 in one pass; chunk 1 (colour 1) runs after and sees the updated x=15, so it fills too;
        // chunk 2 (colour 0) ran before chunk 1 and saw chunk 1's old zeros.
        Assert.Equal(1, accessor[15, 0, 0]);
        Assert.Equal(1, accessor[31, 0, 0]);
        Assert.Equal(0, accessor[32, 0, 0]);
        Assert.Equal(0, accessor[47, 0, 0]);

        world.Tick();
        Assert.Equal(1, accessor[47, 0, 0]);
        Assert.Equal(1, accessor[63, 0, 0]);
    }

    [Fact]
    public void Chunk_colouring_never_puts_two_adjacent_chunks_in_one_colour()
    {
        var layout = new ChunkLayout(new WorldSize(80, 48, 32));
        int total = 0;
        for (int colour = 0; colour < ChunkLayout.ColourCount; colour++)
        {
            var ordinals = layout.OrdinalsOfColour(colour);
            total += ordinals.Length;
            for (int i = 0; i < ordinals.Length; i++)
            {
                var a = layout.CoordOf(ordinals[i]);
                Assert.Equal(colour, ChunkLayout.ColourOf(a));
                Assert.True(i == 0 || ordinals[i] > ordinals[i - 1], "Morton order within a colour");
                for (int j = i + 1; j < ordinals.Length; j++)
                {
                    var b = layout.CoordOf(ordinals[j]);
                    bool adjacent = Math.Abs(a.X - b.X) <= 1 && Math.Abs(a.Y - b.Y) <= 1 && Math.Abs(a.Z - b.Z) <= 1;
                    Assert.False(adjacent, $"{a} and {b} share colour {colour}");
                }
            }
        }

        Assert.Equal(layout.ChunkCount, total);
    }
}
