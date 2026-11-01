using System.Collections.Immutable;
using System.Globalization;

namespace Colony;

/// <summary>Whether a source may issue an Input Command (TASK-35).</summary>
public enum InputAccess : byte
{
    Allow = 1,
    Deny = 2,
}

/// <summary>What an <see cref="InputPermissionRule"/> covers.</summary>
public enum InputPermissionScope : byte
{
    /// <summary>Every command from one source (<see cref="WorldBuilder.SetInputAccess(InputSource, InputAccess)"/>).</summary>
    Source = 1,

    /// <summary>One command from every source (<see cref="WorldBuilder.SetInputAccess(InputCommandType, InputAccess)"/>).</summary>
    Command = 2,

    /// <summary>One command from one source (<see cref="WorldBuilder.GrantInput"/> / <see cref="WorldBuilder.DenyInput"/>).</summary>
    Pair = 3,
}

/// <summary>
/// One declared permission. <see cref="Source"/> is unused (default) for <see cref="InputPermissionScope.Command"/>
/// rules and <see cref="Command"/> is unused for <see cref="InputPermissionScope.Source"/> rules.
/// </summary>
public readonly record struct InputPermissionRule(InputPermissionScope Scope, InputSource Source, InputCommandType Command, InputAccess Access);

/// <summary>
/// The Input Command permission table of a schema (TASK-35, spec section 7.3), frozen at <see cref="WorldBuilder.Build"/>.
/// Resolution for (source, command): a <see cref="InputPermissionScope.Pair"/> rule decides when one exists;
/// otherwise, if the source default or the command default is Deny the command is denied, else if either is
/// Allow it is allowed, else <see cref="DefaultAccess"/> applies (Allow unless the schema declared otherwise).
/// Denied commands are rejected when the canonical list is built at the Tick boundary: they stay in the list
/// (and in replay logs and lockstep frames), handlers never see them, and a <see cref="CommandRejected"/> with
/// <see cref="CommandRejected.PermissionDenied"/> is emitted for each. A non-default table is part of
/// <see cref="WorldSchema.SchemaHash"/> (through <see cref="Hash"/>), so peers with different permissions disagree on the schema.
/// </summary>
public sealed class InputPermissions
{
    /// <summary>Seed of <see cref="Hash"/> ("PERM").</summary>
    private const ulong HashSeed = 0x5045_524DUL;

    private readonly InputPermissionRule[] _rules;
    private readonly InputCommandDescriptor[] _commands;
    private readonly byte[] _commandDefaults;
    private readonly uint[] _sources;
    private readonly byte[] _sourceAccess;
    private readonly ulong[] _pairs;
    private readonly byte[] _pairAccess;

    internal InputPermissions(InputAccess defaultAccess, InputPermissionRule[] sortedRules, InputCommandDescriptor[] commands)
    {
        DefaultAccess = defaultAccess;
        _rules = sortedRules;
        _commands = commands;
        _commandDefaults = new byte[commands.Length];
        var sources = new List<uint>();
        var sourceAccess = new List<byte>();
        var pairs = new List<ulong>();
        var pairAccess = new List<byte>();
        foreach (var rule in sortedRules)
        {
            switch (rule.Scope)
            {
                case InputPermissionScope.Source:
                    sources.Add(rule.Source.Value);
                    sourceAccess.Add((byte)rule.Access);
                    break;
                case InputPermissionScope.Command:
                    _commandDefaults[rule.Command.Id] = (byte)rule.Access;
                    break;
                default:
                    pairs.Add(PairKey(rule.Source.Value, rule.Command.Id));
                    pairAccess.Add((byte)rule.Access);
                    break;
            }
        }

        _sources = [.. sources];
        _sourceAccess = [.. sourceAccess];
        _pairs = [.. pairs];
        _pairAccess = [.. pairAccess];
        IsUnrestricted = sortedRules.Length == 0 && defaultAccess == InputAccess.Allow;

        ulong hash = Hashing.Hash64.Compute(ReadOnlySpan<byte>.Empty, HashSeed);
        hash = Hashing.Hash64.Combine(hash, (ulong)defaultAccess);
        foreach (var rule in sortedRules)
        {
            hash = Hashing.Hash64.Combine(hash, (ulong)rule.Scope);
            hash = Hashing.Hash64.Combine(hash, rule.Scope == InputPermissionScope.Command ? 0UL : rule.Source.Value);
            hash = Hashing.Hash64.Combine(hash, rule.Scope == InputPermissionScope.Source ? 0UL : commands[rule.Command.Id].LayoutHash);
            hash = Hashing.Hash64.Combine(hash, (ulong)rule.Access);
        }

        Hash = hash;
    }

    /// <summary>What applies when no rule covers a (source, command); Allow unless declared with <see cref="WorldBuilder.DefaultInputAccess"/>.</summary>
    public InputAccess DefaultAccess { get; }

    /// <summary>True when nothing restricts any source: no rules and an Allow default. Such a table is not part of the schema hash.</summary>
    public bool IsUnrestricted { get; }

    /// <summary>
    /// Hash of the table: the default, then every rule in <see cref="Rules"/> order (scope, source, the command's
    /// layout hash, access). Declaration order does not matter. Folded into the schema hash unless <see cref="IsUnrestricted"/>.
    /// </summary>
    public ulong Hash { get; }

