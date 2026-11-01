using System.Buffers.Binary;
using System.Globalization;

namespace Colony.Diagnostics.Inspection;

/// <summary>One System as a hash trail file records it.</summary>
public readonly record struct HashTrailSystem(int Id, string Name, int Phase, int Wave, int Order);

/// <summary>The outcome of comparing two hash trail files: where they first diverge, named.</summary>
public sealed record HashTrailComparison(HashDivergence? Divergence, int ComparedTicks, string Description)
{
    public bool Diverged => Divergence is not null;
}

/// <summary>
/// A hash trail on disk (spec section 10.2, the desync finder): the Ticks a <see cref="HashTrailRecorder"/>
/// collected plus the schema's Phase and System names and run order, so <c>colony-inspect desync</c> can compare
/// trails from two machines or builds and name the first diverging Tick, Phase and System without the Game's
/// schema. Encoded as one <see cref="InspectCodec"/> value.
/// </summary>
public sealed class HashTrailFile
{
    public const string Format = "colony-hash-trail";

    public const int FormatVersion = 1;

    private HashTrailFile(ulong schemaHash, IReadOnlyList<string> phases, IReadOnlyList<HashTrailSystem> systems, IReadOnlyList<TickHashes> ticks)
    {
        SchemaHash = schemaHash;
        Phases = phases;
        Systems = systems;
        Ticks = ticks;
    }

    public ulong SchemaHash { get; }

    /// <summary>Phase names by Phase id.</summary>
    public IReadOnlyList<string> Phases { get; }

    public IReadOnlyList<HashTrailSystem> Systems { get; }

    public IReadOnlyList<TickHashes> Ticks { get; }

    public static HashTrailFile From(WorldSchema schema, IReadOnlyList<TickHashes> ticks)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(ticks);
        var phases = new List<string>(schema.Phases.Length);
        foreach (var phase in schema.Phases)
        {
            phases.Add(phase.Name);
        }

        var systems = new List<HashTrailSystem>(schema.Systems.Length);
        foreach (ref readonly var s in schema.Systems)
        {
            systems.Add(new HashTrailSystem(s.Id, s.Name, s.Phase.Id, s.Wave, s.Order));
        }

        return new HashTrailFile(schema.SchemaHash, phases, systems, [.. ticks]);
    }

    public byte[] Encode()
    {
        var systems = new List<InspectValue>(Systems.Count);
        foreach (var s in Systems)
        {
            systems.Add(InspectValue.Map(new("id", s.Id), new("name", s.Name), new("phase", s.Phase), new("wave", s.Wave), new("order", s.Order)));
        }

        var ticks = new List<InspectValue>(Ticks.Count);
        foreach (var t in Ticks)
        {
            ticks.Add(InspectValue.Map(
                new("tick", t.Tick),
                new("hash", InspectValue.From(t.Hash)),
                new("phases", Pack(t.PhaseHashes)),
                new("systems", Pack(t.SystemHashes)),
                new("stride", t.ArchetypeStride),
                new("archetypes", Pack(t.ArchetypeHashes))));
        }

        var phases = new List<InspectValue>(Phases.Count);
        foreach (string p in Phases)
        {
            phases.Add(p);
        }

        return InspectCodec.Encode(InspectValue.Map(
            new("format", Format),
            new("version", FormatVersion),
            new("schemaHash", InspectValue.From(SchemaHash)),
            new("phases", InspectValue.List(phases)),
            new("systems", InspectValue.List(systems)),
            new("ticks", InspectValue.List(ticks))));
    }

    public void WriteFile(string path) => File.WriteAllBytes(path, Encode());

    public static HashTrailFile ReadFile(string path) => Decode(File.ReadAllBytes(path));

    public static HashTrailFile Decode(ReadOnlySpan<byte> bytes)
    {
        var root = InspectCodec.Decode(bytes);
        if (root.Kind != InspectValueKind.Map || root["format"].Kind != InspectValueKind.Text || root["format"].AsText != Format)
        {
            throw new FormatException("Not a Colony hash trail file.");
        }

        if (root["version"].AsWhole != FormatVersion)
        {
            throw new FormatException(string.Create(CultureInfo.InvariantCulture, $"Hash trail format version {root["version"].AsWhole} is not supported (expected {FormatVersion})."));
        }

        var phases = root["phases"].Items.Select(p => p.AsText).ToList();
        var systems = root["systems"].Items.Select(s => new HashTrailSystem((int)s["id"].AsWhole, s["name"].AsText, (int)s["phase"].AsWhole, (int)s["wave"].AsWhole, (int)s["order"].AsWhole)).ToList();
        var ticks = root["ticks"].Items.Select(t => new TickHashes(
            t["tick"].AsWhole,
            (ulong)t["hash"].AsWhole,
            Unpack(t["phases"].AsBytes),
            Unpack(t["systems"].AsBytes),
            Unpack(t["archetypes"].AsBytes),
            (int)t["stride"].AsWhole)).ToList();
        return new HashTrailFile((ulong)root["schemaHash"].AsWhole, phases, systems, ticks);
    }

    /// <summary>
    /// The first Tick, Phase and System where the two trails differ (Systems examined in run order). Throws when
    /// the trails come from different schemas: their Phase and System ids would not line up.
    /// </summary>
    public static HashTrailComparison Compare(HashTrailFile a, HashTrailFile b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        if (a.SchemaHash != b.SchemaHash)
        {
            throw new ArgumentException(string.Create(CultureInfo.InvariantCulture, $"The trails come from different schemas ({a.SchemaHash:x16} vs {b.SchemaHash:x16})."), nameof(b));
        }

        var order = a.Systems.Select(s => new SystemOrder(s.Id, s.Phase, s.Wave, s.Order)).ToList();
        var divergence = HashTrail.Compare(a.Ticks, b.Ticks, order);
        int compared = Math.Min(a.Ticks.Count, b.Ticks.Count);
        return new HashTrailComparison(divergence, compared, divergence is { } d ? a.Describe(d) : string.Create(CultureInfo.InvariantCulture, $"The trails agree over {compared} Ticks."));
    }

    /// <summary>Like <see cref="HashDivergence.Describe"/>, with the names this file recorded.</summary>
    public string Describe(HashDivergence divergence)
    {
        string phase = divergence.PhaseId >= 0 && divergence.PhaseId < Phases.Count ? Phases[divergence.PhaseId] : "?";
        string system = divergence.SystemId >= 0 ? Systems.FirstOrDefault(s => s.Id == divergence.SystemId).Name ?? "?" : "(Phase boundary)";
        string archetype = divergence.Archetype >= 0 ? string.Create(CultureInfo.InvariantCulture, $"archetype {divergence.Archetype}") : "outside recorded archetypes";
        return string.Create(CultureInfo.InvariantCulture, $"Tick {divergence.Tick}, Phase {phase}, System {system}, {archetype}");
    }

    private static InspectValue Pack(ReadOnlySpan<ulong> values)
    {
        var bytes = new byte[values.Length * sizeof(ulong)];
        for (int i = 0; i < values.Length; i++)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(i * sizeof(ulong)), values[i]);
        }

        return InspectValue.From(bytes);
    }

    private static ulong[] Unpack(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length % sizeof(ulong) != 0)
        {
            throw new FormatException("A hash list is not a whole number of 8-byte hashes.");
        }

        var values = new ulong[bytes.Length / sizeof(ulong)];
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = BinaryPrimitives.ReadUInt64LittleEndian(bytes[(i * sizeof(ulong))..]);
        }

        return values;
    }
}
