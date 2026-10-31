namespace Colony.Persistence;

/// <summary>Rewrites one Component value from its saved layout (<paramref name="source"/>) into the next layout (<paramref name="destination"/>, zeroed).</summary>
public delegate void ComponentMigrator(ReadOnlySpan<byte> source, Span<byte> destination);

/// <summary>
/// One step of a Component's migration chain (spec section 7.1): from the layout hash a save recorded to the
/// next one. Chains are followed step by step at load time until the schema's current layout is reached.
/// </summary>
public sealed class ComponentMigration
{
    public ComponentMigration(string component, ulong fromLayoutHash, int fromSize, ulong toLayoutHash, int toSize, ComponentMigrator migrate)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(component);
        ArgumentNullException.ThrowIfNull(migrate);
        ArgumentOutOfRangeException.ThrowIfLessThan(fromSize, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(toSize, 1);
        if (fromLayoutHash == toLayoutHash)
        {
            throw new ArgumentException($"Component '{component}': a migration must change the layout hash.", nameof(toLayoutHash));
        }

        Component = component;
        FromLayoutHash = fromLayoutHash;
        FromSize = fromSize;
        ToLayoutHash = toLayoutHash;
        ToSize = toSize;
        Migrate = migrate;
    }

    /// <summary>The Component's stable registered name.</summary>
    public string Component { get; }

    public ulong FromLayoutHash { get; }

    public int FromSize { get; }

    public ulong ToLayoutHash { get; }

    public int ToSize { get; }

    public ComponentMigrator Migrate { get; }
}

/// <summary>
/// The registered N to N+1 Component migrations, keyed by Component name and source layout hash. A load
/// chains them from the saved layout to the schema's; a missing link fails the load with a message naming
/// the Component and both layouts (<see cref="MigrationException"/>).
/// </summary>
public sealed class MigrationRegistry
{
    private readonly Dictionary<(string Component, ulong From), ComponentMigration> _steps = [];

    public int Count => _steps.Count;

    public MigrationRegistry Add(ComponentMigration migration)
    {
        ArgumentNullException.ThrowIfNull(migration);
        var key = (migration.Component, migration.FromLayoutHash);
        if (!_steps.TryAdd(key, migration))
        {
            throw new ArgumentException($"Component '{migration.Component}' already has a migration from layout 0x{migration.FromLayoutHash:X16}.", nameof(migration));
        }

        return this;
    }

    /// <summary>Registers a migration between two typed layouts; sizes are taken from the types.</summary>
    public MigrationRegistry Add<TFrom, TTo>(string component, ulong fromLayoutHash, ulong toLayoutHash, ComponentMigrator migrate)
        where TFrom : unmanaged
        where TTo : unmanaged
        => Add(new ComponentMigration(component, fromLayoutHash, global::System.Runtime.CompilerServices.Unsafe.SizeOf<TFrom>(), toLayoutHash, global::System.Runtime.CompilerServices.Unsafe.SizeOf<TTo>(), migrate));

    /// <summary>The chain from <paramref name="fromLayoutHash"/> to <paramref name="toLayoutHash"/>, or a <see cref="MigrationException"/> naming the gap.</summary>
    internal ComponentMigration[] Resolve(string component, ulong fromLayoutHash, int fromSize, ulong toLayoutHash, int toSize)
    {
        var chain = new List<ComponentMigration>();
        ulong current = fromLayoutHash;
        int size = fromSize;
        while (current != toLayoutHash)
        {
            if (!_steps.TryGetValue((component, current), out var step))
            {
                throw new MigrationException(
                    $"Component '{component}': no migration registered from layout 0x{current:X16} towards the schema's layout 0x{toLayoutHash:X16} (saved layout 0x{fromLayoutHash:X16}, {chain.Count} step(s) applied so far).");
            }

            if (step.FromSize != size)
            {
                throw new MigrationException($"Component '{component}': the migration from layout 0x{current:X16} expects {step.FromSize} byte values but the previous layout is {size} bytes.");
            }

            if (chain.Count >= _steps.Count)
            {
                throw new MigrationException($"Component '{component}': the migrations from layout 0x{fromLayoutHash:X16} form a cycle.");
            }

            chain.Add(step);
            current = step.ToLayoutHash;
            size = step.ToSize;
        }

        if (size != toSize)
        {
            throw new MigrationException($"Component '{component}': the migration chain ends with {size} byte values but the schema's Component is {toSize} bytes.");
        }

        return [.. chain];
    }
}
