using System.Runtime.InteropServices;

namespace Colony.Persistence;

/// <summary>Builds a fresh World from a verified container (spec section 7.1): schema table matched by name, layouts bulk-copied or migrated, rebuildable state recreated.</summary>
internal static unsafe class WorldLoader
{
    /// <summary>Per saved Component: where it goes in the schema and how its bytes get there.</summary>
    private sealed class ColumnPlan
    {
        public required SavedComponent Saved { get; init; }

        public required ComponentDescriptor Current { get; init; }

        /// <summary>Null when the layout hash matches (bulk copy).</summary>
        public ComponentMigration[]? Chain { get; init; }

        public int MaxStepSize { get; init; }
    }

    public static World Load(SaveContainer.Reader reader, WorldSchema schema, LoadOptions options)
    {
        var manifest = reader.Manifest;
        var plans = PlanComponents(manifest, schema, options.Migrations);
        PlanResources(manifest, schema);
        PlanExtensions(manifest, schema);

        var entities = reader.Section(SaveContainer.EntitiesSection);
        var er = new ByteReader(entities, SaveContainer.EntitiesSection);
        int highWater = er.ReadCount(8);
        int freeCount = er.ReadCount(4);
        var generations = MemoryMarshal.Cast<byte, uint>(er.ReadBytes(highWater * 4));
        var freedEpochs = MemoryMarshal.Cast<byte, uint>(er.ReadBytes(highWater * 4));
        var freeSlots = MemoryMarshal.Cast<byte, int>(er.ReadBytes(freeCount * 4));
        er.ExpectEnd();

        var host = options.WorldOptions;
        var world = schema.CreateWorld(new WorldOptions
        {
            Seed = manifest.Seed,
            InitialEntityCapacity = Math.Max(host.InitialEntityCapacity, highWater),
            WorkerThreads = host.WorkerThreads,
            TickGate = host.TickGate,
            DebugListener = host.DebugListener,
            Diagnostics = host.Diagnostics,
        });

        try
        {
            if (!world.IsFresh)
            {
                throw new InvalidOperationException("A save can only be loaded into a fresh World.");
            }

            world.RestoreEntityIndex(highWater, generations, freedEpochs, freeSlots);
            LoadArchetypes(reader, world, plans);
            LoadNonFragmenting(reader, world, plans);
            LoadResources(reader, world, manifest);
            LoadTables(reader, world);
            LoadWorldSection(reader, world, manifest);
            world.MarkAllChunksChanged();
            world.RebuildRelationIndex();
            world.LoadExtensions(name => new SectionReader(reader, name, SaveContainer.ExtensionPrefix + name + "/"));

            if (options.VerifyWorldHash && manifest.SchemaHash == schema.SchemaHash)
            {
                ulong hash = world.Hash();
                if (hash != manifest.WorldHash)
                {
                    throw new SaveCorruptionException(SaveContainer.ManifestSection, $"The loaded World hashes to 0x{hash:X16} but the save recorded 0x{manifest.WorldHash:X16}: the sections are mutually inconsistent.");
                }
            }

            return world;
        }
        catch
        {
            world.Dispose();
            throw;
        }
    }

    private static ColumnPlan[] PlanComponents(SaveManifest manifest, WorldSchema schema, MigrationRegistry migrations)
    {
        var plans = new ColumnPlan[manifest.Components.Length];
        for (int i = 0; i < plans.Length; i++)
        {
            var saved = manifest.Components[i];
            if (!schema.TryGetComponent(saved.Name, out var type))
            {
                throw new MigrationException($"Component '{saved.Name}' (saved layout 0x{saved.LayoutHash:X16}) is not registered in the schema; a removed Component cannot be loaded.");
            }

            var current = schema.Describe(type);
            const ComponentOptions storage = ComponentOptions.NonFragmenting | ComponentOptions.Relation | ComponentOptions.Buffer;
            if ((saved.Flags & storage) != (current.Flags & storage))
            {
                throw new MigrationException($"Component '{saved.Name}' was saved with storage flags {saved.Flags & storage} but is registered with {current.Flags & storage}; storage mode changes cannot be migrated.");
            }

            ComponentMigration[]? chain = null;
            int maxStep = 0;
            if (saved.LayoutHash != current.LayoutHash)
            {
                if (current.IsBuffer)
                {
                    throw new MigrationException($"Buffer Component '{saved.Name}' changed layout (saved 0x{saved.LayoutHash:X16}, schema 0x{current.LayoutHash:X16}); buffer Components cannot be migrated.");
                }

                chain = migrations.Resolve(saved.Name, saved.LayoutHash, saved.Size, current.LayoutHash, current.Size);
                maxStep = Math.Max(saved.Size, current.Size);
                foreach (var step in chain)
                {
                    maxStep = Math.Max(maxStep, Math.Max(step.FromSize, step.ToSize));
                }
            }
            else if (saved.Size != current.Size)
            {
                throw new SaveFormatException($"Component '{saved.Name}' has layout 0x{saved.LayoutHash:X16} in both the save and the schema but different sizes ({saved.Size} vs {current.Size}).");
            }

            plans[i] = new ColumnPlan { Saved = saved, Current = current, Chain = chain, MaxStepSize = maxStep };
        }

        return plans;
    }

