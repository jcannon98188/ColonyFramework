using System.Collections.Immutable;

namespace Colony.Persistence;

/// <summary>A Game-defined session option peers must agree on (input delay, map, difficulty...): compared by ordinal name and value.</summary>
public readonly record struct SessionSetting(string Name, string Value);

/// <summary>One Plugin of a peer's Plugin set, in load order (spec section 8).</summary>
public readonly record struct PeerPlugin(string Id, string Version, bool Deterministic, int AbiVersion);

/// <summary>What a Game adds to a <see cref="PeerIdentity"/> beyond what the schema and seed say.</summary>
public sealed class PeerIdentityOptions
{
    public static PeerIdentityOptions Default { get; } = new();

    /// <summary>The Game build (version, commit, platform tag...). Peers with different builds do not match.</summary>
    public string Build { get; init; } = "";

    /// <summary>Ticks per second the session runs at (0 = unspecified); recorded and compared.</summary>
    public uint TickRate { get; init; }

    /// <summary>Further options that must be equal on every peer. Names must be unique; order does not matter.</summary>
    public IReadOnlyList<SessionSetting> Settings { get; init; } = [];
}

/// <summary>
/// Build and Plugin identity of a lockstep peer (spec section 7.4): the Framework version and the save, replay
/// and join format versions, the Game build, the Plugin set in load order, the schema hash, the World seed, the
/// tick rate and the Game's session settings. Peers exchange it in wire form (<see cref="Encode"/> /
/// <see cref="Decode"/>) and compare it with <see cref="PeerHandshake.Compare"/>; a <see cref="JoinPackage"/> carries the host's.
/// Compare identities through the handshake, not with <c>==</c>.
/// </summary>
public sealed record PeerIdentity
{
    /// <summary>Version of the identity's wire form.</summary>
    public const int WireVersion = 1;

    /// <summary>The Framework (lockstep package) version of this build; a test keeps it equal to the assembly version.</summary>
    public const string CurrentFrameworkVersion = "0.1.0";

    public string FrameworkVersion { get; init; } = CurrentFrameworkVersion;

    public int SaveFormatVersion { get; init; } = SaveContainer.Version;

    public int ReplayFormatVersion { get; init; } = ReplayLog.Version;

    public int JoinFormatVersion { get; init; } = JoinPackage.Version;

    public string Build { get; init; } = "";

    public ImmutableArray<PeerPlugin> Plugins { get; init; } = [];

    public ulong SchemaHash { get; init; }

    public ulong Seed { get; init; }

    public uint TickRate { get; init; }

    /// <summary>Session settings sorted by ordinal name.</summary>
    public ImmutableArray<SessionSetting> Settings { get; init; } = [];

    /// <summary>False when any Plugin declared itself non-deterministic: lockstep carries no guarantee.</summary>
    public bool IsDeterministic
    {
        get
        {
            foreach (var plugin in Plugins)
            {
                if (!plugin.Deterministic)
                {
                    return false;
                }
            }

            return true;
        }
    }

    /// <summary>The identity of a running World (its schema and seed).</summary>
    public static PeerIdentity Of(World world, PeerIdentityOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(world);
        return Of(world.Schema, world.Seed, options);
    }

    /// <summary>The identity a World of <paramref name="schema"/> with <paramref name="seed"/> would have; usable before any World exists.</summary>
    public static PeerIdentity Of(WorldSchema schema, ulong seed, PeerIdentityOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(schema);
        options ??= PeerIdentityOptions.Default;
        ArgumentNullException.ThrowIfNull(options.Build);
        var plugins = ImmutableArray.CreateBuilder<PeerPlugin>(schema.Plugins.Length);
        foreach (var p in schema.Plugins)
        {
            plugins.Add(new PeerPlugin(p.Id, p.Version, p.Deterministic, p.AbiVersion));
        }

        return new PeerIdentity
        {
            Build = options.Build,
            Plugins = plugins.MoveToImmutable(),
            SchemaHash = schema.SchemaHash,
            Seed = seed,
            TickRate = options.TickRate,
            Settings = SortSettings(options.Settings),
        };
    }

