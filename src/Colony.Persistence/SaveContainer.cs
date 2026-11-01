using System.Buffers.Binary;
using Colony.Hashing;

namespace Colony.Persistence;

/// <summary>
/// The container layout (spec section 7.1), little-endian throughout:
/// <list type="bullet">
/// <item>Header (48 bytes): magic <c>CLNYSAVE</c>, container version, header size, section count, flags, table-of-contents offset, length and hash.</item>
/// <item>Sections back to back: the manifest first, then the Entity index, the archetype table, one section per archetype, the non-fragmenting Components, Resources, string table, blob store, the World counters, and one section per extension section (Field chunks).</item>
/// <item>Table of contents: per section its name, compression, offset, stored length, raw length and the XXH64 of the raw bytes.</item>
/// </list>
/// Every section's hash is verified before its bytes are used; a mismatch names the section.
/// </summary>
internal static class SaveContainer
{
    public const int Version = 1;
    public const int HeaderSize = 48;
    public const ulong SectionHashSeed = 0x5345_4354UL; // "SECT"
    public const ulong TocHashSeed = 0x544F_4320UL;     // "TOC "

    public const string ManifestSection = "manifest";
    public const string EntitiesSection = "entities";
    public const string ArchetypesSection = "archetypes";
    public const string ArchetypePrefix = "archetype/";
    public const string NonFragmentingSection = "nonfragmenting";
    public const string ResourcesSection = "resources";
    public const string StringsSection = "strings";
    public const string BlobsSection = "blobs";
    public const string WorldSection = "world";
    public const string ExtensionPrefix = "ext/";

    private static ReadOnlySpan<byte> Magic => "CLNYSAVE"u8;

    /// <summary>Header flag: the container is a replay log (spec section 7.2), not a World save.</summary>
    public const uint ReplayFlag = 1;

    /// <summary>Header flag: the container is a join-in-progress package (spec section 7.4).</summary>
    public const uint JoinFlag = 2;

    /// <summary>Lays out the sections into one container image.</summary>
    public static byte[] Encode(IReadOnlyList<ContainerSection> sections, SaveOptions options, uint flags = 0)
    {
        var w = new ByteWriter(4096);
        w.WriteBytes(Magic);
        w.WriteUInt32(Version);
        w.WriteUInt32(HeaderSize);
        w.WriteUInt32((uint)sections.Count);
        w.WriteUInt32(flags);
        int tocFields = w.Position;
        w.Skip(24);

        var entries = new TocEntry[sections.Count];
        byte[]? scratch = null;
        for (int i = 0; i < sections.Count; i++)
        {
            var section = sections[i];
            var raw = section.Payload;
            ulong hash = Hash64.Compute(raw, SectionHashSeed);
            ReadOnlySpan<byte> stored = raw;
            byte compression = (byte)SaveCompression.None;
            if (options.Compression == SaveCompression.Lz4 && raw.Length >= Math.Max(1, options.CompressionThreshold))
            {
                int bound = Lz4Block.MaxCompressedLength(raw.Length);
                if (scratch is null || scratch.Length < bound)
                {
                    scratch = new byte[bound];
                }

                int compressed = Lz4Block.Compress(raw, scratch);
                if (compressed < raw.Length)
                {
                    stored = scratch.AsSpan(0, compressed);
                    compression = (byte)SaveCompression.Lz4;
                }
            }

            entries[i] = new TocEntry(section.Name, compression, (ulong)w.Position, (ulong)stored.Length, (ulong)raw.Length, hash);
            w.WriteBytes(stored);
        }

        int tocOffset = w.Position;
        foreach (var e in entries)
        {
            w.WriteString(e.Name);
            w.WriteByte(e.Compression);
            w.WriteUInt64(e.Offset);
            w.WriteUInt64(e.StoredLength);
            w.WriteUInt64(e.RawLength);
            w.WriteUInt64(e.RawHash);
        }

        int tocLength = w.Position - tocOffset;
        ulong tocHash = Hash64.Compute(w.WrittenSpan.Slice(tocOffset, tocLength), TocHashSeed);
        w.WriteUInt64At(tocFields, (ulong)tocOffset);
        w.WriteUInt64At(tocFields + 8, (ulong)tocLength);
        w.WriteUInt64At(tocFields + 16, tocHash);
        return w.ToArray();
    }

    public static bool HasMagic(ReadOnlySpan<byte> bytes) => bytes.Length >= Magic.Length && bytes[..Magic.Length].SequenceEqual(Magic);

    internal readonly record struct TocEntry(string Name, byte Compression, ulong Offset, ulong StoredLength, ulong RawLength, ulong RawHash);

    /// <summary>Parses and validates the header and table of contents; sections are decoded (and verified) on demand.</summary>
    public sealed class Reader
    {
        private readonly ReadOnlyMemory<byte> _image;
        private readonly TocEntry[] _entries;
        private readonly Dictionary<string, int> _byName = new(StringComparer.Ordinal);
        private readonly Dictionary<string, byte[]> _decoded = new(StringComparer.Ordinal);
        private SaveManifest? _manifest;

        public Reader(ReadOnlyMemory<byte> image)
        {
            _image = image;
            var bytes = image.Span;
            if (bytes.Length < HeaderSize || !HasMagic(bytes))
            {
                throw new SaveFormatException("The data is not a Colony save container (bad magic or too short).");
            }

            ContainerVersion = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]);
            if (ContainerVersion != Version)
            {
                throw new SaveFormatException($"Save container version {ContainerVersion} is not supported by this build (version {Version}).");
            }

