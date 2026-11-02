using System.Runtime.InteropServices;
using Colony.Persistence;

namespace Colony.Persistence.Tests;

public struct HealthV1
{
    public int Value;
}

public struct HealthV2
{
    public int Value;
    public int Max;
}

public struct HealthV3
{
    public long Value;
    public int Max;
    public int Flags;
}

public struct Marker
{
    public int Id;
}

/// <summary>TASK-14 AC #3: a Component whose layout hash changed is migrated through registered chained N to N+1 migrations; a missing link fails with a clear message.</summary>
public sealed class MigrationTests
{
    private const string Health = "m.health";
    private const int Count = 50;

    [Fact]
    public void A_changed_layout_without_a_registered_migration_fails_naming_the_component_and_both_layouts()
    {
        byte[] bytes = SaveV1();
        var v2 = Schema<HealthV2>();
        ulong from = LayoutOf<HealthV1>();
        ulong to = LayoutOf<HealthV2>();

        var error = Assert.Throws<MigrationException>(() => WorldPersistence.Load(bytes, v2));

        Assert.Contains(Health, error.Message, StringComparison.Ordinal);
        Assert.Contains($"0x{from:X16}", error.Message, StringComparison.Ordinal);
        Assert.Contains($"0x{to:X16}", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_registered_migration_rewrites_every_saved_value_into_the_new_layout()
    {
        byte[] bytes = SaveV1();
        var v2 = Schema<HealthV2>();
        var migrations = new MigrationRegistry().Add<HealthV1, HealthV2>(Health, LayoutOf<HealthV1>(), LayoutOf<HealthV2>(), V1ToV2);

        using var loaded = WorldPersistence.Load(bytes, v2, new LoadOptions { Migrations = migrations });

        var health = Component(v2);
        var marker = v2.Component("m.marker");
        int seen = 0;
        foreach (var chunk in loaded.Query(ComponentMask.Of(health, marker)))
        {
            var healths = chunk.Column<HealthV2>(health);
            var markers = chunk.Column<Marker>(marker);
            for (int i = 0; i < chunk.Count; i++)
            {
                Assert.Equal(markers[i].Id * 3, healths[i].Value);
                Assert.Equal(markers[i].Id * 6, healths[i].Max);
                seen++;
            }
        }

        Assert.Equal(Count, seen);
    }

    [Fact]
    public void Migrations_chain_from_the_saved_layout_through_every_intermediate_layout()
    {
        byte[] bytes = SaveV1();
        var v3 = Schema<HealthV3>();
        var migrations = new MigrationRegistry()
            .Add<HealthV2, HealthV3>(Health, LayoutOf<HealthV2>(), LayoutOf<HealthV3>(), V2ToV3)
            .Add<HealthV1, HealthV2>(Health, LayoutOf<HealthV1>(), LayoutOf<HealthV2>(), V1ToV2);

        using var loaded = WorldPersistence.Load(bytes, v3, new LoadOptions { Migrations = migrations });

        var health = Component(v3);
        var marker = v3.Component("m.marker");
        int seen = 0;
        foreach (var chunk in loaded.Query(ComponentMask.Of(health, marker)))
        {
            var healths = chunk.Column<HealthV3>(health);
            var markers = chunk.Column<Marker>(marker);
            for (int i = 0; i < chunk.Count; i++)
            {
                Assert.Equal(markers[i].Id * 3L, healths[i].Value);
                Assert.Equal(markers[i].Id * 6, healths[i].Max);
                Assert.Equal(7, healths[i].Flags);
                seen++;
            }
        }

        Assert.Equal(Count, seen);
        Assert.Equal(Count, loaded.Counters.Creates);
    }

    [Fact]
    public void A_chain_with_a_missing_link_names_the_layout_it_stopped_at()
    {
        byte[] bytes = SaveV1();
        var v3 = Schema<HealthV3>();
        var migrations = new MigrationRegistry().Add<HealthV1, HealthV2>(Health, LayoutOf<HealthV1>(), LayoutOf<HealthV2>(), V1ToV2);

        var error = Assert.Throws<MigrationException>(() => WorldPersistence.Load(bytes, v3, new LoadOptions { Migrations = migrations }));

        Assert.Contains(Health, error.Message, StringComparison.Ordinal);
        Assert.Contains($"0x{LayoutOf<HealthV2>():X16}", error.Message, StringComparison.Ordinal);
        Assert.Contains("1 step(s) applied", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_saved_component_the_schema_no_longer_has_fails_the_load()
    {
        byte[] bytes = SaveV1();
        var without = new WorldBuilder().AddComponent<Marker>("m.marker").DebugValidation(false).Build();

        var error = Assert.Throws<MigrationException>(() => WorldPersistence.Load(bytes, without));
        Assert.Contains(Health, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_identical_layout_is_bulk_copied_and_the_world_hash_is_verified()
    {
        byte[] bytes = SaveV1();
        using var loaded = WorldPersistence.Load(bytes, Schema<HealthV1>());
        Assert.Equal(WorldPersistence.ReadManifest(bytes).WorldHash, loaded.Hash());
    }

    private static void V1ToV2(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        var from = MemoryMarshal.Read<HealthV1>(source);
        MemoryMarshal.Write(destination, new HealthV2 { Value = from.Value, Max = from.Value * 2 });
    }

    private static void V2ToV3(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        var from = MemoryMarshal.Read<HealthV2>(source);
        MemoryMarshal.Write(destination, new HealthV3 { Value = from.Value, Max = from.Max, Flags = 7 });
    }

    private static WorldSchema Schema<T>()
        where T : unmanaged
        => new WorldBuilder().AddComponent<T>(Health).AddComponent<Marker>("m.marker").DebugValidation(false).Build();

    private static ComponentType Component(WorldSchema schema) => schema.Component(Health);

    private static ulong LayoutOf<T>()
        where T : unmanaged
    {
        var schema = Schema<T>();
        return schema.Describe(Component(schema)).LayoutHash;
    }

    private static byte[] SaveV1()
    {
        var schema = Schema<HealthV1>();
        using var world = schema.CreateWorld(new WorldOptions { Seed = 5, WorkerThreads = 1, InitialEntityCapacity = 64 });
        var health = Component(schema);
        var marker = schema.Component("m.marker");
        var buffer = world.CreateCommandBuffer();
        for (int i = 0; i < Count; i++)
        {
            var e = buffer.Create(ComponentMask.Of(health, marker));
            buffer.Add(e, health, new HealthV1 { Value = i * 3 });
            buffer.Add(e, marker, new Marker { Id = i });
        }

        world.Playback(buffer);
        return world.SaveToBytes();
    }
}