    public byte[] Encode()
    {
        var w = new ByteWriter(256);
        Write(w);
        return w.ToArray();
    }

    /// <summary>Parses an identity's wire form; malformed or truncated bytes throw <see cref="SaveFormatException"/>.</summary>
    public static PeerIdentity Decode(ReadOnlySpan<byte> bytes)
    {
        var r = new ByteReader(bytes, "peer identity");
        var identity = Read(ref r);
        r.ExpectEnd();
        return identity;
    }

    internal void Write(ByteWriter w)
    {
        w.WriteInt32(WireVersion);
        w.WriteString(FrameworkVersion);
        w.WriteInt32(SaveFormatVersion);
        w.WriteInt32(ReplayFormatVersion);
        w.WriteInt32(JoinFormatVersion);
        w.WriteString(Build);
        w.WriteInt32(Plugins.Length);
        foreach (var p in Plugins)
        {
            w.WriteString(p.Id);
            w.WriteString(p.Version);
            w.WriteByte(p.Deterministic ? (byte)1 : (byte)0);
            w.WriteInt32(p.AbiVersion);
        }

        w.WriteUInt64(SchemaHash);
        w.WriteUInt64(Seed);
        w.WriteUInt32(TickRate);
        w.WriteInt32(Settings.Length);
        foreach (var s in Settings)
        {
            w.WriteString(s.Name);
            w.WriteString(s.Value);
        }
    }

    internal static PeerIdentity Read(ref ByteReader r)
    {
        int version = r.ReadInt32();
        if (version != WireVersion)
        {
            throw new SaveFormatException($"Peer identity version {version} is not supported by this build (version {WireVersion}).");
        }

        string framework = r.ReadString();
        int save = r.ReadInt32();
        int replay = r.ReadInt32();
        int join = r.ReadInt32();
        string build = r.ReadString();
        int count = r.ReadCount(13);
        var plugins = ImmutableArray.CreateBuilder<PeerPlugin>(count);
        for (int i = 0; i < count; i++)
        {
            string id = r.ReadString();
            string pluginVersion = r.ReadString();
            bool deterministic = r.ReadByte() != 0;
            plugins.Add(new PeerPlugin(id, pluginVersion, deterministic, r.ReadInt32()));
        }

        ulong schemaHash = r.ReadUInt64();
        ulong seed = r.ReadUInt64();
        uint tickRate = r.ReadUInt32();
        count = r.ReadCount(8);
        var settings = new SessionSetting[count];
        for (int i = 0; i < count; i++)
        {
            string name = r.ReadString();
            settings[i] = new SessionSetting(name, r.ReadString());
        }

        return new PeerIdentity
        {
            FrameworkVersion = framework,
            SaveFormatVersion = save,
            ReplayFormatVersion = replay,
            JoinFormatVersion = join,
            Build = build,
            Plugins = plugins.MoveToImmutable(),
            SchemaHash = schemaHash,
            Seed = seed,
            TickRate = tickRate,
            Settings = SortSettings(settings),
        };
    }

    private static ImmutableArray<SessionSetting> SortSettings(IReadOnlyList<SessionSetting> settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var sorted = settings.ToArray();
        Array.Sort(sorted, static (a, b) => string.CompareOrdinal(a.Name, b.Name));
        for (int i = 0; i < sorted.Length; i++)
        {
            if (sorted[i].Name is null || sorted[i].Value is null)
            {
                throw new ArgumentException("A session setting needs a name and a value.", nameof(settings));
            }

            if (i > 0 && string.Equals(sorted[i - 1].Name, sorted[i].Name, StringComparison.Ordinal))
            {
                throw new ArgumentException($"Session setting '{sorted[i].Name}' is listed twice.", nameof(settings));
            }
        }

        return [.. sorted];
    }
}
