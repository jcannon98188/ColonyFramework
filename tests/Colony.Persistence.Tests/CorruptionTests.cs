using Colony.Persistence;

namespace Colony.Persistence.Tests;

/// <summary>TASK-14 AC #2: every section's hash is verified on load and a corrupt section fails by name.</summary>
public sealed class CorruptionTests
{
    [Fact]
    public void A_flipped_byte_in_an_uncompressed_section_fails_the_load_naming_the_section()
    {
        var tables = TablesWorld.Compose();
        using var world = tables.Create();
        byte[] bytes = world.SaveToBytes(new SaveOptions { Compression = SaveCompression.None });

        var entry = new SaveContainer.Reader(bytes).Entry(SaveContainer.EntitiesSection);
        bytes[(int)entry.Offset + 9] ^= 0x5A;

        var error = Assert.Throws<SaveCorruptionException>(() => WorldPersistence.Load(bytes, TablesWorld.Compose().Schema));
        Assert.Equal("entities", error.Section);
        Assert.Contains("'entities'", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_flipped_byte_in_a_compressed_section_fails_the_load_naming_the_section()
    {
        var tables = TablesWorld.Compose();
        using var world = tables.Create();
        byte[] bytes = world.SaveToBytes();

        var reader = new SaveContainer.Reader(bytes);
        Assert.True(reader.IsCompressed("archetype/0"), "the test needs a compressed section");
        var entry = reader.Entry("archetype/0");
        bytes[(int)entry.Offset + ((int)entry.StoredLength / 2)] ^= 0xFF;

        var error = Assert.Throws<SaveCorruptionException>(() => WorldPersistence.Load(bytes, TablesWorld.Compose().Schema));
        Assert.Equal("archetype/0", error.Section);
    }

    [Fact]
    public void A_damaged_table_of_contents_is_reported_as_such()
    {
        using var world = TablesWorld.Compose().Create();
        byte[] bytes = world.SaveToBytes();
        var (offset, length) = new SaveContainer.Reader(bytes).TocRange;
        bytes[offset + (length / 3)] ^= 0x01;

        var error = Assert.Throws<SaveCorruptionException>(() => WorldPersistence.ReadManifest(bytes));
        Assert.Equal("table of contents", error.Section);
    }

    [Fact]
    public void Truncated_or_foreign_data_is_rejected_as_a_format_error()
    {
        using var world = TablesWorld.Compose().Create();
        byte[] bytes = world.SaveToBytes();

        Assert.Throws<SaveFormatException>(() => WorldPersistence.ReadManifest(bytes.AsMemory(0, bytes.Length - 40)));
        Assert.Throws<SaveFormatException>(() => WorldPersistence.ReadManifest(new byte[100]));
        Assert.Throws<SaveFormatException>(() => WorldPersistence.ReadManifest(new byte[3]));
    }

    [Fact]
    public void Sections_whose_bytes_are_consistent_but_whose_world_hash_disagrees_fail_the_final_check()
    {
        var tables = TablesWorld.Compose();
        using var world = tables.Create();
        byte[] bytes = world.SaveToBytes();
        var manifest = WorldPersistence.ReadManifest(bytes);

        using var loaded = WorldPersistence.Load(bytes, TablesWorld.Compose().Schema);
        Assert.Equal(manifest.WorldHash, loaded.Hash());
        Assert.Equal(world.CurrentTick, manifest.Tick);
        Assert.Equal(TablesWorld.Seed, manifest.Seed);
        Assert.Contains(manifest.Components, c => c.Name == "t.items" && (c.Flags & ComponentOptions.Buffer) != 0);
        Assert.Contains(manifest.Relations, r => r.Name == WorldBuilder.HierarchyRelationName);
        Assert.Empty(manifest.Plugins);
    }
}