    private static void PlanResources(SaveManifest manifest, WorldSchema schema)
    {
        foreach (var saved in manifest.Resources)
        {
            if (!saved.IsPersistent)
            {
                continue;
            }

            if (!schema.TryGetResource(saved.Name, out var type))
            {
                throw new MigrationException($"Persistent Resource '{saved.Name}' is not registered in the schema.");
            }

            var current = schema.DescribeResource(type);
            if (current.LayoutHash != saved.LayoutHash || current.Kind != saved.Kind)
            {
                throw new MigrationException($"Resource '{saved.Name}' changed layout (saved 0x{saved.LayoutHash:X16}, schema 0x{current.LayoutHash:X16}); Resource migrations are not supported.");
            }
        }
    }

    private static void PlanExtensions(SaveManifest manifest, WorldSchema schema)
    {
        foreach (var saved in manifest.Extensions)
        {
            bool found = false;
            foreach (var current in schema.Extensions)
            {
                if (string.Equals(current.Name, saved.Name, StringComparison.Ordinal))
                {
                    found = true;
                    if (current.LayoutHash != saved.LayoutHash)
                    {
                        throw new MigrationException($"Extension '{saved.Name}' changed layout (saved 0x{saved.LayoutHash:X16}, schema 0x{current.LayoutHash:X16}); extension migrations are not supported.");
                    }
                }
            }

            if (!found)
            {
                throw new MigrationException($"Extension '{saved.Name}' is not registered in the schema.");
            }
        }
    }

    private static void LoadArchetypes(SaveContainer.Reader reader, World world, ColumnPlan[] plans)
    {
        var schema = world.Schema;
        var index = world.EntityIndex;
        var heap = world.BufferHeap;
        var table = new ByteReader(reader.Section(SaveContainer.ArchetypesSection), SaveContainer.ArchetypesSection);
        int archetypeCount = table.ReadCount(4);
        Span<byte> scratch = stackalloc byte[0];
        byte[]? migrationScratch = null;
        for (int a = 0; a < archetypeCount; a++)
        {
            int componentCount = table.ReadCount(4);
            var savedIds = new int[componentCount];
            var signature = ComponentMask.Empty;
            for (int i = 0; i < componentCount; i++)
            {
                int id = table.ReadInt32();
                if ((uint)id >= (uint)plans.Length)
                {
                    throw new SaveFormatException($"Archetype {a} names saved Component id {id}, which the manifest does not list.");
                }

                savedIds[i] = id;
                signature = signature.With(plans[id].Current.Type);
            }

            var archetype = world.RestoreArchetype(signature);
            string section = SaveContainer.ArchetypePrefix + a.ToString(global::System.Globalization.CultureInfo.InvariantCulture);
            var r = new ByteReader(reader.Section(section), section);
            int chunkCount = r.ReadCount(4);
            for (int c = 0; c < chunkCount; c++)
            {
                int rows = r.ReadInt32();
                bool asleep = r.ReadByte() != 0;
                if (rows <= 0 || rows > archetype.Capacity)
                {
                    throw new SaveFormatException($"Section '{section}' chunk {c} holds {rows} rows; the archetype's capacity is {archetype.Capacity}.");
                }

                int chunk = archetype.AppendChunk(world.Allocator, rows);
                byte* memory = archetype.ChunkPointer(chunk);
                var entityColumn = r.ReadBytes(rows * Archetype.EntityColumnElementSize);
                entityColumn.CopyTo(new Span<byte>(memory, entityColumn.Length));
                for (int row = 0; row < rows; row++)
                {
                    var id = new EntityId(((ulong*)memory)[row]);
                    if ((uint)id.Index >= (uint)index.HighWater || index.GenerationAt(id.Index) != id.Generation)
                    {
                        throw new SaveFormatException($"Section '{section}' chunk {c} row {row} holds {id}, which the saved Entity index does not know.");
                    }

                    index.RestoreSlot(id.Index, archetype.Id, chunk, row);
                }

                foreach (int savedId in savedIds)
                {
                    var plan = plans[savedId];
                    var current = plan.Current;
                    int column = archetype.ColumnOf(current.Type.Id);
                    byte* destination = memory + archetype.Offsets[column];
                    int size = current.Size;
                    if (current.IsBuffer)
                    {
                        for (int row = 0; row < rows; row++)
                        {
                            ReadBuffer(ref r, destination + ((long)row * size), in current, heap, section);
                        }
                    }
                    else if (plan.Chain is null)
                    {
                        r.ReadBytes(rows * size).CopyTo(new Span<byte>(destination, rows * size));
                    }
                    else
                    {
                        migrationScratch ??= new byte[plan.MaxStepSize * 2];
                        if (migrationScratch.Length < plan.MaxStepSize * 2)
                        {
                            migrationScratch = new byte[plan.MaxStepSize * 2];
                        }

                        for (int row = 0; row < rows; row++)
                        {
                            Migrate(plan, r.ReadBytes(plan.Saved.Size), new Span<byte>(destination + ((long)row * size), size), migrationScratch);
                        }
                    }

                    var bits = r.ReadBytes((rows + 7) / 8);
                    bits.CopyTo(new Span<byte>(memory + archetype.EnabledOffsets[column], bits.Length));
                }

                archetype.RecountEnabled(chunk);
                archetype.SetAsleep(chunk, asleep);
            }

            r.ExpectEnd();
        }

        table.ExpectEnd();
        _ = scratch;
    }

