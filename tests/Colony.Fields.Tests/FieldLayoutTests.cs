using Colony.Fields;

namespace Colony.Fields.Tests;

/// <summary>AC #1: Fields registered through the builder for a bounded World, 16x16x16 chunks in Morton order, uniform chunks without cell storage.</summary>
public sealed class FieldLayoutTests
{
    public struct Heat
    {
        public ushort Value;
    }

    private static (WorldSchema Schema, Field<Heat> Heat) BuildSchema(WorldSize size)
    {
        var builder = new WorldBuilder().DebugValidation(false);
        var fields = builder.AddFields(size);
        var heat = fields.AddField<Heat>("heat");
        return (builder.Build(), heat);
    }

    [Fact]
    public void World_dimensions_are_fixed_at_creation_and_chunked_at_16_cubed()
    {
        var size = new WorldSize(100, 40, 1); // depth 1 = 2D
        var (schema, heat) = BuildSchema(size);
        using var world = schema.CreateWorld(new WorldOptions { WorkerThreads = 1 });
        var fields = world.Fields();

        Assert.Equal(size, fields.Size);
        Assert.Equal(16, FieldChunk.Size);
        Assert.Equal(4096, FieldChunk.CellCount);
        Assert.Equal(7, fields.Size.ChunksX);
        Assert.Equal(3, fields.Size.ChunksY);
        Assert.Equal(1, fields.Size.ChunksZ);
        Assert.Equal(21, fields.ChunkCount);
        Assert.True(fields.Size.Is2D);
        Assert.Equal("heat", fields.Describe(heat).Name);
        Assert.Equal(2, fields.Describe(heat).CellSize);
    }

