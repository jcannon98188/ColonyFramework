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

    /// <summary>Per saved persistent Resource: the schema's descriptor and the chain that gets the saved bytes there (null = bulk copy; a dropped Resource has no plan).</summary>
    private sealed class ResourcePlan
    {
        public required SavedResource Saved { get; init; }

        public required ResourceDescriptor Current { get; init; }

        public ResourceMigration[]? Chain { get; init; }

        public int MaxStepSize { get; init; }
    }

    public static World Load(SaveContainer.Reader reader, WorldSchema schema, LoadOptions options)
    {
        var manifest = reader.Manifest;
        CheckPlugins(manifest, schema, options);
        var plans = PlanComponents(manifest, schema, options);
        var resourcePlans = PlanResources(manifest, schema, options);
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
            LoadArchetypes(reader, world, manifest, plans);
            LoadNonFragmenting(reader, world, manifest, plans);
            LoadResources(reader, world, manifest, resourcePlans);
            LoadTables(reader, world);
            // Change versions are rebuildable (spec section 7.1): every chunk counts as changed once, before the
            // saved change counter is restored so the counter itself continues from the save.
            world.MarkAllChunksChanged();
            LoadWorldSection(reader, world, manifest);
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

    /// <summary>A save made with a Plugin the schema lacks fails here unless the host opted into dropping what that Plugin owned (spec section 8).</summary>
    private static void CheckPlugins(SaveManifest manifest, WorldSchema schema, LoadOptions options)
    {
        if (options.DropUnknownComponents)
        {
            return;
        }

        foreach (var saved in manifest.Plugins)
        {
            if (!schema.TryGetPlugin(saved.Id, out _))
            {
                throw new MissingPluginException(saved.Id, $"The save was made with Plugin '{saved.Id}' {saved.Version}, which the schema does not have ({saved.Components.Length} Components, {saved.Resources.Length} Resources). Load with LoadOptions.DropUnknownComponents to drop them and continue without the Plugin.");
            }
        }
    }

    /// <summary>One plan per saved Component; null where the Component is unknown and <see cref="LoadOptions.DropUnknownComponents"/> drops it.</summary>
    private static ColumnPlan?[] PlanComponents(SaveManifest manifest, WorldSchema schema, LoadOptions options)
    {
        var migrations = options.Migrations;
        var plans = new ColumnPlan?[manifest.Components.Length];
        for (int i = 0; i < plans.Length; i++)
        {
            var saved = manifest.Components[i];
            if (!schema.TryGetComponent(saved.Name, out var type))
            {
                if (options.DropUnknownComponents)
                {
                    continue;
                }

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

    /// <summary>One plan per saved Resource (null for transient ones, and for unknown ones in drop mode); layout changes resolve to a migration chain or fail naming the Resource and both layouts.</summary>
    private static ResourcePlan?[] PlanResources(SaveManifest manifest, WorldSchema schema, LoadOptions options)
    {
        var plans = new ResourcePlan?[manifest.Resources.Length];
        for (int i = 0; i < plans.Length; i++)
        {
            var saved = manifest.Resources[i];
            if (!saved.IsPersistent)
            {
                continue;
            }

            if (!schema.TryGetResource(saved.Name, out var type))
            {
                if (options.DropUnknownComponents)
                {
                    continue;
                }

                throw new MigrationException($"Persistent Resource '{saved.Name}' is not registered in the schema.");
            }

            var current = schema.DescribeResource(type);
            if (current.Kind != saved.Kind)
            {
                throw new MigrationException($"Resource '{saved.Name}' was saved as a {saved.Kind} but is registered as a {current.Kind}; a Resource's kind cannot be migrated.");
            }

            if (!current.IsPersistent)
            {
                // The schema made it transient: the saved bytes are stepped over, nothing to migrate.
                continue;
            }

            ResourceMigration[]? chain = null;
            int maxStep = 0;
            if (saved.LayoutHash != current.LayoutHash)
            {
                chain = options.Migrations.ResolveResource(saved.Name, saved.LayoutHash, saved.Size, current.LayoutHash, current.Size);
                maxStep = Math.Max(saved.Size, current.Size);
                foreach (var step in chain)
                {
                    maxStep = Math.Max(maxStep, Math.Max(step.FromSize, step.ToSize));
                }
            }
            else if (saved.Size != current.Size)
            {
                throw new SaveFormatException($"Resource '{saved.Name}' has layout 0x{saved.LayoutHash:X16} in both the save and the schema but different sizes ({saved.Size} vs {current.Size}).");
            }

            plans[i] = new ResourcePlan { Saved = saved, Current = current, Chain = chain, MaxStepSize = maxStep };
        }

        return plans;
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

    private static void LoadArchetypes(SaveContainer.Reader reader, World world, SaveManifest manifest, ColumnPlan?[] plans)
    {
        var schema = world.Schema;
        var index = world.EntityIndex;
        var heap = world.BufferHeap;
        // Dropping Components can give two saved archetypes the same signature; they merge into one.
        bool dropping = Array.IndexOf(plans, null) >= 0;
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
                if (plans[id] is { } kept)
                {
                    signature = signature.With(kept.Current.Type);
                }
            }

            var archetype = world.RestoreArchetype(signature, mergeExisting: dropping);
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

                if (dropping)
                {
                    // A merged archetype cannot take whole chunks (only its last chunk may be partial): rows go in one by one.
                    LoadRowsScattered(ref r, world, archetype, manifest, plans, savedIds, rows, section, ref migrationScratch);
                    continue;
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
                    if (plan is null)
                    {
                        // Dropped: step over the column's values and its enable bits.
                        var dropped = manifest.Components[savedId];
                        if ((dropped.Flags & ComponentOptions.Buffer) != 0)
                        {
                            for (int row = 0; row < rows; row++)
                            {
                                SkipBuffer(ref r, in dropped, section);
                            }
                        }
                        else
                        {
                            r.ReadBytes(rows * dropped.Size);
                        }

                        r.ReadBytes((rows + 7) / 8);
                        continue;
                    }

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

    /// <summary>
    /// Drop mode's chunk loader: appends each saved row through <see cref="Archetype.AddRow"/> so saved archetypes
    /// that collapsed onto one signature share it, then scatters every kept column's bytes and enable bits to the
    /// rows' new places. Dropped columns are stepped over. Sleep state is not kept (it is a rebuildable optimisation).
    /// </summary>
    private static void LoadRowsScattered(ref ByteReader r, World world, Archetype archetype, SaveManifest manifest, ColumnPlan?[] plans, int[] savedIds, int rows, string section, ref byte[]? migrationScratch)
    {
        var index = world.EntityIndex;
        var heap = world.BufferHeap;
        var placed = new (int Chunk, int Row)[rows];
        var entityColumn = MemoryMarshal.Cast<byte, ulong>(r.ReadBytes(rows * Archetype.EntityColumnElementSize));
        for (int row = 0; row < rows; row++)
        {
            var id = new EntityId(entityColumn[row]);
            if ((uint)id.Index >= (uint)index.HighWater || index.GenerationAt(id.Index) != id.Generation)
            {
                throw new SaveFormatException($"Section '{section}' row {row} holds {id}, which the saved Entity index does not know.");
            }

            var slot = archetype.AddRow(world.Allocator);
            ((ulong*)archetype.ChunkPointer(slot.Chunk))[slot.Row] = entityColumn[row];
            index.RestoreSlot(id.Index, archetype.Id, slot.Chunk, slot.Row);
            placed[row] = slot;
        }

        foreach (int savedId in savedIds)
        {
            var plan = plans[savedId];
            if (plan is null)
            {
                var dropped = manifest.Components[savedId];
                if ((dropped.Flags & ComponentOptions.Buffer) != 0)
                {
                    for (int row = 0; row < rows; row++)
                    {
                        SkipBuffer(ref r, in dropped, section);
                    }
                }
                else
                {
                    r.ReadBytes(rows * dropped.Size);
                }

                r.ReadBytes((rows + 7) / 8);
                continue;
            }

            var current = plan.Current;
            int column = archetype.ColumnOf(current.Type.Id);
            int offset = archetype.Offsets[column];
            int size = current.Size;
            for (int row = 0; row < rows; row++)
            {
                var (chunk, slotRow) = placed[row];
                byte* destination = archetype.ChunkPointer(chunk) + offset + ((long)slotRow * size);
                if (current.IsBuffer)
                {
                    ReadBuffer(ref r, destination, in current, heap, section);
                }
                else if (plan.Chain is null)
                {
                    r.ReadBytes(size).CopyTo(new Span<byte>(destination, size));
                }
                else
                {
                    if (migrationScratch is null || migrationScratch.Length < plan.MaxStepSize * 2)
                    {
                        migrationScratch = new byte[plan.MaxStepSize * 2];
                    }

                    Migrate(plan, r.ReadBytes(plan.Saved.Size), new Span<byte>(destination, size), migrationScratch);
                }
            }

            // AddRow enables every Component; clear the bits the save had clear.
            var bits = r.ReadBytes((rows + 7) / 8);
            for (int row = 0; row < rows; row++)
            {
                if ((bits[row >> 3] & (1 << (row & 7))) == 0)
                {
                    var (chunk, slotRow) = placed[row];
                    archetype.SetEnabled(chunk, column, slotRow, false);
                }
            }
        }
    }

    private static void LoadNonFragmenting(SaveContainer.Reader reader, World world, SaveManifest manifest, ColumnPlan?[] plans)
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
            if (plan is null)
            {
                var dropped = manifest.Components[savedId];
                if ((dropped.Flags & ComponentOptions.NonFragmenting) == 0)
                {
                    continue;
                }

                int droppedCount = r.ReadCount(5);
                for (int n = 0; n < droppedCount; n++)
                {
                    r.ReadInt32();
                    if ((dropped.Flags & ComponentOptions.Buffer) != 0)
                    {
                        SkipBuffer(ref r, in dropped, SaveContainer.NonFragmentingSection);
                    }
                    else
                    {
                        r.ReadBytes(dropped.Size);
                    }

                    r.ReadByte();
                }

                continue;
            }

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

    private static void LoadResources(SaveContainer.Reader reader, World world, SaveManifest manifest, ResourcePlan?[] plans)
    {
        var resources = world.ResourceStore;
        var r = new ByteReader(reader.Section(SaveContainer.ResourcesSection), SaveContainer.ResourcesSection);
        byte[]? migrationScratch = null;
        for (int i = 0; i < plans.Length; i++)
        {
            var saved = manifest.Resources[i];
            if (!saved.IsPersistent)
            {
                continue;
            }

            // No plan: unknown in drop mode, or transient in the schema (PlanResources threw otherwise). The bytes are stepped over.
            var plan = plans[i];
            if (plan is { Chain: not null } && (migrationScratch is null || migrationScratch.Length < plan.MaxStepSize * 2))
            {
                migrationScratch = new byte[plan.MaxStepSize * 2];
            }

            if (saved.Kind == ResourceKind.Value)
            {
                var bytes = r.ReadBytes(saved.Size);
                if (plan is null)
                {
                    continue;
                }

                var destination = new Span<byte>(resources.Pointer(plan.Current.Type.Id), plan.Current.Size);
                if (plan.Chain is null)
                {
                    bytes.CopyTo(destination);
                }
                else
                {
                    MigrateResource(plan, bytes, destination, migrationScratch!);
                }
            }
            else
            {
                int count = r.ReadCount(saved.Size);
                byte[]? record = plan is null ? null : new byte[plan.Current.Size];
                for (int n = 0; n < count; n++)
                {
                    var bytes = r.ReadBytes(saved.Size);
                    if (plan is null)
                    {
                        continue;
                    }

                    if (plan.Chain is null)
                    {
                        bytes.CopyTo(record);
                    }
                    else
                    {
                        MigrateResource(plan, bytes, record, migrationScratch!);
                    }

                    fixed (byte* source = record)
                    {
                        resources.Append(plan.Current.Type.Id, source);
                    }
                }
            }
        }

        r.ExpectEnd();
    }

    private static void MigrateResource(ResourcePlan plan, ReadOnlySpan<byte> saved, Span<byte> destination, byte[] scratch)
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

    /// <summary>Steps over a dropped buffer Component's saved content (length, then elements) without restoring it.</summary>
    private static void SkipBuffer(ref ByteReader r, in SavedComponent dropped, string section)
    {
        int length = r.ReadInt32();
        if (length < 0 || (long)length * dropped.ElementSize > BufferHeap.MaxBlockSize)
        {
            throw new SaveFormatException($"Section '{section}' holds a buffer '{dropped.Name}' of {length} elements.");
        }

        r.ReadBytes(length * dropped.ElementSize);
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
