using System.Reflection;
using Colony.Fields;
using Colony.Numerics;

namespace Colony.Fields.Tests;

/// <summary>AC #3: grid Entities carry TilePos (3 ints) with an optional fixed-point sub-tile offset and no floats.</summary>
public sealed class GridEntityTests
{
    [Fact]
    public void TilePos_is_three_ints_and_SubTileOffset_is_fixed_point()
    {
        Assert.Equal(12, System.Runtime.CompilerServices.Unsafe.SizeOf<TilePos>());
        Assert.Equal(12, System.Runtime.CompilerServices.Unsafe.SizeOf<SubTileOffset>());
        Assert.All(FieldTypes(typeof(TilePos)), t => Assert.Equal(typeof(int), t));
        Assert.All(FieldTypes(typeof(SubTileOffset)), t => Assert.Equal(typeof(int), t));

        var offset = new SubTileOffset(Fixed32.FromDouble(0.25), Fixed32.FromDouble(0.5), Fixed32.Zero);
        Assert.Equal(Fixed32.FromDouble(0.25), offset.X);
        Assert.Equal(Fixed32.FromDouble(0.5), offset.Value.Y);
    }

    [Fact]
    public void TilePos_maps_to_its_chunk_and_cell()
    {
        var tile = new TilePos(35, 17, 2);
        Assert.Equal(new ChunkCoord(2, 1, 0), tile.Chunk);
        Assert.Equal(FieldChunk.CellIndex(3, 1, 2), tile.CellIndex);
        Assert.Equal(new TilePos(32, 16, 0), tile.Chunk.Origin);
        Assert.Equal(new TilePos(36, 19, 5), tile + new TilePos(1, 2, 3));
    }

    [Fact]
    public void Grid_components_register_on_the_core_builder_and_read_through_the_field_accessor()
    {
        var builder = new WorldBuilder()
            .AddComponent<TilePos>("tile.pos")
            .AddComponent<SubTileOffset>("tile.offset")
            .DebugValidation(false);
        var registration = builder.AddFields(new WorldSize(32, 32, 1));
        var height = registration.AddField<int>("height");
        var schema = builder.Build();
        using var world = schema.CreateWorld(new WorldOptions { WorkerThreads = 1 });
        var pos = schema.Component<TilePos>();
        var offset = schema.Component<SubTileOffset>();

        var buffer = world.CreateCommandBuffer();
        var e = buffer.Create(ComponentMask.Of(pos, offset));
        buffer.Add(e, pos, new TilePos(20, 5, 0));
        buffer.Add(e, offset, new SubTileOffset(Fixed32.FromDouble(0.5), Fixed32.Zero, Fixed32.Zero));
        world.Playback(buffer);
        world.ResetScratch();

        var fields = world.Fields();
        var heights = fields.Accessor(height);
        heights[20, 5, 0] = 42;
        EntityId id = default;
        foreach (var chunk in world.Query(pos))
        {
            id = chunk.Entities[0];
        }

        var tile = world.Get<TilePos>(id, pos);
        Assert.True(fields.Size.Contains(tile));
        Assert.Equal(42, heights[tile]);
        Assert.Equal(Fixed32.FromDouble(0.5), world.Get<SubTileOffset>(id, offset).X);
    }

    private static IEnumerable<Type> FieldTypes(Type type)
    {
        foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            if (field.FieldType.IsPrimitive)
            {
                yield return field.FieldType;
            }
            else
            {
                foreach (var nested in FieldTypes(field.FieldType))
                {
                    yield return nested;
                }
            }
        }
    }
}
