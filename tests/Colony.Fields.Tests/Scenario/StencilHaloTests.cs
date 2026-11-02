using Colony.Fields;

namespace Colony.Fields.Tests.Scenario;

/// <summary>
/// The halo / row-span kernel API (TASK-43): same semantics as the per-cell <see cref="StencilInput{T}.Read"/>
/// kernels in <see cref="StencilTests"/>, reached through <see cref="StencilHalo{T}.Row"/> and
/// <see cref="StencilHalo{T}.OutputRow"/>.
/// </summary>
public sealed class StencilHaloTests
{
    [Fact]
    public void A_double_buffered_halo_kernel_reads_only_the_previous_state_across_chunk_seams()
    {
        var builder = new WorldBuilder().DebugValidation(false);
        var registration = builder.AddFields(new WorldSize(48, 16, 16)); // 3 chunks in a row
        var value = registration.AddField<int>("value");
        // Shift every cell one step along +x; the boundary feeds zeros.
        builder.AddSystem(Stencil.DoubleBuffered("Shift", value, static (in StencilHalo<int> halo) =>
        {
            for (int z = 0; z < FieldChunk.Size; z++)
            {
                for (int y = 0; y < FieldChunk.Size; y++)
                {
                    var row = halo.Row(y, z);
                    var output = halo.OutputRow(y, z);
                    for (int x = 0; x < FieldChunk.Size; x++)
                    {
                        output[x] = row[x]; // row[x] is local x - 1
                    }
                }
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
        Assert.Equal([0, 1], fields.LastTickChanges.ToArray().Select(c => c.Ordinal).ToArray());
    }

    [Fact]
    public void An_in_place_halo_kernel_sees_its_own_writes_and_lower_coloured_chunks_already_updated()
    {
        var builder = new WorldBuilder().DebugValidation(false);
        var registration = builder.AddFields(new WorldSize(64, 16, 16)); // chunks 0..3 along x: colours 0,1,0,1
        var value = registration.AddField<int>("value");
        builder.AddSystem(Stencil.InPlace("Sweep", value, static (in StencilHalo<int> halo) =>
        {
            Assert.True(halo.IsInPlace);
            for (int z = 0; z < FieldChunk.Size; z++)
            {
                for (int y = 0; y < FieldChunk.Size; y++)
                {
                    var row = halo.Row(y, z);
                    var output = halo.OutputRow(y, z);
                    for (int x = 0; x < FieldChunk.Size; x++)
                    {
                        output[x] = Math.Max(row[x + 1], row[x]); // row[x] was written one step earlier
                    }
                }
            }
        }, boundary: 0));
        using var world = builder.Build().CreateWorld(new WorldOptions { WorkerThreads = 4 });
        var fields = world.Fields();
        var accessor = fields.Accessor(value);
        accessor[0, 0, 0] = 1;
        world.Tick();

        // Same outcome as the per-cell Read kernel in StencilTests.
        Assert.Equal(1, accessor[15, 0, 0]);
        Assert.Equal(1, accessor[31, 0, 0]);
        Assert.Equal(0, accessor[32, 0, 0]);
        Assert.Equal(0, accessor[47, 0, 0]);
        Assert.Equal(0, accessor[0, 1, 0]);

        world.Tick();
        Assert.Equal(1, accessor[47, 0, 0]);
        Assert.Equal(1, accessor[63, 0, 0]);
    }

    [Fact]
    public void A_double_buffered_halo_kernel_on_an_edge_chunk_reads_the_boundary_past_the_World_and_stores_it_there()
    {
        var builder = new WorldBuilder().DebugValidation(false);
        var registration = builder.AddFields(new WorldSize(20, 16, 16)); // chunk 1 holds tiles 16..19 only
        var value = registration.AddField<int>("value");
        int seenExtent = 0;
        builder.AddSystem(Stencil.DoubleBuffered("Pull", value, (in StencilHalo<int> halo) =>
        {
            if (halo.Chunk.X == 1)
            {
                seenExtent = (halo.ExtentX * 10000) + (halo.ExtentY * 100) + halo.ExtentZ;
            }

            for (int z = 0; z < FieldChunk.Size; z++)
            {
                for (int y = 0; y < FieldChunk.Size; y++)
                {
                    var row = halo.Row(y, z);
                    var output = halo.OutputRow(y, z);
                    for (int x = 0; x < output.Length; x++)
                    {
                        output[x] = row[x + 2]; // local x + 1
                    }
                }
            }
        }, boundary: -1));
        using var world = builder.Build().CreateWorld(new WorldOptions { WorkerThreads = 1 });
        var fields = world.Fields();
        var accessor = fields.Accessor(value);
        int edge = fields.OrdinalOf(new ChunkCoord(1, 0, 0));
        accessor[19, 0, 0] = 5;
        accessor[16, 0, 0] = 5;
        accessor.WriteChunk(edge)[FieldChunk.CellIndex(5, 0, 0)] = 8; // a storage-only cell (tile x = 21) holding data
        world.Tick();

        Assert.Equal(41616, seenExtent);
        Assert.Equal(5, accessor[15, 0, 0]); // crossed the chunk seam from chunk 1's previous state
        Assert.Equal(5, accessor[18, 0, 0]);
        Assert.Equal(-1, accessor[19, 0, 0]); // x = 20 lies in the chunk's storage but outside the World
        var cells = accessor.ReadChunk(edge);
        Assert.Equal(0, cells[FieldChunk.CellIndex(0, 15, 15)]);
        Assert.Equal(-1, cells[FieldChunk.CellIndex(3, 15, 15)]);
        Assert.Equal(-1, cells[FieldChunk.CellIndex(4, 0, 0)]); // storage-only cells hold the boundary
        Assert.Equal(-1, cells[FieldChunk.CellIndex(5, 0, 0)]); // the stored 8 is gone
        Assert.Equal(-1, cells[FieldChunk.CellIndex(15, 9, 9)]);
    }

    [Fact]
    public void An_in_place_halo_kernel_on_an_edge_chunk_sees_its_own_updates_but_reads_the_boundary_past_the_World()
    {
        var builder = new WorldBuilder().DebugValidation(false);
        var registration = builder.AddFields(new WorldSize(20, 16, 16)); // chunk 1 holds tiles 16..19 only
        var value = registration.AddField<int>("value");
        builder.AddSystem(Stencil.InPlace("Sweep", value, static (in StencilHalo<int> halo) =>
        {
            for (int z = 0; z < FieldChunk.Size; z++)
            {
                for (int y = 0; y < FieldChunk.Size; y++)
                {
                    var row = halo.Row(y, z);
                    var output = halo.OutputRow(y, z);
                    for (int x = 0; x < output.Length; x++)
                    {
                        output[x] = Math.Max(row[x], row[x + 1]);
                    }
                }
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
        Assert.Equal(-1, cells[FieldChunk.CellIndex(4, 3, 3)]); // past the World: the boundary, never the race
        Assert.Equal(-1, cells[FieldChunk.CellIndex(5, 0, 0)]); // the stored 8 is not part of the World
        Assert.Equal(0, accessor[15, 3, 3]); // chunk 0 (colour 0) ran first and reads only -x: untouched
    }

    [Fact]
    public void Halo_rows_outside_the_one_cell_ring_are_rejected()
    {
        var builder = new WorldBuilder().DebugValidation(false);
        var registration = builder.AddFields(new WorldSize(16, 16, 16));
        var value = registration.AddField<int>("value");
        string? rowError = null;
        string? outputError = null;
        builder.AddSystem(Stencil.DoubleBuffered("Probe", value, (in StencilHalo<int> halo) =>
        {
            Assert.Equal(StencilHalo.Side, halo.Row(-1, 16).Length);
            Assert.Equal(FieldChunk.Size, halo.OutputRow(15, 0).Length);
            Assert.Equal(halo.Row(3, 4)[0], halo.Cells[StencilHalo.IndexOf(-1, 3, 4)]);
            try
            {
                halo.Row(-2, 0);
            }
            catch (ArgumentOutOfRangeException e)
            {
                rowError = e.Message;
            }

            try
            {
                halo.OutputRow(0, 16);
            }
            catch (ArgumentOutOfRangeException e)
            {
                outputError = e.Message;
            }
        }));
        using var world = builder.Build().CreateWorld(new WorldOptions { WorkerThreads = 1 });
        world.Tick();

        Assert.Contains("one-cell ring", rowError, StringComparison.Ordinal);
        Assert.Contains("outside the chunk", outputError, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_halo_kernel_computes_the_same_in_World_cells_as_the_per_cell_kernel(bool inPlace)
    {
        // 3x2x2 chunks with a ragged last chunk on every axis; 26-neighbour Life, 6 Ticks, 2 workers.
        var size = new WorldSize(40, 24, 20);
        var byRead = RunLife(size, inPlace, halo: false);
        var byHalo = RunLife(size, inPlace, halo: true);
        Assert.Equal(byRead, byHalo);
        Assert.Contains((byte)1, byHalo);
    }

    private static byte[] RunLife(WorldSize size, bool inPlace, bool halo)
    {
        var builder = new WorldBuilder().DebugValidation(false);
        var registration = builder.AddFields(size);
        var life = registration.AddField<byte>("life");
        SystemDefinition system = (inPlace, halo) switch
        {
            (false, false) => Stencil.DoubleBuffered("Life", life, LifeByRead, boundary: (byte)0),
            (false, true) => Stencil.DoubleBuffered("Life", life, LifeByHalo, boundary: (byte)0),
            (true, false) => Stencil.InPlace("Life", life, LifeByRead, boundary: (byte)0),
            (true, true) => Stencil.InPlace("Life", life, LifeByHalo, boundary: (byte)0),
        };
        builder.AddSystem(system);
        using var world = builder.Build().CreateWorld(new WorldOptions { WorkerThreads = 2, Seed = 5 });
        var accessor = world.Fields().Accessor(life);
        var random = world.Random.Derive("halo-life");
        for (int i = 0; i < 1500; i++)
        {
            ulong c = (ulong)i * 3;
            accessor[random.Int32At(c, 0, size.X), random.Int32At(c + 1, 0, size.Y), random.Int32At(c + 2, 0, size.Z)] = 1;
        }

        for (int t = 0; t < 6; t++)
        {
            world.Tick();
        }

        var cells = new byte[size.X * size.Y * size.Z];
        for (int z = 0, i = 0; z < size.Z; z++)
        {
            for (int y = 0; y < size.Y; y++)
            {
                for (int x = 0; x < size.X; x++)
                {
                    cells[i++] = accessor[x, y, z];
                }
            }
        }

        return cells;
    }

    private static byte Rule(byte self, int alive) => self != 0 ? (byte)(alive is >= 3 and <= 6 ? 1 : 0) : (byte)(alive is 4 or 5 ? 1 : 0);

    private static void LifeByRead(in StencilInput<byte> input, Span<byte> output)
    {
        for (int i = 0; i < FieldChunk.CellCount; i++)
        {
            var (x, y, z) = FieldChunk.CellCoordinates(i);
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

            output[i] = Rule(input.Read(x, y, z), alive);
        }
    }

    private static void LifeByHalo(in StencilHalo<byte> halo)
    {
        var cells = halo.Cells;
        for (int z = 0; z < FieldChunk.Size; z++)
        {
            for (int y = 0; y < FieldChunk.Size; y++)
            {
                var output = halo.OutputRow(y, z);
                for (int x = 0; x < output.Length; x++)
                {
                    int centre = StencilHalo.IndexOf(x, y, z);
                    int alive = 0;
                    for (int dz = -1; dz <= 1; dz++)
                    {
                        for (int dy = -1; dy <= 1; dy++)
                        {
                            for (int dx = -1; dx <= 1; dx++)
                            {
                                if ((dx | dy | dz) != 0)
                                {
                                    alive += cells[centre + dx + (dy * StencilHalo.RowStride) + (dz * StencilHalo.PlaneStride)];
                                }
                            }
                        }
                    }

                    output[x] = Rule(cells[centre], alive);
                }
            }
        }
    }
}