    [Fact]
    public void Dimensions_outside_the_documented_bounds_are_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new WorldSize(0, 16, 16));
        Assert.Throws<ArgumentOutOfRangeException>(() => new WorldSize(16, -1, 16));
        Assert.Throws<ArgumentOutOfRangeException>(() => new WorldSize(WorldSize.MaxX + 1, 16, 16));
        Assert.Throws<ArgumentOutOfRangeException>(() => new WorldSize(16, 16, WorldSize.MaxZ + 1));
        _ = new WorldSize(WorldSize.MaxX, WorldSize.MaxY, WorldSize.MaxZ);
    }

    [Fact]
    public void Chunks_are_iterated_in_morton_order_of_their_coordinates()
    {
        var (schema, _) = BuildSchema(new WorldSize(48, 40, 32)); // 3 x 3 x 2 chunks: a non-power-of-two grid
        using var world = schema.CreateWorld(new WorldOptions { WorkerThreads = 1 });
        var fields = world.Fields();

        Assert.Equal(18, fields.ChunkCount);
        ulong previous = 0;
        for (int ordinal = 0; ordinal < fields.ChunkCount; ordinal++)
        {
            var coord = fields.ChunkAt(ordinal);
            ulong code = Morton.Encode(coord.X, coord.Y, coord.Z);
            Assert.True(ordinal == 0 || code > previous, $"chunk {ordinal} ({coord}) is out of Morton order");
            previous = code;
            Assert.Equal(ordinal, fields.OrdinalOf(coord));
        }

        // Spot checks against the interleaving by hand: x bit 0, y bit 1, z bit 2, then x bit 3...
        Assert.Equal(0, fields.OrdinalOf(new ChunkCoord(0, 0, 0)));
        Assert.Equal(1, fields.OrdinalOf(new ChunkCoord(1, 0, 0)));
        Assert.Equal(2, fields.OrdinalOf(new ChunkCoord(0, 1, 0)));
        Assert.Equal(3, fields.OrdinalOf(new ChunkCoord(1, 1, 0)));
        Assert.Equal(4, fields.OrdinalOf(new ChunkCoord(0, 0, 1)));
        Assert.Equal(8, fields.OrdinalOf(new ChunkCoord(2, 0, 0)));
    }

    [Fact]
    public void Morton_codes_interleave_bits_and_round_trip()
    {
        Assert.Equal(0UL, Morton.Encode(0, 0, 0));
        Assert.Equal(1UL, Morton.Encode(1, 0, 0));
        Assert.Equal(2UL, Morton.Encode(0, 1, 0));
        Assert.Equal(4UL, Morton.Encode(0, 0, 1));
        Assert.Equal(0b111UL, Morton.Encode(1, 1, 1));
        Assert.Equal(0b100_000UL, Morton.Encode(0, 0, 2));
        Assert.Equal(0b100_100UL, Morton.Encode(0, 0, 3));
        Assert.Equal(0b001_001UL, Morton.Encode(3, 0, 0));

        foreach (var (x, y, z) in new[] { (0, 0, 0), (255, 1, 31), (4095, 4095, 511), (1 << 20, 0, (1 << 20) + 5), (Morton.MaxCoordinate, Morton.MaxCoordinate, Morton.MaxCoordinate) })
        {
            var decoded = Morton.Decode(Morton.Encode(x, y, z));
            Assert.Equal((x, y, z), decoded);
        }
    }

    [Fact]
    public void Cells_are_row_major_with_x_fastest()
    {
        Assert.Equal(0, FieldChunk.CellIndex(0, 0, 0));
        Assert.Equal(1, FieldChunk.CellIndex(1, 0, 0));
        Assert.Equal(16, FieldChunk.CellIndex(0, 1, 0));
        Assert.Equal(256, FieldChunk.CellIndex(0, 0, 1));
        Assert.Equal(4095, FieldChunk.CellIndex(15, 15, 15));
    }

    [Fact]
    public void Uniform_chunks_allocate_no_cell_storage_until_written()
    {
        var (schema, heat) = BuildSchema(new WorldSize(4096, 4096, 16)); // 65536 chunks: dense storage would be 512 MiB
        using var world = schema.CreateWorld(new WorldOptions { WorkerThreads = 1 });
        var fields = world.Fields();
        var accessor = fields.Accessor(heat);

        Assert.Equal(65536, fields.ChunkCount);
        Assert.Equal(0, fields.CellBytesAllocated);
        for (int ordinal = 0; ordinal < fields.ChunkCount; ordinal += 977)
        {
            Assert.True(accessor.IsUniform(ordinal));
            Assert.Equal(0, accessor.UniformValue(ordinal).Value);
        }

        Assert.Equal(0, accessor[4000, 4000, 15].Value);

        accessor[17, 3, 0] = new Heat { Value = 9 };
        int written = fields.OrdinalOf(new ChunkCoord(1, 0, 0));
        Assert.False(accessor.IsUniform(written));
        Assert.Equal(4096 * 2, fields.CellBytesAllocated);
        Assert.Equal(9, accessor[17, 3, 0].Value);
        Assert.Equal(0, accessor[18, 3, 0].Value);
        Assert.True(accessor.IsUniform(fields.OrdinalOf(new ChunkCoord(0, 0, 0))));

        // A whole-chunk fill of a uniform chunk stays uniform: no storage.
        accessor.FillChunk(fields.OrdinalOf(new ChunkCoord(2, 2, 0)), new Heat { Value = 5 });
        Assert.True(accessor.IsUniform(fields.OrdinalOf(new ChunkCoord(2, 2, 0))));
        Assert.Equal(5, accessor[40, 40, 0].Value);
        Assert.Equal(4096 * 2, fields.CellBytesAllocated);
    }

    [Fact]
    public void A_field_has_a_stable_name_and_layout_hash_and_is_found_by_name()
    {
        var builder = new WorldBuilder().DebugValidation(false);
        var fields = builder.AddFields(new WorldSize(16, 16, 16));
        var heat = fields.AddField<Heat>("heat");
        var height = fields.AddField<int>("height");
        Assert.Throws<ArgumentException>(() => fields.AddField<int>("heat"));
        var schema = builder.Build();
        Assert.Throws<InvalidOperationException>(() => fields.AddField<int>("late"));

        using var world = schema.CreateWorld(new WorldOptions { WorkerThreads = 1 });
        var set = world.Fields();
        Assert.Equal(heat, set.Field<Heat>("heat"));
        Assert.Equal(height, set.Field<int>("height"));
        Assert.NotEqual(set.Describe(heat).LayoutHash, set.Describe(height).LayoutHash);
        Assert.Throws<ArgumentException>(() => set.Field<Heat>("height"));

        // Different field sets or sizes give different schema hashes; identical registrations give equal ones.
        var again = new WorldBuilder().DebugValidation(false);
        var againFields = again.AddFields(new WorldSize(16, 16, 16));
        againFields.AddField<Heat>("heat");
        againFields.AddField<int>("height");
        Assert.Equal(schema.SchemaHash, again.Build().SchemaHash);

        var other = new WorldBuilder().DebugValidation(false);
        other.AddFields(new WorldSize(32, 16, 16)).AddField<Heat>("heat");
        Assert.NotEqual(schema.SchemaHash, other.Build().SchemaHash);
    }
}
