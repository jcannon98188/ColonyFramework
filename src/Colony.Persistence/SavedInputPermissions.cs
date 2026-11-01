using System.Collections.Immutable;

namespace Colony.Persistence;

/// <summary>One Input permission rule as saves and replay logs record it: Input Commands by name, so it compares across schemas.</summary>
public readonly record struct SavedInputPermissionRule(InputPermissionScope Scope, uint Source, string? Command, InputAccess Access)
{
    /// <summary>The rule's key as text (<c>source 3</c>, <c>command 'x'</c>, <c>source 1, command 'x'</c>).</summary>
    public string Key => Scope switch
    {
        InputPermissionScope.Source => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"source {Source}"),
        InputPermissionScope.Command => $"command '{Command}'",
        _ => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"source {Source}, command '{Command}'"),
    };
}

/// <summary>
/// The Input Command permission table of a saved World or a replay log (TASK-35): the default access and the rules
/// in the recording schema's canonical order. <see cref="Differences"/> gives the human-readable reasons two tables
/// disagree, for a peer handshake or a replay check (the schema hash already differs; this says why).
/// </summary>
public sealed class SavedInputPermissions
{
    public SavedInputPermissions(InputAccess defaultAccess, ImmutableArray<SavedInputPermissionRule> rules)
    {
        DefaultAccess = defaultAccess;
        Rules = rules.IsDefault ? [] : rules;
    }

    /// <summary>The table of a World built without permission declarations.</summary>
    public static SavedInputPermissions Unrestricted { get; } = new(InputAccess.Allow, []);

    public InputAccess DefaultAccess { get; }

    public ImmutableArray<SavedInputPermissionRule> Rules { get; }

    public bool IsUnrestricted => DefaultAccess == InputAccess.Allow && Rules.IsEmpty;

    /// <summary>The table of <paramref name="schema"/>.</summary>
    public static SavedInputPermissions Of(WorldSchema schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        var permissions = schema.InputPermissions;
        if (permissions.IsUnrestricted)
        {
            return Unrestricted;
        }

        var rules = ImmutableArray.CreateBuilder<SavedInputPermissionRule>(permissions.Rules.Length);
        foreach (var rule in permissions.Rules)
        {
            string? command = rule.Scope == InputPermissionScope.Source ? null : schema.DescribeInputCommand(rule.Command).Name;
            rules.Add(new SavedInputPermissionRule(rule.Scope, rule.Source.Value, command, rule.Access));
        }

        return new SavedInputPermissions(permissions.DefaultAccess, rules.MoveToImmutable());
    }

    /// <summary>The table as text, in <see cref="InputPermissions.Describe()"/> format.</summary>
    public ImmutableArray<string> Describe()
    {
        var lines = ImmutableArray.CreateBuilder<string>(Rules.Length + 1);
        lines.Add("default " + Word(DefaultAccess));
        foreach (var rule in Rules)
        {
            lines.Add(InputPermissions.Describe(rule.Scope, rule.Source, rule.Command, rule.Access));
        }

        return lines.MoveToImmutable();
    }

    /// <summary>
    /// Why this table ("here") and <paramref name="other"/> ("the other side") disagree, one reason per differing key:
    /// the default first, then this table's rules in order, then rules only the other side declares. Empty when equal.
    /// </summary>
    public ImmutableArray<string> Differences(SavedInputPermissions other)
    {
        ArgumentNullException.ThrowIfNull(other);
        var reasons = ImmutableArray.CreateBuilder<string>();
        if (DefaultAccess != other.DefaultAccess)
        {
            reasons.Add($"default input access is {Word(DefaultAccess)} here and {Word(other.DefaultAccess)} on the other side");
        }

        foreach (var rule in Rules)
        {
            var theirs = Find(other.Rules, rule);
            if (theirs is null)
            {
                reasons.Add($"input permission '{rule.Key}' is {Word(rule.Access)} here and not declared on the other side");
            }
            else if (theirs.Value.Access != rule.Access)
            {
                reasons.Add($"input permission '{rule.Key}' is {Word(rule.Access)} here and {Word(theirs.Value.Access)} on the other side");
            }
        }

        foreach (var rule in other.Rules)
        {
            if (Find(Rules, rule) is null)
            {
                reasons.Add($"input permission '{rule.Key}' is not declared here and {Word(rule.Access)} on the other side");
            }
        }

        return reasons.ToImmutable();
    }

    internal void Write(ByteWriter w)
    {
        w.WriteByte((byte)DefaultAccess);
        w.WriteInt32(Rules.Length);
        foreach (var rule in Rules)
        {
            w.WriteByte((byte)rule.Scope);
            w.WriteUInt32(rule.Source);
            w.WriteString(rule.Command ?? "");
            w.WriteByte((byte)rule.Access);
        }
    }

    internal static SavedInputPermissions Read(ref ByteReader r)
    {
        var defaultAccess = ReadAccess(ref r);
        int count = r.ReadCount(10);
        var rules = ImmutableArray.CreateBuilder<SavedInputPermissionRule>(count);
        for (int i = 0; i < count; i++)
        {
            var scope = (InputPermissionScope)r.ReadByte();
            if (scope is not (InputPermissionScope.Source or InputPermissionScope.Command or InputPermissionScope.Pair))
            {
                throw new SaveFormatException($"Input permission rule #{i} has an unknown scope {(byte)scope}.");
            }

            uint source = r.ReadUInt32();
            string command = r.ReadString();
            rules.Add(new SavedInputPermissionRule(scope, source, scope == InputPermissionScope.Source ? null : command, ReadAccess(ref r)));
        }

        return new SavedInputPermissions(defaultAccess, rules.MoveToImmutable());
    }

    private static InputAccess ReadAccess(ref ByteReader r)
    {
        var access = (InputAccess)r.ReadByte();
        return access is InputAccess.Allow or InputAccess.Deny ? access : throw new SaveFormatException($"Unknown input access {(byte)access}.");
    }

    private static SavedInputPermissionRule? Find(ImmutableArray<SavedInputPermissionRule> rules, SavedInputPermissionRule key)
    {
        foreach (var rule in rules)
        {
            if (rule.Scope == key.Scope && rule.Source == key.Source && string.Equals(rule.Command, key.Command, StringComparison.Ordinal))
            {
                return rule;
            }
        }

        return null;
    }

    private static string Word(InputAccess access) => access == InputAccess.Allow ? "allow" : "deny";
}
