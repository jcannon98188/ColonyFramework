using System.Buffers.Binary;
using Colony.Hashing;
using Colony.Persistence;

namespace Colony.Persistence.Tests;

/// <summary>
/// A test extension holding one ledger of per-Tick entries, saved in a format that depends on
/// <see cref="FormatVersion"/>: version 1 writes section <c>ledger</c> as <c>int32 count, int32 values</c>;
/// version 2 writes section <c>entries</c> as <c>int64 values</c> and a one-byte <c>flags</c> section.
/// The simulated state (and so the World hash) is the same in both.
/// </summary>
internal sealed class LedgerDefinition : WorldExtensionDefinition
{
    public const string ExtensionName = "ledger";

    public LedgerDefinition(int formatVersion)
    {
        FormatVersion = formatVersion;
    }

    public override string Name => ExtensionName;

    public override ulong LayoutHash => 0x4C45_4447_4552_0001;

    public override int FormatVersion { get; }

    public override IWorldExtension Create(World world) => new Ledger(this, world);
}

internal sealed class Ledger : IWorldExtension
{
    private readonly LedgerDefinition _definition;
    private readonly World _world;
    private readonly List<long> _entries = [];

    public Ledger(LedgerDefinition definition, World world)
    {
        _definition = definition;
        _world = world;
    }

    public IReadOnlyList<long> Entries => _entries;

    public byte Flags { get; set; }

    public void BeginTick(long tick)
    {
    }

    public void EndPhase(Phase phase)
    {
    }

    /// <summary>Appends the Tick number times the live Entity count: the same on every worker count.</summary>
    public void EndTick(long tick) => _entries.Add((tick + 1) * _world.Counters.Creates);

    public void Hash(ref Hash64State state)
    {
        state.Append(_entries.Count);
        foreach (long entry in _entries)
        {
            state.Append(entry);
        }

        state.Append(Flags);
    }

    public void Save(ExtensionStateWriter writer)
    {
        if (_definition.FormatVersion == 1)
        {
            var payload = new byte[4 + (_entries.Count * 4)];
            BinaryPrimitives.WriteInt32LittleEndian(payload, _entries.Count);
            for (int i = 0; i < _entries.Count; i++)
            {
                BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(4 + (i * 4)), checked((int)_entries[i]));
            }

            writer.WriteSection("ledger", payload);
            return;
        }

        var entries = new byte[_entries.Count * 8];
        for (int i = 0; i < _entries.Count; i++)
        {
            BinaryPrimitives.WriteInt64LittleEndian(entries.AsSpan(i * 8), _entries[i]);
        }

        writer.WriteSection("entries", entries);
        writer.WriteSection("flags", [Flags]);
    }

    public void Load(ExtensionStateReader reader)
    {
        _entries.Clear();
        if (_definition.FormatVersion == 1)
        {
            var payload = reader.Section("ledger");
            int count = BinaryPrimitives.ReadInt32LittleEndian(payload);
            for (int i = 0; i < count; i++)
            {
                _entries.Add(BinaryPrimitives.ReadInt32LittleEndian(payload[(4 + (i * 4))..]));
            }

            return;
        }

        var entries = reader.Section("entries");
        for (int i = 0; i < entries.Length / 8; i++)
        {
            _entries.Add(BinaryPrimitives.ReadInt64LittleEndian(entries[(i * 8)..]));
        }

        Flags = reader.Section("flags")[0];
    }

    public void Dispose()
    {
    }

    /// <summary>The registered format 1 to 2 migration: widen the entries and add the flags section.</summary>
    public static void MigrateV1ToV2(ExtensionSections sections)
    {
        var ledger = sections.Get("ledger").Span;
        int count = BinaryPrimitives.ReadInt32LittleEndian(ledger);
        var entries = new byte[count * 8];
        for (int i = 0; i < count; i++)
        {
            BinaryPrimitives.WriteInt64LittleEndian(entries.AsSpan(i * 8), BinaryPrimitives.ReadInt32LittleEndian(ledger[(4 + (i * 4))..]));
        }

        sections.Remove("ledger");
        sections.Set("entries", entries);
        sections.Set("flags", [0]);
    }
}

/// <summary>TASK-37 AC #2 and #3: extension sections carry a format version; older saves are brought forward through registered chained migrations before the extension loads them; a missing step or a newer save fails naming the extension.</summary>
public sealed class ExtensionMigrationTests
{
    private const int Ticks = 4;

    [Fact]
    public void The_manifest_records_the_extension_format_version()
    {
        var manifest = WorldPersistence.ReadManifest(Save(1));
        Assert.Equal(1, Assert.Single(manifest.Extensions).FormatVersion);

        manifest = WorldPersistence.ReadManifest(Save(2));
        Assert.Equal(2, Assert.Single(manifest.Extensions).FormatVersion);
    }

    [Fact]
    public void A_save_at_the_current_format_version_loads_without_migrations()
    {
        byte[] bytes = Save(2);
        using var loaded = WorldPersistence.Load(bytes, Schema(2));
        Assert.Equal(WorldPersistence.ReadManifest(bytes).WorldHash, loaded.Hash());
        Assert.Equal(Ticks, loaded.Extension<Ledger>().Entries.Count);
    }

