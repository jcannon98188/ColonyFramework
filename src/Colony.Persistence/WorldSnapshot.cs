using System.Runtime.InteropServices;

namespace Colony.Persistence;

/// <summary>
/// The saved state of a World copied out at one Tick boundary (spec section 7.1): every section's raw
/// bytes, ready to be laid out into a container. Taking the snapshot is the only part of a save that touches
/// the World; <see cref="Encode"/>, <see cref="Write"/> and <see cref="WriteFile"/> run anywhere, any time
/// later, and produce the bytes a synchronous save at that Tick produces. This is the background-save path:
/// the host snapshots between Ticks and keeps simulating while a worker encodes and writes.
/// </summary>
public sealed class WorldSnapshot
{
    private readonly List<ContainerSection> _sections;

    private WorldSnapshot(SaveManifest manifest, List<ContainerSection> sections)
    {
        Manifest = manifest;
        _sections = sections;
    }

    public SaveManifest Manifest { get; }

    public long Tick => Manifest.Tick;

    public ulong WorldHash => Manifest.WorldHash;

    public int SectionCount => _sections.Count;

    /// <summary>Section names in container order.</summary>
    public string SectionName(int index) => _sections[index].Name;

    /// <summary>The raw payload of a section, for tooling and tests.</summary>
    public ReadOnlySpan<byte> SectionPayload(int index) => _sections[index].Payload;

    /// <summary>Total raw bytes of every section (before compression).</summary>
    public long RawBytes
    {
        get
        {
            long total = 0;
            foreach (var s in _sections)
            {
                total += s.Payload.Length;
            }

            return total;
        }
    }

    /// <summary>Lays the sections out as a container image.</summary>
    public byte[] Encode(SaveOptions? options = null) => SaveContainer.Encode(_sections, options ?? SaveOptions.Default);

    public void Write(Stream destination, SaveOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(destination);
        destination.Write(Encode(options));
    }

    /// <summary>Writes the container to a temporary file beside <paramref name="path"/>, flushes it to disk, rotates the backup generations and renames it into place.</summary>
    public void WriteFile(string path, SaveOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        options ??= SaveOptions.Default;
        AtomicFile.Write(path, Encode(options), options.BackupGenerations);
    }

