using System.Collections.Immutable;
using System.Globalization;
using System.Text;

namespace Colony.Persistence;

/// <summary>Why two peers cannot run one lockstep session (spec section 7.4).</summary>
public enum HandshakeMismatchKind
{
    /// <summary>A save, replay or join format version differs (<see cref="HandshakeMismatch.Subject"/> names which).</summary>
    FormatVersion,

    /// <summary>The Framework version differs.</summary>
    FrameworkVersion,

    /// <summary>The Game build differs.</summary>
    Build,

    /// <summary>One peer has a Plugin the other lacks (the lacking side's value is null).</summary>
    MissingPlugin,

    PluginVersion,

    /// <summary>A Plugin declares itself deterministic on one peer and not on the other.</summary>
    PluginDeterminism,

    /// <summary>A native Plugin was built against different C ABI versions.</summary>
    PluginAbiVersion,

    /// <summary>Both peers have the same Plugins, loaded in a different order.</summary>
    PluginOrder,

    /// <summary>A peer runs a non-deterministic Plugin, so lockstep carries no guarantee (Subject is "local" or "remote").</summary>
    NotDeterministic,

    /// <summary>The schema hashes differ (Components, Resources, extensions, Input Commands).</summary>
    SchemaHash,

    Seed,

    TickRate,

    /// <summary>A session setting only one peer has (the lacking side's value is null).</summary>
    MissingSetting,

    /// <summary>A session setting both peers have, with different values.</summary>
    SettingValue,
}

/// <summary>One specific handshake failure: what differs (<see cref="Subject"/>) and each side's value (null = absent).</summary>
public readonly record struct HandshakeMismatch(HandshakeMismatchKind Kind, string Subject, string? Local, string? Remote)
{
    /// <summary>A sentence naming the difference, for logs and the player.</summary>
    public string Message => Kind switch
    {
        HandshakeMismatchKind.FormatVersion => $"Different {Subject} format: {Sides()}.",
        HandshakeMismatchKind.FrameworkVersion => $"Different Framework version: {Sides()}.",
        HandshakeMismatchKind.Build => $"Different build: {Sides()}.",
        HandshakeMismatchKind.MissingPlugin => $"Missing Plugin '{Subject}': {Presence()}.",
        HandshakeMismatchKind.PluginVersion => $"Different version of Plugin '{Subject}': {Sides()}.",
        HandshakeMismatchKind.PluginDeterminism => $"Plugin '{Subject}' is declared differently: {Sides()}.",
        HandshakeMismatchKind.PluginAbiVersion => $"Plugin '{Subject}' targets different C ABI versions: {Sides()}.",
        HandshakeMismatchKind.PluginOrder => $"Different Plugin load order: {Sides()}.",
        HandshakeMismatchKind.NotDeterministic => $"The {Subject} peer runs a non-deterministic Plugin ({(Subject == "local" ? Local : Remote)}); lockstep is not guaranteed.",
        HandshakeMismatchKind.SchemaHash => $"Different schema: {Sides()}.",
        HandshakeMismatchKind.Seed => $"Different seed: {Sides()}.",
        HandshakeMismatchKind.TickRate => $"Different tick rate: {Sides()}.",
        HandshakeMismatchKind.MissingSetting => $"Missing session setting '{Subject}': {Presence()}.",
        HandshakeMismatchKind.SettingValue => $"Different session setting '{Subject}': {Sides()}.",
        _ => $"{Kind} '{Subject}': {Sides()}.",
    };

    private string Sides() => $"the local peer has {Local}, the remote peer has {Remote}";

    private string Presence() => Local is null
        ? $"the local peer does not have it, the remote peer has {Remote}"
        : $"the local peer has {Local}, the remote peer does not have it";
}

