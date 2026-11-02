using Colony.Fields;
using Colony.Hashing;

namespace Colony.Fields.Tests;

/// <summary>AC #5: Field chunks are part of the World hash in Morton order with cells in storage order; versions and storage form are not.</summary>
public sealed class FieldHashTests
{
    public struct Cell
    {
        public ushort A;
        public byte B;
    }

    private static (WorldSchema Schema, Field<Cell> Field) BuildSchema()
    {
        var builder = new WorldBuilder().DebugValidation(false);
        var fields = builder.AddFields(new WorldSize(48, 32, 16)); // 3 x 2 x 1 chunks
        var field = fields.AddField<Cell>("cell", new Cell { A = 1, B = 2 });
        return (builder.Build(), field);
    }

    [Fact]
    public void A_cell_write_changes_the_world_hash_and_equal_fields_hash_equal()
    {
        var (schema, field) = BuildSchema();
        using var a = schema.CreateWorld(new WorldOptions { WorkerThreads = 1 });
        using var b = schema.CreateWorld(new WorldOptions { WorkerThreads = 1 });
        Assert.Equal(a.Hash(), b.Hash());

        a.Fields().Accessor(field)[40, 20, 3] = new Cell { A = 9, B = 9 };
        Assert.NotEqual(a.Hash(), b.Hash());
        b.Fields().Accessor(field)[40, 20, 3] = new Cell { A = 9, B = 9 };
        Assert.Equal(a.Hash(), b.Hash());
    }

    [Fact]
    public void Uniform_and_dense_chunks_with_the_same_cells_hash_alike_and_versions_are_excluded()
    {
        var (schema, field) = BuildSchema();
        using var uniform = schema.CreateWorld(new WorldOptions { WorkerThreads = 1 });
        using var dense = schema.CreateWorld(new WorldOptions { WorkerThreads = 1 });
        var accessor = dense.Fields().Accessor(field);
        accessor.WriteChunk(3); // expands with the uniform value, cells unchanged
        accessor.FillChunk(4, new Cell { A = 1, B = 2 }); // bumps the version, same value
        Assert.False(accessor.IsUniform(3));
        Assert.NotEqual(0u, accessor.VersionOf(4));

        Assert.Equal(uniform.Hash(), dense.Hash());
    }

    [Fact]
    public void The_hash_covers_fields_in_id_order_chunks_in_morton_order_and_cells_in_storage_order()
    {
        var builder = new WorldBuilder().DebugValidation(false);
        var registration = builder.AddFields(new WorldSize(32, 32, 16)); // 2 x 2 x 1 chunks
        var first = registration.AddField<int>("first");
        var second = registration.AddField<byte>("second", 3);
        var schema = builder.Build();
        using var world = schema.CreateWorld(new WorldOptions { WorkerThreads = 1 });
        var fields = world.Fields();
        var ints = fields.Accessor(first);
        ints[17, 0, 0] = 5; // chunk (1,0,0) = ordinal 1, cell 1
        ints[0, 16, 0] = 6; // chunk (0,1,0) = ordinal 2, cell 0

        // Recompute what the Fields extension appends, by the documented order, and check the World hash uses exactly it.
        var expected = new Hash64State(0x5748_5348UL);
        expected.Append(schema.SchemaHash);
        expected.Append(world.CurrentTick);
        // The Entity index (review #6): high water 0, no generations, an empty free list.
        expected.Append(0);
        expected.Append(0);
        // No Entities, so the archetype and non-fragmenting sections are empty; the persistent Resources follow:
        // only the built-in StructuralFailure queue (id 0), empty, hashed as its record count. Then the string table
        // and the blob store (TASK-9), both empty, hashed as their counts. Then the extensions.
        expected.Append(0);
        expected.Append(0);
        expected.Append(0);
        var cells = new int[FieldChunk.CellCount];
        for (int ordinal = 0; ordinal < 4; ordinal++)
        {
            Array.Clear(cells);
            if (ordinal == 1)
            {
                cells[1] = 5;
            }

            if (ordinal == 2)
            {
                cells[0] = 6;
            }

            expected.Append<int>(cells);
        }

        var bytes = new byte[FieldChunk.CellCount];
        Array.Fill(bytes, (byte)3);
        for (int ordinal = 0; ordinal < 4; ordinal++)
        {
            expected.Append(bytes);
        }

        expected.Append(world.SharedRandom.Stream.Seed);
        expected.Append(world.SharedRandom.Stream.Key);
        expected.Append(world.SharedRandom.Counter);
        Assert.Equal(expected.Finish(), world.Hash());
        _ = second;
    }
}