    /// <summary>Copies every saved section out of <paramref name="world"/>. Call it at a Tick boundary on the Tick thread.</summary>
    internal static unsafe WorldSnapshot Capture(World world)
    {
        var schema = world.Schema;
        ulong worldHash = world.Hash();
        var manifest = SaveManifest.Capture(world, worldHash);
        var sections = new List<ContainerSection> { new(SaveContainer.ManifestSection, manifest.Encode()) };

        // Entity index: generations and free list decide future ids, so they are saved exactly.
        var index = world.EntityIndex;
        {
            int highWater = index.HighWater;
            var free = index.FreeSlots;
            var w = new ByteWriter(8 + (highWater * 8) + (free.Length * 4));
            w.WriteInt32(highWater);
            w.WriteInt32(free.Length);
            for (int i = 0; i < highWater; i++)
            {
                w.WriteUInt32(index.GenerationAt(i));
            }

            for (int i = 0; i < highWater; i++)
            {
                w.WriteUInt32(index.FreedEpochAt(i));
            }

            w.WriteBytes(MemoryMarshal.AsBytes(free));
            sections.Add(new ContainerSection(SaveContainer.EntitiesSection, w.ToArray()));
        }

        // Archetype table (all of them, empty ones included: ids are creation order and order the Systems' jobs).
        {
            var w = new ByteWriter();
            w.WriteInt32(world.ArchetypeCount);
            for (int a = 0; a < world.ArchetypeCount; a++)
            {
                var archetype = world.ArchetypeAt(a);
                w.WriteInt32(archetype.ComponentIds.Length);
                foreach (int id in archetype.ComponentIds)
                {
                    w.WriteInt32(id);
                }
            }

            sections.Add(new ContainerSection(SaveContainer.ArchetypesSection, w.ToArray()));
        }

        var heap = world.BufferHeap;
        for (int a = 0; a < world.ArchetypeCount; a++)
        {
            var archetype = world.ArchetypeAt(a);
            var w = new ByteWriter(Math.Max(16, archetype.ChunkCount * archetype.UsedBytesPerChunk));
            w.WriteInt32(archetype.ChunkCount);
            for (int c = 0; c < archetype.ChunkCount; c++)
            {
                int rows = archetype.RowCount(c);
                byte* chunk = archetype.ChunkPointer(c);
                w.WriteInt32(rows);
                w.WriteByte(archetype.IsAsleep(c) ? (byte)1 : (byte)0);
                w.WriteBytes(new ReadOnlySpan<byte>(chunk, rows * Archetype.EntityColumnElementSize));
                for (int i = 0; i < archetype.ComponentIds.Length; i++)
                {
                    ref readonly var descriptor = ref schema.Components[archetype.ComponentIds[i]];
                    if (descriptor.IsBuffer)
                    {
                        for (int row = 0; row < rows; row++)
                        {
                            WriteBuffer(w, chunk + archetype.Offsets[i] + ((long)row * descriptor.Size), in descriptor, heap);
                        }
                    }
                    else
                    {
                        w.WriteBytes(new ReadOnlySpan<byte>(chunk + archetype.Offsets[i], rows * archetype.Sizes[i]));
                    }

                    w.WriteBytes(new ReadOnlySpan<byte>(chunk + archetype.EnabledOffsets[i], (rows + 7) / 8));
                }
            }

            sections.Add(new ContainerSection(SaveContainer.ArchetypePrefix + a.ToString(global::System.Globalization.CultureInfo.InvariantCulture), w.ToArray()));
        }

        // Non-fragmenting Components (Relations included): per Component in id order, every live slot that has it.
        {
            var store = world.NonFragmentingStore;
            var w = new ByteWriter();
            for (int id = 0; id < schema.ComponentCount; id++)
            {
                int slot = schema.NonFragmentingSlots[id];
                if (slot < 0)
                {
                    continue;
                }

                ref readonly var descriptor = ref schema.Components[id];
                int count = 0;
                for (int i = 0; i < index.HighWater; i++)
                {
                    if (index.IsAliveSlot(i) && store.Has(slot, i))
                    {
                        count++;
                    }
                }

                w.WriteInt32(count);
                for (int i = 0; i < index.HighWater; i++)
                {
                    if (!index.IsAliveSlot(i) || !store.Has(slot, i))
                    {
                        continue;
                    }

                    w.WriteInt32(i);
                    if (descriptor.IsBuffer)
                    {
                        WriteBuffer(w, store.Pointer(slot, i), in descriptor, heap);
                    }
                    else
                    {
                        w.WriteBytes(new ReadOnlySpan<byte>(store.Pointer(slot, i), descriptor.Size));
                    }

                    w.WriteByte(store.IsEnabled(slot, i) ? (byte)1 : (byte)0);
                }
            }

            sections.Add(new ContainerSection(SaveContainer.NonFragmentingSection, w.ToArray()));
        }

        // Persistent Resources in id order: value bytes, or count then records for a queue.
        {
            var resources = world.ResourceStore;
            var w = new ByteWriter();
            for (int id = 0; id < schema.ResourceCount; id++)
            {
                var descriptor = schema.Resources[id];
                if (!descriptor.IsPersistent)
                {
                    continue;
                }

                if (descriptor.Kind == ResourceKind.Value)
                {
                    w.WriteBytes(new ReadOnlySpan<byte>(resources.Pointer(id), descriptor.Size));
                }
                else
                {
                    int count = resources.Count(id);
                    w.WriteInt32(count);
                    w.WriteBytes(new ReadOnlySpan<byte>(resources.QueuePointer(id), count * descriptor.Size));
                }
            }

            sections.Add(new ContainerSection(SaveContainer.ResourcesSection, w.ToArray()));
        }

        {
            var strings = world.Strings;
            var w = new ByteWriter();
            w.WriteInt32(strings.Count);
            for (int id = 1; id <= strings.Count; id++)
            {
                var text = strings.Get(new StringId((uint)id));
                w.WriteInt32(text.Length);
                w.WriteBytes(MemoryMarshal.AsBytes(text));
            }

            sections.Add(new ContainerSection(SaveContainer.StringsSection, w.ToArray()));
        }

        {
            var blobs = world.Blobs;
            var w = new ByteWriter();
            w.WriteInt32(blobs.Count);
            for (int i = 0; i < blobs.Count; i++)
            {
                w.WriteInt32(blobs.ElementSizeAt(i));
                w.WriteInt32(blobs.LengthAt(i));
                w.WriteBytes(blobs.BytesAt(i));
            }

            sections.Add(new ContainerSection(SaveContainer.BlobsSection, w.ToArray()));
        }

        {
            var counters = world.Counters;
            var w = new ByteWriter(64);
            w.WriteUInt64(world.SharedRandom.Counter);
            w.WriteUInt32(world.StructuralEpoch);
            w.WriteUInt32(world.ChangeCounter);
            w.WriteInt64(counters.Creates);
            w.WriteInt64(counters.Destroys);
            w.WriteInt64(counters.Adds);
            w.WriteInt64(counters.Removes);
            w.WriteInt64(counters.StaleSkipped);
            sections.Add(new ContainerSection(SaveContainer.WorldSection, w.ToArray()));
        }

        world.SaveExtensions(name => new SectionWriter(sections, SaveContainer.ExtensionPrefix + name + "/"));
        return new WorldSnapshot(manifest, sections);
    }