/// <summary>The outcome of <see cref="PeerHandshake.Compare"/>: match, or every specific mismatch in a fixed order.</summary>
public sealed class HandshakeResult
{
    internal HandshakeResult(ImmutableArray<HandshakeMismatch> mismatches)
    {
        Mismatches = mismatches;
    }

    public bool IsMatch => Mismatches.IsEmpty;

    /// <summary>Formats, Framework, build, Plugins (in the local load order, then remote-only ones), determinism, schema, seed, tick rate, settings (by name).</summary>
    public ImmutableArray<HandshakeMismatch> Mismatches { get; }

    public bool Has(HandshakeMismatchKind kind)
    {
        foreach (var m in Mismatches)
        {
            if (m.Kind == kind)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>One line per mismatch, or "The peers match.".</summary>
    public string Describe()
    {
        if (IsMatch)
        {
            return "The peers match.";
        }

        var text = new StringBuilder();
        foreach (var m in Mismatches)
        {
            if (text.Length > 0)
            {
                text.Append('\n');
            }

            text.Append(m.Message);
        }

        return text.ToString();
    }
}

/// <summary>
/// The peer matching check of spec section 7.4: compares two <see cref="PeerIdentity"/> values field by field
/// and reports each difference specifically (missing Plugin X, different seed...). Pure and symmetric up to
/// swapping Local and Remote; run it on both peers.
/// </summary>
public static class PeerHandshake
{
    public static HandshakeResult Compare(PeerIdentity local, PeerIdentity remote)
    {
        ArgumentNullException.ThrowIfNull(local);
        ArgumentNullException.ThrowIfNull(remote);
        var m = ImmutableArray.CreateBuilder<HandshakeMismatch>();
        Version(m, "save container", local.SaveFormatVersion, remote.SaveFormatVersion);
        Version(m, "replay log", local.ReplayFormatVersion, remote.ReplayFormatVersion);
        Version(m, "join package", local.JoinFormatVersion, remote.JoinFormatVersion);
        Text(m, HandshakeMismatchKind.FrameworkVersion, "framework", local.FrameworkVersion, remote.FrameworkVersion);
        Text(m, HandshakeMismatchKind.Build, "build", local.Build, remote.Build);
        ComparePlugins(m, local.Plugins, remote.Plugins);
        if (!local.IsDeterministic)
        {
            m.Add(new HandshakeMismatch(HandshakeMismatchKind.NotDeterministic, "local", NonDeterministic(local.Plugins), null));
        }

        if (!remote.IsDeterministic)
        {
            m.Add(new HandshakeMismatch(HandshakeMismatchKind.NotDeterministic, "remote", null, NonDeterministic(remote.Plugins)));
        }

        if (local.SchemaHash != remote.SchemaHash)
        {
            m.Add(new HandshakeMismatch(HandshakeMismatchKind.SchemaHash, "schema", Hex(local.SchemaHash), Hex(remote.SchemaHash)));
        }

        if (local.Seed != remote.Seed)
        {
            m.Add(new HandshakeMismatch(HandshakeMismatchKind.Seed, "seed", Hex(local.Seed), Hex(remote.Seed)));
        }

        if (local.TickRate != remote.TickRate)
        {
            m.Add(new HandshakeMismatch(HandshakeMismatchKind.TickRate, "tick rate", local.TickRate.ToString(CultureInfo.InvariantCulture), remote.TickRate.ToString(CultureInfo.InvariantCulture)));
        }

        CompareSettings(m, local.Settings, remote.Settings);
        return new HandshakeResult(m.ToImmutable());
    }

    private static void ComparePlugins(ImmutableArray<HandshakeMismatch>.Builder m, ImmutableArray<PeerPlugin> local, ImmutableArray<PeerPlugin> remote)
    {
        bool sameSet = local.Length == remote.Length;
        foreach (var l in local)
        {
            int index = IndexOf(remote, l.Id);
            if (index < 0)
            {
                sameSet = false;
                m.Add(new HandshakeMismatch(HandshakeMismatchKind.MissingPlugin, l.Id, l.Version, null));
                continue;
            }

            var r = remote[index];
            Text(m, HandshakeMismatchKind.PluginVersion, l.Id, l.Version, r.Version);
            if (l.Deterministic != r.Deterministic)
            {
                m.Add(new HandshakeMismatch(HandshakeMismatchKind.PluginDeterminism, l.Id, Determinism(l.Deterministic), Determinism(r.Deterministic)));
            }

            if (l.AbiVersion != r.AbiVersion)
            {
                m.Add(new HandshakeMismatch(HandshakeMismatchKind.PluginAbiVersion, l.Id, l.AbiVersion.ToString(CultureInfo.InvariantCulture), r.AbiVersion.ToString(CultureInfo.InvariantCulture)));
            }
        }

        foreach (var r in remote)
        {
            if (IndexOf(local, r.Id) < 0)
            {
                sameSet = false;
                m.Add(new HandshakeMismatch(HandshakeMismatchKind.MissingPlugin, r.Id, null, r.Version));
            }
        }

        if (sameSet)
        {
            for (int i = 0; i < local.Length; i++)
            {
                if (!string.Equals(local[i].Id, remote[i].Id, StringComparison.Ordinal))
                {
                    m.Add(new HandshakeMismatch(HandshakeMismatchKind.PluginOrder, "load order", Order(local), Order(remote)));
                    break;
                }
            }
        }
    }

    private static void CompareSettings(ImmutableArray<HandshakeMismatch>.Builder m, ImmutableArray<SessionSetting> local, ImmutableArray<SessionSetting> remote)
    {
        // Both lists are sorted by ordinal name: merge them.
        int i = 0;
        int j = 0;
        while (i < local.Length || j < remote.Length)
        {
            int c = i == local.Length ? 1 : j == remote.Length ? -1 : string.CompareOrdinal(local[i].Name, remote[j].Name);
            if (c < 0)
            {
                m.Add(new HandshakeMismatch(HandshakeMismatchKind.MissingSetting, local[i].Name, local[i].Value, null));
                i++;
            }
            else if (c > 0)
            {
                m.Add(new HandshakeMismatch(HandshakeMismatchKind.MissingSetting, remote[j].Name, null, remote[j].Value));
                j++;
            }
            else
            {
                Text(m, HandshakeMismatchKind.SettingValue, local[i].Name, local[i].Value, remote[j].Value);
                i++;
                j++;
            }
        }
    }

    private static void Version(ImmutableArray<HandshakeMismatch>.Builder m, string subject, int local, int remote)
    {
        if (local != remote)
        {
            m.Add(new HandshakeMismatch(HandshakeMismatchKind.FormatVersion, subject, local.ToString(CultureInfo.InvariantCulture), remote.ToString(CultureInfo.InvariantCulture)));
        }
    }

    private static void Text(ImmutableArray<HandshakeMismatch>.Builder m, HandshakeMismatchKind kind, string subject, string local, string remote)
    {
        if (!string.Equals(local, remote, StringComparison.Ordinal))
        {
            m.Add(new HandshakeMismatch(kind, subject, local, remote));
        }
    }

    private static int IndexOf(ImmutableArray<PeerPlugin> plugins, string id)
    {
        for (int i = 0; i < plugins.Length; i++)
        {
            if (string.Equals(plugins[i].Id, id, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    private static string NonDeterministic(ImmutableArray<PeerPlugin> plugins)
        => string.Join(", ", plugins.Where(static p => !p.Deterministic).Select(static p => p.Id));

    private static string Order(ImmutableArray<PeerPlugin> plugins) => string.Join(", ", plugins.Select(static p => p.Id));

    private static string Determinism(bool deterministic) => deterministic ? "deterministic" : "non-deterministic";

    private static string Hex(ulong value) => "0x" + value.ToString("X16", CultureInfo.InvariantCulture);
}
