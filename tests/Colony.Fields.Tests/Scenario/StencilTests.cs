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
    public void Reads_inside_an_edge_chunk_but_outside_the_World_return_the_boundary_value()
    {
        var builder = new WorldBuilder().DebugValidation(false);
        var registration = builder.AddFields(new WorldSize(20, 16, 16)); // chunk 1 holds tiles 16..19 only
        var value = registration.AddField<int>("value");
        // Every cell becomes its +x neighbour: the World edge at x = 20 (not the chunk edge at 32) must feed the boundary.
        builder.AddSystem(Stencil.DoubleBuffered("Pull", value, static (in StencilInput<int> input, Span<int> output) =>
        {
            for (int i = 0; i < FieldChunk.CellCount; i++)
            {
                var (x, y, z) = FieldChunk.CellCoordinates(i);
                output[i] = input.Read(x + 1, y, z);
            }
        }, boundary: -1));
        using var world = builder.Build().CreateWorld(new WorldOptions { WorkerThreads = 1 });
        var fields = world.Fields();
        var accessor = fields.Accessor(value);
        accessor[19, 0, 0] = 5;
        accessor[16, 0, 0] = 5;
        world.Tick();

        Assert.Equal(5, accessor[15, 0, 0]); // crossed the chunk seam from chunk 1's previous state
        Assert.Equal(5, accessor[18, 0, 0]);
        Assert.Equal(-1, accessor[19, 0, 0]); // x = 20 lies in the chunk's storage but outside the World
        var cells = accessor.ReadChunk(fields.OrdinalOf(new ChunkCoord(1, 0, 0)));
        Assert.Equal(0, cells[FieldChunk.CellIndex(0, 15, 15)]); // x = 17 is inside the World and empty
        Assert.Equal(-1, cells[FieldChunk.CellIndex(3, 15, 15)]); // x = 20 is the boundary on every row
        Assert.Equal(-1, cells[FieldChunk.CellIndex(4, 0, 0)]); // storage-only cells beyond the bound read the boundary too (they are hashed)
    }

    [Fact]
    public void An_in_place_stencil_on_an_edge_chunk_sees_its_own_updates_but_reads_the_boundary_past_the_World()
    {
        var builder = new WorldBuilder().DebugValidation(false);
        var registration = builder.AddFields(new WorldSize(20, 16, 16)); // chunk 1 holds tiles 16..19 only
        var value = registration.AddField<int>("value");
        // Sweep max(left, self) in place: a value races along +x through a chunk; the boundary (-1) never wins.
        builder.AddSystem(Stencil.InPlace("Sweep", value, static (in StencilInput<int> input, Span<int> output) =>
        {
            for (int i = 0; i < FieldChunk.CellCount; i++)
            {
                var (x, y, z) = FieldChunk.CellCoordinates(i);
                output[i] = Math.Max(input.Read(x - 1, y, z), input.Read(x, y, z));
            }
        }, boundary: -1));
        using var world = builder.Build().CreateWorld(new WorldOptions { WorkerThreads = 1 });
        var fields = world.Fields();
        var accessor = fields.Accessor(value);
        int edge = fields.OrdinalOf(new ChunkCoord(1, 0, 0));
        accessor[16, 3, 3] = 5;
        accessor.WriteChunk(edge)[FieldChunk.CellIndex(5, 0, 0)] = 8; // a storage-only cell (tile x = 21) holding data
        world.Tick();

        var cells = accessor.ReadChunk(edge);
        Assert.Equal(5, cells[FieldChunk.CellIndex(0, 3, 3)]);
        Assert.Equal(5, cells[FieldChunk.CellIndex(3, 3, 3)]); // own earlier cells are seen updated (in-place order)
        Assert.Equal(0, cells[FieldChunk.CellIndex(4, 0, 0)]); // tile 20: its in-World neighbour x = 19 is read, itself is the boundary
        Assert.Equal(-1, cells[FieldChunk.CellIndex(5, 0, 0)]); // a cell past the World reads itself as the boundary, not its stored 8
        Assert.Equal(0, accessor[15, 3, 3]); // chunk 0 (colour 0) ran first and reads only -x: untouched
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