    /// <summary>A buffer Component's logical content: length then the elements, wherever they live.</summary>
    private static unsafe void WriteBuffer(ByteWriter w, byte* component, in ComponentDescriptor descriptor, BufferHeap heap)
    {
        var header = (BufferHeader*)component;
        int length = header->Length;
        byte* data = header->Handle == 0 ? component + descriptor.InlineOffset : heap.Pointer(header->Handle);
        w.WriteInt32(length);
        w.WriteBytes(new ReadOnlySpan<byte>(data, length * descriptor.ElementSize));
    }

    private sealed class SectionWriter : ExtensionStateWriter
    {
        private readonly List<ContainerSection> _sections;
        private readonly string _prefix;
        private readonly HashSet<string> _names = new(StringComparer.Ordinal);

        public SectionWriter(List<ContainerSection> sections, string prefix)
        {
            _sections = sections;
            _prefix = prefix;
        }

        public override void WriteSection(string name, ReadOnlySpan<byte> payload)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            if (!_names.Add(name))
            {
                throw new ArgumentException($"Extension section '{name}' was written twice.", nameof(name));
            }

            _sections.Add(new ContainerSection(_prefix + name, payload.ToArray()));
        }
    }
}

/// <summary>
/// Temp file, flush to disk, rotate backups, replace into place (spec section 7.1). The save path names a complete
/// file at every instant: older backups rotate first (the save itself is untouched), then one replace makes the
/// current save the first backup and the new file the save (File.Replace: ReplaceFile on Windows, link + rename
/// on Unix), and finally the directory entry is flushed so the rename survives power loss.
/// </summary>
internal static partial class AtomicFile
{
    /// <summary>Writes the save; <paramref name="step"/> is a fault-injection seam for tests, called after each step with its name.</summary>
    public static void Write(string path, byte[] bytes, int backupGenerations, Action<string>? step = null)
    {
        string directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(directory);
        string temp = path + ".tmp";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
        {
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }

        step?.Invoke("temp-written");
        if (File.Exists(path) && backupGenerations > 0)
        {
            for (int generation = backupGenerations; generation >= 2; generation--)
            {
                string older = BackupPath(path, generation - 1);
                if (File.Exists(older))
                {
                    File.Move(older, BackupPath(path, generation), overwrite: true);
                }
            }

            step?.Invoke("rotated");
            File.Replace(temp, path, BackupPath(path, 1), ignoreMetadataErrors: true);
        }
        else
        {
            File.Move(temp, path, overwrite: true);
        }

        step?.Invoke("replaced");
        FlushDirectory(directory);
        step?.Invoke("synced");
    }

    /// <summary>
    /// fsync of the directory so the renames are durable (POSIX). Windows needs none: NTFS journals the rename, and
    /// FlushFileBuffers on a directory handle needs backup privileges. Best effort: a missing libc leaves the rename
    /// to the file system's own commit interval.
    /// </summary>
    private static void FlushDirectory(string directory)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            int fd = PosixOpen(directory, 0);
            if (fd >= 0)
            {
                _ = PosixFsync(fd);
                _ = PosixClose(fd);
            }
        }
        catch (DllNotFoundException)
        {
        }
        catch (EntryPointNotFoundException)
        {
        }
    }

    [System.Runtime.InteropServices.LibraryImport("libc", EntryPoint = "open", StringMarshalling = System.Runtime.InteropServices.StringMarshalling.Utf8)]
    private static partial int PosixOpen(string path, int flags);

    [System.Runtime.InteropServices.LibraryImport("libc", EntryPoint = "fsync")]
    private static partial int PosixFsync(int fd);

    [System.Runtime.InteropServices.LibraryImport("libc", EntryPoint = "close")]
    private static partial int PosixClose(int fd);

    public static string BackupPath(string path, int generation) => path + ".bak" + generation.ToString(global::System.Globalization.CultureInfo.InvariantCulture);
}