            uint headerSize = BinaryPrimitives.ReadUInt32LittleEndian(bytes[12..]);
            uint sectionCount = BinaryPrimitives.ReadUInt32LittleEndian(bytes[16..]);
            Flags = BinaryPrimitives.ReadUInt32LittleEndian(bytes[20..]);
            ulong tocOffset = BinaryPrimitives.ReadUInt64LittleEndian(bytes[24..]);
            ulong tocLength = BinaryPrimitives.ReadUInt64LittleEndian(bytes[32..]);
            ulong tocHash = BinaryPrimitives.ReadUInt64LittleEndian(bytes[40..]);
            if (headerSize != HeaderSize || tocOffset < HeaderSize || tocOffset + tocLength > (ulong)bytes.Length)
            {
                throw new SaveFormatException("The save header or table of contents lies outside the data.");
            }

            var toc = bytes.Slice((int)tocOffset, (int)tocLength);
            if (Hash64.Compute(toc, TocHashSeed) != tocHash)
            {
                throw new SaveCorruptionException("table of contents", "The save's table of contents failed its hash check; the file is corrupt or truncated.");
            }

            var r = new ByteReader(toc, "table of contents");
            _entries = new TocEntry[sectionCount];
            for (int i = 0; i < _entries.Length; i++)
            {
                string name = r.ReadString();
                byte compression = r.ReadByte();
                ulong offset = r.ReadUInt64();
                ulong stored = r.ReadUInt64();
                ulong raw = r.ReadUInt64();
                ulong hash = r.ReadUInt64();
                if (offset < HeaderSize || offset + stored > tocOffset || raw > int.MaxValue)
                {
                    throw new SaveFormatException($"Section '{name}' lies outside the save data.");
                }

                _entries[i] = new TocEntry(name, compression, offset, stored, raw, hash);
                if (!_byName.TryAdd(name, i))
                {
                    throw new SaveFormatException($"The save lists section '{name}' twice.");
                }
            }

            r.ExpectEnd();
        }

        public int ContainerVersion { get; }

        /// <summary>Header flags (<see cref="ReplayFlag"/>).</summary>
        public uint Flags { get; }

        public int SectionCount => _entries.Length;

        public string SectionName(int index) => _entries[index].Name;

        public bool IsCompressed(string name) => _entries[_byName[name]].Compression != (byte)SaveCompression.None;

        public bool Has(string name) => _byName.ContainsKey(name);

        /// <summary>Where a section's stored bytes lie in the image (tests corrupt them in place).</summary>
        internal TocEntry Entry(string name) => _entries[_byName[name]];

        /// <summary>Offset and length of the table of contents inside the image.</summary>
        internal (int Offset, int Length) TocRange
        {
            get
            {
                var bytes = _image.Span;
                return ((int)BinaryPrimitives.ReadUInt64LittleEndian(bytes[24..]), (int)BinaryPrimitives.ReadUInt64LittleEndian(bytes[32..]));
            }
        }

        public SaveManifest Manifest => _manifest ??= SaveManifest.Decode(Section(ManifestSection), ContainerVersion);

        /// <summary>Every section name starting with <paramref name="prefix"/>, in container order.</summary>
        public List<string> NamesWithPrefix(string prefix)
        {
            var names = new List<string>();
            foreach (var e in _entries)
            {
                if (e.Name.StartsWith(prefix, StringComparison.Ordinal))
                {
                    names.Add(e.Name);
                }
            }

            return names;
        }

        /// <summary>The verified raw bytes of a section; throws <see cref="SaveFormatException"/> when absent.</summary>
        public byte[] Section(string name)
        {
            if (!TryGetSection(name, out var payload))
            {
                throw new SaveFormatException($"The save has no section '{name}'.");
            }

            return payload;
        }

        public bool TryGetSection(string name, out byte[] payload)
        {
            if (_decoded.TryGetValue(name, out payload!))
            {
                return true;
            }

            if (!_byName.TryGetValue(name, out int index))
            {
                payload = [];
                return false;
            }

            var entry = _entries[index];
            var stored = _image.Span.Slice((int)entry.Offset, (int)entry.StoredLength);
            byte[] raw;
            if (entry.Compression == (byte)SaveCompression.None)
            {
                if (entry.StoredLength != entry.RawLength)
                {
                    throw new SaveCorruptionException(name, $"Section '{name}' is stored uncompressed but its stored and raw lengths differ.");
                }

                raw = stored.ToArray();
            }
            else if (entry.Compression == (byte)SaveCompression.Lz4)
            {
                raw = new byte[(int)entry.RawLength];
                try
                {
                    Lz4Block.Decompress(stored, raw);
                }
                catch (InvalidDataException e)
                {
                    throw new SaveCorruptionException(name, $"Section '{name}' is corrupt: it does not decompress ({e.Message}).", e);
                }
            }
            else
            {
                throw new SaveFormatException($"Section '{name}' uses unknown compression {entry.Compression}.");
            }

            ulong hash = Hash64.Compute(raw, SectionHashSeed);
            if (hash != entry.RawHash)
            {
                throw new SaveCorruptionException(name, $"Section '{name}' is corrupt: its hash is 0x{hash:X16} but the table of contents recorded 0x{entry.RawHash:X16}.");
            }

            _decoded[name] = raw;
            payload = raw;
            return true;
        }
    }
}

/// <summary>One raw section of a save before it is laid out in the container.</summary>
internal sealed record ContainerSection(string Name, byte[] Payload);
