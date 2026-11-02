using System.Runtime.InteropServices;
using Colony.Persistence;

namespace Colony.Persistence.Tests;

public struct TallyV1
{
    public long Total;
}

public struct TallyV2
{
    public long Total;
    public int Samples;
}

public struct TallyV3
{
    public long Total;
    public long Samples;
    public int Flags;
}

public struct NoteV1
{
    public int Value;
}

public struct NoteV2
{
    public int Value;
    public int Source;
}

public struct Pulse
{
    public int Beat;
}

/// <summary>TASK-37 AC #1 and #3: a persistent Resource whose layout hash changed is migrated through registered chained N to N+1 migrations (values and event queue records alike); a missing link fails naming the Resource and both layouts.</summary>
public sealed class ResourceMigrationTests
{
    private const string Tally = "r.tally";
    private const string Notes = "r.notes";
    private const int NoteCount = 5;

    [Fact]
    public void A_changed_resource_layout_without_a_registered_migration_fails_naming_the_resource_and_both_layouts()
    {
        byte[] bytes = SaveV1();
        var v2 = Schema<TallyV2, NoteV1>();
        ulong from = ResourceLayoutOf<TallyV1, NoteV1>(Tally);
        ulong to = ResourceLayoutOf<TallyV2, NoteV1>(Tally);

        var error = Assert.Throws<MigrationException>(() => WorldPersistence.Load(bytes, v2));

        Assert.Contains($"Resource '{Tally}'", error.Message, StringComparison.Ordinal);
        Assert.Contains($"0x{from:X16}", error.Message, StringComparison.Ordinal);
        Assert.Contains($"0x{to:X16}", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_registered_migration_rewrites_the_saved_value_into_the_new_layout()
    {
        byte[] bytes = SaveV1();
        var v2 = Schema<TallyV2, NoteV1>();
        var migrations = new MigrationRegistry().AddResource<TallyV1, TallyV2>(Tally, ResourceLayoutOf<TallyV1, NoteV1>(Tally), ResourceLayoutOf<TallyV2, NoteV1>(Tally), TallyV1ToV2);

        using var loaded = WorldPersistence.Load(bytes, v2, new LoadOptions { Migrations = migrations });

        ref var tally = ref loaded.Resource<TallyV2>(v2.Resource<TallyV2>());
        Assert.Equal(1234L, tally.Total);
        Assert.Equal(12, tally.Samples);
        Assert.Equal(NoteCount, loaded.Events<NoteV1>(v2.Resource<NoteV1>()).Length);
    }

    [Fact]
    public void Resource_migrations_chain_through_every_intermediate_layout()
    {
        byte[] bytes = SaveV1();
        var v3 = Schema<TallyV3, NoteV1>();
        var migrations = new MigrationRegistry()
            .AddResource<TallyV2, TallyV3>(Tally, ResourceLayoutOf<TallyV2, NoteV1>(Tally), ResourceLayoutOf<TallyV3, NoteV1>(Tally), TallyV2ToV3)
            .AddResource<TallyV1, TallyV2>(Tally, ResourceLayoutOf<TallyV1, NoteV1>(Tally), ResourceLayoutOf<TallyV2, NoteV1>(Tally), TallyV1ToV2);

        using var loaded = WorldPersistence.Load(bytes, v3, new LoadOptions { Migrations = migrations });

        ref var tally = ref loaded.Resource<TallyV3>(v3.Resource<TallyV3>());
        Assert.Equal(1234L, tally.Total);
        Assert.Equal(12L, tally.Samples);
        Assert.Equal(9, tally.Flags);
    }

    [Fact]
    public void A_resource_chain_with_a_missing_link_names_the_layout_it_stopped_at()
    {
        byte[] bytes = SaveV1();
        var v3 = Schema<TallyV3, NoteV1>();
        var migrations = new MigrationRegistry().AddResource<TallyV1, TallyV2>(Tally, ResourceLayoutOf<TallyV1, NoteV1>(Tally), ResourceLayoutOf<TallyV2, NoteV1>(Tally), TallyV1ToV2);

        var error = Assert.Throws<MigrationException>(() => WorldPersistence.Load(bytes, v3, new LoadOptions { Migrations = migrations }));

        Assert.Contains($"Resource '{Tally}'", error.Message, StringComparison.Ordinal);
        Assert.Contains($"0x{ResourceLayoutOf<TallyV2, NoteV1>(Tally):X16}", error.Message, StringComparison.Ordinal);
        Assert.Contains("1 step(s) applied", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_persistent_event_queue_migrates_every_pending_record()
    {
        byte[] bytes = SaveV1();
        var v2 = Schema<TallyV1, NoteV2>();
        var migrations = new MigrationRegistry().AddResource<NoteV1, NoteV2>(Notes, ResourceLayoutOf<TallyV1, NoteV1>(Notes), ResourceLayoutOf<TallyV1, NoteV2>(Notes), NoteV1ToV2);

        using var loaded = WorldPersistence.Load(bytes, v2, new LoadOptions { Migrations = migrations });

        var notes = loaded.Events<NoteV2>(v2.Resource<NoteV2>());
        Assert.Equal(NoteCount, notes.Length);
        for (int i = 0; i < notes.Length; i++)
        {
            Assert.Equal(i * 10, notes[i].Value);
            Assert.Equal(77, notes[i].Source);
        }

        Assert.Equal(1234L, loaded.Resource<TallyV1>(v2.Resource<TallyV1>()).Total);
    }

    [Fact]
    public void A_migration_between_a_value_and_a_queue_is_refused()
    {
        byte[] bytes = SaveV1();
        var asValueSchema = new WorldBuilder().AddResource<TallyV1>(Tally).AddResource<NoteV1>(Notes).AddComponent<Pulse>("r.pulse").DebugValidation(false).Build();
        ulong from = ResourceLayoutOf<TallyV1, NoteV1>(Notes);
        ulong to = asValueSchema.DescribeResource(asValueSchema.Resource<NoteV1>()).LayoutHash;
        var migrations = new MigrationRegistry().AddResource<NoteV1, NoteV1>(Notes, from, to, static (s, d) => s.CopyTo(d));

        var error = Assert.Throws<MigrationException>(() => WorldPersistence.Load(bytes, asValueSchema, new LoadOptions { Migrations = migrations }));

        Assert.Contains($"Resource '{Notes}'", error.Message, StringComparison.Ordinal);
        Assert.Contains("EventQueue", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Registering_two_migrations_from_one_resource_layout_is_refused()
    {
        var registry = new MigrationRegistry().AddResource<TallyV1, TallyV2>(Tally, 1, 2, TallyV1ToV2);
        Assert.Throws<ArgumentException>(() => registry.AddResource<TallyV1, TallyV3>(Tally, 1, 3, TallyV1ToV2));
        Assert.Equal(1, registry.ResourceCount);
    }

    private static void TallyV1ToV2(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        var from = MemoryMarshal.Read<TallyV1>(source);
        MemoryMarshal.Write(destination, new TallyV2 { Total = from.Total, Samples = 12 });
    }

    private static void TallyV2ToV3(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        var from = MemoryMarshal.Read<TallyV2>(source);
        MemoryMarshal.Write(destination, new TallyV3 { Total = from.Total, Samples = from.Samples, Flags = 9 });
    }

    private static void NoteV1ToV2(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        var from = MemoryMarshal.Read<NoteV1>(source);
        MemoryMarshal.Write(destination, new NoteV2 { Value = from.Value, Source = 77 });
    }

    private static WorldSchema Schema<TTally, TNote>()
        where TTally : unmanaged
        where TNote : unmanaged
        => new WorldBuilder().AddResource<TTally>(Tally).AddEvent<TNote>(Notes, EventOptions.Persistent).AddComponent<Pulse>("r.pulse").DebugValidation(false).Build();

    private static ulong ResourceLayoutOf<TTally, TNote>(string name)
        where TTally : unmanaged
        where TNote : unmanaged
    {
        var schema = Schema<TTally, TNote>();
        return schema.DescribeResource(schema.Resource(name)).LayoutHash;
    }

    private static byte[] SaveV1()
    {
        var schema = Schema<TallyV1, NoteV1>();
        using var world = schema.CreateWorld(new WorldOptions { Seed = 5, WorkerThreads = 1, InitialEntityCapacity = 64 });
        world.Resource<TallyV1>(schema.Resource<TallyV1>()).Total = 1234;
        var buffer = world.CreateCommandBuffer();
        var notes = schema.Resource<NoteV1>();
        for (int i = 0; i < NoteCount; i++)
        {
            buffer.Emit(notes, new NoteV1 { Value = i * 10 });
        }

        world.Playback(buffer);
        return world.SaveToBytes();
    }
}