    private static void LoadNonFragmenting(SaveContainer.Reader reader, World world, ColumnPlan[] plans)
    {
        var schema = world.Schema;
        var index = world.EntityIndex;
        var store = world.NonFragmentingStore;
        var heap = world.BufferHeap;
        var r = new ByteReader(reader.Section(SaveContainer.NonFragmentingSection), SaveContainer.NonFragmentingSection);
        byte[]? migrationScratch = null;
        for (int savedId = 0; savedId < plans.Length; savedId++)
        {
            var plan = plans[savedId];
            if (!plan.Current.IsNonFragmenting)
            {
                continue;
            }

            int slot = schema.NonFragmentingSlots[plan.Current.Type.Id];
            int size = plan.Current.Size;
            int count = r.ReadCount(5);
            for (int n = 0; n < count; n++)
            {
                int entity = r.ReadInt32();
                if (!index.IsAliveSlot(entity))
                {
                    throw new SaveFormatException($"Section '{SaveContainer.NonFragmentingSection}' gives Component '{plan.Current.Name}' to dead Entity slot {entity}.");
                }

                store.Set(slot, entity, null);
                byte* destination = store.Pointer(slot, entity);
                var current = plan.Current;
                if (current.IsBuffer)
                {
                    ReadBuffer(ref r, destination, in current, heap, SaveContainer.NonFragmentingSection);
                }
                else if (plan.Chain is null)
                {
                    r.ReadBytes(size).CopyTo(new Span<byte>(destination, size));
                }
                else
                {
                    migrationScratch ??= new byte[plan.MaxStepSize * 2];
                    if (migrationScratch.Length < plan.MaxStepSize * 2)
                    {
                        migrationScratch = new byte[plan.MaxStepSize * 2];
                    }

                    Migrate(plan, r.ReadBytes(plan.Saved.Size), new Span<byte>(destination, size), migrationScratch);
                }

                store.SetEnabled(slot, entity, r.ReadByte() != 0);
            }
        }

        r.ExpectEnd();
    }

    private static void LoadResources(SaveContainer.Reader reader, World world, SaveManifest manifest)
    {
        var schema = world.Schema;
        var resources = world.ResourceStore;
        var r = new ByteReader(reader.Section(SaveContainer.ResourcesSection), SaveContainer.ResourcesSection);
        foreach (var saved in manifest.Resources)
        {
            if (!saved.IsPersistent)
            {
                continue;
            }

            var type = schema.Resource(saved.Name);
            var current = schema.DescribeResource(type);
            if (saved.Kind == ResourceKind.Value)
            {
                var bytes = r.ReadBytes(saved.Size);
                if (current.IsPersistent)
                {
                    bytes.CopyTo(new Span<byte>(resources.Pointer(type.Id), saved.Size));
                }
            }
            else
            {
                int count = r.ReadCount(saved.Size);
                for (int i = 0; i < count; i++)
                {
                    var record = r.ReadBytes(saved.Size);
                    if (current.IsPersistent)
                    {
                        fixed (byte* source = record)
                        {
                            resources.Append(type.Id, source);
                        }
                    }
                }
            }
        }

        r.ExpectEnd();
    }

