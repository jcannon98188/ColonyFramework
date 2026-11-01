namespace Colony.Diagnostics;

/// <summary>
/// Body of an inspector (spec section 10.2): reads <paramref name="world"/> at a Tick boundary and writes
/// named values into <paramref name="output"/>. Plugins register one from <c>Register</c> with
/// <see cref="WorldBuilder.AddInspector"/>; the inspection server lists and runs them for <c>colony-inspect</c>.
/// Never mutate the World from an inspector.
/// </summary>
public delegate void InspectorCallback(World world, InspectorOutput output);

/// <summary>Kind of one <see cref="InspectorEntry"/> value.</summary>
public enum InspectorValueKind : byte
{
    Whole = 1,
    Number = 2,
    Text = 3,
    Entity = 4,
}

/// <summary>One named value an inspector produced.</summary>
public readonly record struct InspectorEntry(string Name, InspectorValueKind Kind, long Whole, double Number, string? Text)
{
    /// <summary>The Entity for <see cref="InspectorValueKind.Entity"/> entries (stored in <see cref="Whole"/>).</summary>
    public EntityId Entity => new((ulong)Whole);
}

/// <summary>Collects the entries of one inspector run. Reusable: <see cref="Clear"/> between runs.</summary>
public sealed class InspectorOutput
{
    private readonly List<InspectorEntry> _entries = [];

    public IReadOnlyList<InspectorEntry> Entries => _entries;

    public void Add(string name, long value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        _entries.Add(new InspectorEntry(name, InspectorValueKind.Whole, value, 0, null));
    }

    public void Add(string name, double value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        _entries.Add(new InspectorEntry(name, InspectorValueKind.Number, 0, value, null));
    }

    public void Add(string name, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(value);
        _entries.Add(new InspectorEntry(name, InspectorValueKind.Text, 0, 0, value));
    }

    public void Add(string name, EntityId value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        _entries.Add(new InspectorEntry(name, InspectorValueKind.Entity, (long)value.Value, 0, null));
    }

    public void Clear() => _entries.Clear();
}

/// <summary>A registered inspector: its dense id, name and body.</summary>
public sealed class InspectorDescriptor
{
    private readonly InspectorCallback _callback;

    internal InspectorDescriptor(int id, string name, InspectorCallback callback)
    {
        Id = id;
        Name = name;
        _callback = callback;
    }

    public int Id { get; }

    public string Name { get; }

    /// <summary>Runs the inspector against <paramref name="world"/>, appending to <paramref name="output"/>. Call it at a Tick boundary.</summary>
    public void Run(World world, InspectorOutput output)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(output);
        _callback(world, output);
    }
}