    [Fact]
    public void An_older_format_without_a_registered_migration_fails_naming_the_extension_and_both_versions()
    {
        byte[] bytes = Save(1);

        var error = Assert.Throws<MigrationException>(() => WorldPersistence.Load(bytes, Schema(2)));

        Assert.Contains($"Extension '{LedgerDefinition.ExtensionName}'", error.Message, StringComparison.Ordinal);
        Assert.Contains("format version 1", error.Message, StringComparison.Ordinal);
        Assert.Contains("version 2", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_newer_format_than_the_build_fails_naming_the_extension()
    {
        byte[] bytes = Save(2);
        var migrations = new MigrationRegistry().AddExtension(LedgerDefinition.ExtensionName, 1, 2, Ledger.MigrateV1ToV2);

        var error = Assert.Throws<MigrationException>(() => WorldPersistence.Load(bytes, Schema(1), new LoadOptions { Migrations = migrations }));

        Assert.Contains($"Extension '{LedgerDefinition.ExtensionName}'", error.Message, StringComparison.Ordinal);
        Assert.Contains("newer", error.Message, StringComparison.Ordinal);
        Assert.Contains("format version 2", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_registered_migration_rewrites_the_sections_before_the_extension_loads_them_and_the_world_hash_is_verified()
    {
        byte[] bytes = Save(1);
        var migrations = new MigrationRegistry().AddExtension(LedgerDefinition.ExtensionName, 1, 2, Ledger.MigrateV1ToV2);

        using var loaded = WorldPersistence.Load(bytes, Schema(2), new LoadOptions { Migrations = migrations });

        var ledger = loaded.Extension<Ledger>();
        Assert.Equal(Ticks, ledger.Entries.Count);
        Assert.Equal(0, ledger.Flags);
        // The format changed, the state did not: the migrated World hashes as the saved one (verified by Load too).
        Assert.Equal(WorldPersistence.ReadManifest(bytes).WorldHash, loaded.Hash());
    }

    [Fact]
    public void Extension_migrations_chain_version_by_version()
    {
        byte[] bytes = Save(1);
        var migrations = new MigrationRegistry()
            .AddExtension(LedgerDefinition.ExtensionName, 2, 3, static sections => sections.Set("flags", [1]))
            .AddExtension(LedgerDefinition.ExtensionName, 1, 2, Ledger.MigrateV1ToV2);

        // The second step changes hashed state (flags), so the saved World hash cannot be verified after it.
        using var loaded = WorldPersistence.Load(bytes, Schema(3), new LoadOptions { Migrations = migrations, VerifyWorldHash = false });

        Assert.Equal(1, loaded.Extension<Ledger>().Flags);
        Assert.Equal(Ticks, loaded.Extension<Ledger>().Entries.Count);
    }

    [Fact]
    public void A_chain_with_a_missing_step_names_the_version_it_stopped_at()
    {
        byte[] bytes = Save(1);
        var migrations = new MigrationRegistry().AddExtension(LedgerDefinition.ExtensionName, 1, 2, Ledger.MigrateV1ToV2);

        var error = Assert.Throws<MigrationException>(() => WorldPersistence.Load(bytes, Schema(3), new LoadOptions { Migrations = migrations }));

        Assert.Contains($"Extension '{LedgerDefinition.ExtensionName}'", error.Message, StringComparison.Ordinal);
        Assert.Contains("from format version 2", error.Message, StringComparison.Ordinal);
        Assert.Contains("1 step(s) applied", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_extension_migration_cannot_bridge_a_layout_change()
    {
        byte[] bytes = Save(1);
        var migrations = new MigrationRegistry().AddExtension(LedgerDefinition.ExtensionName, 1, 2, Ledger.MigrateV1ToV2);
        var otherLayout = new WorldBuilder().AddComponent<Pulse>("x.pulse").DebugValidation(false);
        otherLayout.AddExtension(new OtherLayoutLedger());

        var error = Assert.Throws<MigrationException>(() => WorldPersistence.Load(bytes, otherLayout.Build(), new LoadOptions { Migrations = migrations }));

        Assert.Contains($"Extension '{LedgerDefinition.ExtensionName}' changed layout", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Registering_a_backwards_or_duplicate_extension_step_is_refused()
    {
        Assert.Throws<ArgumentException>(() => new ExtensionMigration(LedgerDefinition.ExtensionName, 2, 2, Ledger.MigrateV1ToV2));
        var registry = new MigrationRegistry().AddExtension(LedgerDefinition.ExtensionName, 1, 2, Ledger.MigrateV1ToV2);
        Assert.Throws<ArgumentException>(() => registry.AddExtension(LedgerDefinition.ExtensionName, 1, 3, Ledger.MigrateV1ToV2));
        Assert.Equal(1, registry.ExtensionCount);
    }

    private static WorldSchema Schema(int formatVersion)
    {
        var builder = new WorldBuilder().AddComponent<Pulse>("x.pulse").DebugValidation(false);
        builder.AddExtension(new LedgerDefinition(formatVersion));
        return builder.Build();
    }

    private static byte[] Save(int formatVersion)
    {
        var schema = Schema(formatVersion);
        using var world = schema.CreateWorld(new WorldOptions { Seed = 3, WorkerThreads = 1, InitialEntityCapacity = 64 });
        var buffer = world.CreateCommandBuffer();
        for (int i = 0; i < 10; i++)
        {
            buffer.Create(ComponentMask.Of(schema.Component<Pulse>()));
        }

        world.Playback(buffer);
        for (int t = 0; t < Ticks; t++)
        {
            world.Tick();
        }

        return world.SaveToBytes();
    }

    private sealed class OtherLayoutLedger : WorldExtensionDefinition
    {
        public override string Name => LedgerDefinition.ExtensionName;

        public override ulong LayoutHash => 0x4C45_4447_4552_0002;

        public override int FormatVersion => 2;

        public override IWorldExtension Create(World world) => new Ledger(new LedgerDefinition(2), world);
    }
}