    /// <summary>Every declared rule, ordered by scope (Source, Command, Pair), then source, then command id.</summary>
    public ReadOnlySpan<InputPermissionRule> Rules => _rules;

    /// <summary>The access <paramref name="source"/> has to <paramref name="command"/> (see the type remarks for the precedence).</summary>
    public InputAccess Access(InputSource source, InputCommandType command)
    {
        if ((uint)command.Id >= (uint)_commands.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(command), command.Id, "The schema has no such Input Command.");
        }

        return Resolve(source.Value, command.Id);
    }

    public bool IsAllowed(InputSource source, InputCommandType command) => Access(source, command) == InputAccess.Allow;

    /// <summary>Allocation-free resolution used at the Tick boundary.</summary>
    internal InputAccess Resolve(uint source, int command)
    {
        if (IsUnrestricted)
        {
            return InputAccess.Allow;
        }

        int pair = Array.BinarySearch(_pairs, PairKey(source, command));
        if (pair >= 0)
        {
            return (InputAccess)_pairAccess[pair];
        }

        int s = Array.BinarySearch(_sources, source);
        byte bySource = s >= 0 ? _sourceAccess[s] : (byte)0;
        byte byCommand = _commandDefaults[command];
        if (bySource == (byte)InputAccess.Deny || byCommand == (byte)InputAccess.Deny)
        {
            return InputAccess.Deny;
        }

        return bySource != 0 || byCommand != 0 ? InputAccess.Allow : DefaultAccess;
    }

    /// <summary>The table as text, one line per entry, default first: <c>default allow</c>, <c>source 3: deny</c>, <c>command 'x': deny</c>, <c>source 1, command 'x': allow</c>.</summary>
    public ImmutableArray<string> Describe()
    {
        var lines = ImmutableArray.CreateBuilder<string>(_rules.Length + 1);
        lines.Add("default " + Word(DefaultAccess));
        foreach (var rule in _rules)
        {
            lines.Add(Describe(rule.Scope, rule.Source.Value, rule.Scope == InputPermissionScope.Source ? null : _commands[rule.Command.Id].Name, rule.Access));
        }

        return lines.MoveToImmutable();
    }

    /// <summary>One rule as text in the <see cref="Describe()"/> format; <paramref name="command"/> is null for a source rule.</summary>
    public static string Describe(InputPermissionScope scope, uint source, string? command, InputAccess access) => scope switch
    {
        InputPermissionScope.Source => string.Create(CultureInfo.InvariantCulture, $"source {source}: {Word(access)}"),
        InputPermissionScope.Command => string.Create(CultureInfo.InvariantCulture, $"command '{command}': {Word(access)}"),
        _ => string.Create(CultureInfo.InvariantCulture, $"source {source}, command '{command}': {Word(access)}"),
    };

    private static string Word(InputAccess access) => access == InputAccess.Allow ? "allow" : "deny";

    private static ulong PairKey(uint source, int command) => ((ulong)source << 32) | (uint)command;
}

/// <summary>Collects permission declarations on a <see cref="WorldBuilder"/>; a key may be declared once (repeating the same access is a no-op).</summary>
internal sealed class InputPermissionsBuilder
{
    private readonly List<(InputPermissionRule Rule, string Owner)> _rules = [];
    private InputAccess? _default;
    private string _defaultOwner = "";

    public void SetDefault(InputAccess access, string owner)
    {
        Validate(access);
        if (_default is { } existing && existing != access)
        {
            throw new ArgumentException($"The default Input access is already {existing} (declared by {_defaultOwner}); {owner} cannot change it to {access}.", nameof(access));
        }

        _default = access;
        _defaultOwner = owner;
    }

    public void Declare(InputPermissionRule rule, string owner, Func<int, string> commandName)
    {
        Validate(rule.Access);
        for (int i = 0; i < _rules.Count; i++)
        {
            var (existing, existingOwner) = _rules[i];
            if (existing.Scope == rule.Scope && existing.Source == rule.Source && existing.Command == rule.Command)
            {
                if (existing.Access != rule.Access)
                {
                    string what = InputPermissions.Describe(rule.Scope, rule.Source.Value, rule.Scope == InputPermissionScope.Source ? null : commandName(rule.Command.Id), existing.Access);
                    throw new ArgumentException($"Input permission '{what}' was declared by {existingOwner}; {owner} cannot declare {rule.Access} for the same key.", nameof(rule));
                }

                return;
            }
        }

        _rules.Add((rule, owner));
    }

    public InputPermissions Build(List<InputCommandDescriptor> commands)
    {
        var rules = new InputPermissionRule[_rules.Count];
        for (int i = 0; i < rules.Length; i++)
        {
            rules[i] = _rules[i].Rule;
        }

        Array.Sort(rules, static (a, b) =>
        {
            int c = a.Scope.CompareTo(b.Scope);
            if (c != 0)
            {
                return c;
            }

            c = a.Source.Value.CompareTo(b.Source.Value);
            return c != 0 ? c : a.Command.Id.CompareTo(b.Command.Id);
        });
        return new InputPermissions(_default ?? InputAccess.Allow, rules, [.. commands]);
    }

    private static void Validate(InputAccess access)
    {
        if (access is not (InputAccess.Allow or InputAccess.Deny))
        {
            throw new ArgumentOutOfRangeException(nameof(access), access, "Input access must be Allow or Deny.");
        }
    }
}
