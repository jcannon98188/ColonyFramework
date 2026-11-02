using System.Runtime.CompilerServices;
using Colony.Fields;

namespace Colony.Fields.Tests;

/// <summary>
/// Spec section 6: the World hash excludes padding (TASK-44). A cell struct with padding written with poisoned
/// padding bytes (what the JIT leaves in a struct built field by field: the Linux CI failure of
/// <see cref="FieldHashTests"/>) hashes, compares and saves like the same cell with clean padding, whichever
/// path wrote it.
/// </summary>
public sealed class FieldPaddingTests
{
    /// <summary>Four bytes: A (0-1), B (2), one padding byte (3).</summary>
    public struct Cell
    {
        public ushort A;
        public byte B;
    }

    private static (WorldSchema Schema, Field<Cell> Field) BuildSchema()
    {
        var builder = new WorldBuilder().DebugValidation(false);
        var fields = builder.AddFields(new WorldSize(48, 32, 16));
        var field = fields.AddField<Cell>("cell", Poisoned(1, 2, 0x5A));
        return (builder.Build(), field);
    }

    private static Cell Poisoned(ushort a, byte b, byte poison)
    {
        var cell = new Cell { A = a, B = b };
        Unsafe.Add(ref Unsafe.As<Cell, byte>(ref cell), 3) = poison;
        return cell;
    }

    [Fact]
    public void Tile_writes_with_different_padding_hash_equal()
    {
        var (schema, field) = BuildSchema();
        using var a = schema.CreateWorld(new WorldOptions { WorkerThreads = 1 });
        using var b = schema.CreateWorld(new WorldOptions { WorkerThreads = 1 });
        a.Fields().Accessor(field)[40, 20, 3] = Poisoned(9, 9, 0x00);
        b.Fields().Accessor(field)[40, 20, 3] = Poisoned(9, 9, 0xAB);

        Assert.Equal(a.Hash(), b.Hash());
        Assert.True(WorldHashTree.Compare(a.HashTree(), b.HashTree()).Identical);
    }

    [Fact]
    public void Chunk_span_fill_and_raw_chunk_writes_with_different_padding_hash_equal()
    {
        var (schema, field) = BuildSchema();
        using var a = schema.CreateWorld(new WorldOptions { WorkerThreads = 1 });
        using var b = schema.CreateWorld(new WorldOptions { WorkerThreads = 1 });
        Write(a, 0x00);
        Write(b, 0xCD);

        Assert.Equal(a.Hash(), b.Hash());

        void Write(World world, byte poison)
        {
            var fields = world.Fields();
            var accessor = fields.Accessor(field);
            accessor.WriteChunk(0)[17] = Poisoned(3, 4, poison);
            accessor.FillChunk(1, Poisoned(5, 6, poison));
            var cells = new Cell[FieldChunk.CellCount];
            Array.Fill(cells, Poisoned(7, 8, poison));
            cells[5] = Poisoned(1, 1, poison);
            fields.WriteChunkCells(field.Id, 2, System.Runtime.InteropServices.MemoryMarshal.AsBytes(cells.AsSpan()));
        }
    }

    [Fact]
    public void Writing_the_uniform_value_with_other_padding_keeps_the_chunk_uniform_and_unwritten()
    {
        var (schema, field) = BuildSchema();
        using var world = schema.CreateWorld(new WorldOptions { WorkerThreads = 1 });
        var accessor = world.Fields().Accessor(field);
        accessor[1, 1, 1] = Poisoned(1, 2, 0xEE);

        Assert.True(accessor.IsUniform(0));
        Assert.Equal(0u, accessor.VersionOf(0));
    }

    [Fact]
    public void Padding_differences_do_not_reach_saved_chunk_bytes()
    {
        var (schema, field) = BuildSchema();
        using var a = schema.CreateWorld(new WorldOptions { WorkerThreads = 1 });
        using var b = schema.CreateWorld(new WorldOptions { WorkerThreads = 1 });
        a.Fields().Accessor(field)[2, 2, 2] = Poisoned(4, 4, 0x11);
        b.Fields().Accessor(field)[2, 2, 2] = Poisoned(4, 4, 0x77);

        Assert.Equal(SaveFields(a), SaveFields(b));
    }

    private static byte[] SaveFields(World world)
    {
        var writer = new CapturingWriter();
        ((IWorldExtension)world.Fields()).Save(writer);
        return [.. writer.Bytes];
    }

    private sealed class CapturingWriter : ExtensionStateWriter
    {
        public List<byte> Bytes { get; } = [];

        public override void WriteSection(string name, ReadOnlySpan<byte> payload)
        {
            Bytes.AddRange(System.Text.Encoding.UTF8.GetBytes(name));
            Bytes.AddRange(payload);
        }
    }
}