    private static void LoadTables(SaveContainer.Reader reader, World world)
    {
        var s = new ByteReader(reader.Section(SaveContainer.StringsSection), SaveContainer.StringsSection);
        int count = s.ReadCount(4);
        for (int i = 0; i < count; i++)
        {
            int length = s.ReadCount(2);
            var text = MemoryMarshal.Cast<byte, char>(s.ReadBytes(length * 2));
            var id = world.Strings.Intern(text);
            if (id.Value != (uint)(i + 1))
            {
                throw new SaveFormatException($"Section '{SaveContainer.StringsSection}' lists string {i + 1} twice; the string table cannot be restored.");
            }
        }

        s.ExpectEnd();

        var b = new ByteReader(reader.Section(SaveContainer.BlobsSection), SaveContainer.BlobsSection);
        count = b.ReadCount(8);
        for (int i = 0; i < count; i++)
        {
            int elementSize = b.ReadInt32();
            int length = b.ReadInt32();
            if (elementSize <= 0 || length < 0)
            {
                throw new SaveFormatException($"Section '{SaveContainer.BlobsSection}' blob {i + 1} has element size {elementSize} and length {length}.");
            }

            world.Blobs.RestoreBlob(elementSize, length, b.ReadBytes(elementSize * length));
        }

        b.ExpectEnd();
    }

    private static void LoadWorldSection(SaveContainer.Reader reader, World world, SaveManifest manifest)
    {
        var r = new ByteReader(reader.Section(SaveContainer.WorldSection), SaveContainer.WorldSection);
        ulong randomCounter = r.ReadUInt64();
        uint structuralEpoch = r.ReadUInt32();
        uint changeCounter = r.ReadUInt32();
        long creates = r.ReadInt64();
        long destroys = r.ReadInt64();
        long adds = r.ReadInt64();
        long removes = r.ReadInt64();
        long stale = r.ReadInt64();
        r.ExpectEnd();
        world.RestoreSharedRandom(randomCounter);
        world.RestoreCounters(new StructuralCounters(creates, destroys, adds, removes, stale), structuralEpoch, changeCounter);
        world.RestoreTick(manifest.Tick);
    }

    /// <summary>Restores a buffer Component from its logical content: inline when it fits, else into a fresh heap block.</summary>
    private static void ReadBuffer(ref ByteReader r, byte* component, in ComponentDescriptor descriptor, BufferHeap heap, string section)
    {
        int length = r.ReadInt32();
        if (length < 0 || (long)length * descriptor.ElementSize > BufferHeap.MaxBlockSize)
        {
            throw new SaveFormatException($"Section '{section}' holds a buffer '{descriptor.Name}' of {length} elements.");
        }

        var elements = r.ReadBytes(length * descriptor.ElementSize);
        var header = (BufferHeader*)component;
        header->Length = length;
        header->Handle = 0;
        if (length <= descriptor.InlineCapacity)
        {
            elements.CopyTo(new Span<byte>(component + descriptor.InlineOffset, elements.Length));
        }
        else
        {
            uint handle = heap.Alloc(elements.Length, fromInline: true);
            elements.CopyTo(new Span<byte>(heap.Pointer(handle), elements.Length));
            header->Handle = handle;
        }
    }

    private static void Migrate(ColumnPlan plan, ReadOnlySpan<byte> saved, Span<byte> destination, byte[] scratch)
    {
        var chain = plan.Chain!;
        int half = scratch.Length / 2;
        ReadOnlySpan<byte> source = saved;
        for (int i = 0; i < chain.Length; i++)
        {
            var step = chain[i];
            Span<byte> target = i == chain.Length - 1 ? destination : scratch.AsSpan((i & 1) * half, step.ToSize);
            target.Clear();
            step.Migrate(source, target);
            source = target;
        }
    }

    private sealed class SectionReader : ExtensionStateReader
    {
        private readonly SaveContainer.Reader _reader;
        private readonly string _prefix;
        private readonly List<string> _names;

        public SectionReader(SaveContainer.Reader reader, string extension, string prefix)
        {
            _reader = reader;
            _prefix = prefix;
            ExtensionName = extension;
            _names = reader.NamesWithPrefix(prefix);
        }

        public override string ExtensionName { get; }

        public override int SectionCount => _names.Count;

        public override string SectionName(int index) => _names[index][_prefix.Length..];

        public override bool TryGetSection(string name, out ReadOnlySpan<byte> payload)
        {
            bool found = _reader.TryGetSection(_prefix + name, out var bytes);
            payload = bytes;
            return found;
        }
    }
}
