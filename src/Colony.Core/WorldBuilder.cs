using System.Runtime.CompilerServices;

namespace Colony;

/// <summary>
/// Composition root for a World (spec section 11): registers Components and validates them, then
/// <see cref="Build"/> freezes everything into an immutable <see cref="WorldSchema"/> that many Worlds
/// can share. Systems, Fields, Plugins and budgets are registered here by later tickets.
/// </summary>
public sealed class WorldBuilder
{
    /// <summary>Seed for Component layout hashes ("LAYT"), so they never collide with a World hash by construction.</summary>
    private const ulong LayoutHashSeed = 0x4C41_5954UL;

    private readonly List<ComponentDescriptor> _components = [];
    private readonly Dictionary<Type, int> _byClrType = [];
    private readonly Dictionary<string, int> _byName = new(StringComparer.Ordinal);
    private readonly List<Phase> _phases = [Phase.Streaming, Phase.Input, Phase.Begin, Phase.Simulate, Phase.Late, Phase.End];
    private readonly List<SystemDefinition> _systems = [];
    private readonly HashSet<string> _systemNames = new(StringComparer.Ordinal);

    /// <summary>
    /// Registers a System (spec section 4.3). Its id is the registration order, which breaks ordering ties.
    /// Access conflicts, ordering cycles and unknown names are reported by <see cref="Build"/>.
    /// </summary>
    public WorldBuilder AddSystem(SystemDefinition system)
    {
        ArgumentNullException.ThrowIfNull(system);
        ThrowIfBuilt();
        ArgumentException.ThrowIfNullOrWhiteSpace(system.Name, nameof(system));
        if (!_systemNames.Add(system.Name))
        {
            throw new ArgumentException($"A System named '{system.Name}' is already registered.", nameof(system));
        }

        if (system.Begin is null && system.ForEachChunk is null && system.End is null)
        {
            throw new ArgumentException($"System '{system.Name}' has no body: set Begin, ForEachChunk or End.", nameof(system));
        }

        _systems.Add(system);
        return this;
    }
    private bool _debugValidation = WorldSchema.DefaultDebugValidation;
    private bool _built;

    /// <summary>
    /// Adds a Game Phase directly after the named Phase <paramref name="after"/> (and after any Game Phase
    /// already added there), so it runs before the next named Phase. Game Phases go only between named
    /// ones (spec section 4.3); nothing can follow <see cref="Phase.End"/>.
    /// </summary>
    public Phase AddPhase(string name, Phase after)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ThrowIfBuilt();
        if (!after.IsNamed)
        {
            throw new ArgumentException($"Game Phases are inserted relative to a named Phase, not to '{after.Name}'.", nameof(after));
        }

        if (after == Phase.End)
        {
            throw new ArgumentException("No Phase can follow End.", nameof(after));
        }

        if (Phase.IsNamedName(name) || _phases.Exists(p => string.Equals(p.Name, name, StringComparison.Ordinal)))
        {
            throw new ArgumentException($"A Phase named '{name}' already exists.", nameof(name));
        }

        var phase = new Phase(_phases.Count, name);
        int index = _phases.IndexOf(after) + 1;
        while (!_phases[index].IsNamed)
        {
            index++;
        }

        _phases.Insert(index, phase);
        return phase;
    }

    /// <summary>
    /// Registers an unmanaged Component under a stable <paramref name="name"/>. The layout hash is
    /// computed from the name, size and alignment; the CLR type is not part of the identity.
    /// </summary>
    public WorldBuilder AddComponent<T>(string name, ComponentOptions flags = ComponentOptions.None) where T : unmanaged
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ThrowIfBuilt();
        if (_byName.ContainsKey(name))
        {
            throw new ArgumentException($"A Component named '{name}' is already registered.", nameof(name));
        }

        if (_byClrType.ContainsKey(typeof(T)))
        {
            throw new ArgumentException($"The type {typeof(T).Name} is already registered as a Component.", nameof(T));
        }

        int size = Unsafe.SizeOf<T>();
        int alignment = Unsafe.SizeOf<AlignmentProbe<T>>() - size;
        if (size > Archetype.MaxComponentSize)
        {
            throw new ArgumentException(
                $"Component '{name}' is {size} bytes; a Component must fit {Archetype.MinCapacity} rows in a {SlabAllocator.ChunkSize} byte chunk ({Archetype.MaxComponentSize} bytes at most).",
                nameof(T));
        }

        int id = _components.Count;
        if (id >= ComponentMask.Capacity)
        {
            throw new InvalidOperationException($"A schema may register at most {ComponentMask.Capacity} Components.");
        }

        // Layout identity (spec section 4.1): the registered name, size and alignment, never the CLR type.
        ulong hash = Hashing.Hash64.Compute(System.Runtime.InteropServices.MemoryMarshal.AsBytes(name.AsSpan()), LayoutHashSeed);
        hash = Hashing.Hash64.Combine(hash, (ulong)size);
        hash = Hashing.Hash64.Combine(hash, (ulong)alignment);
        _components.Add(new ComponentDescriptor(new ComponentType(id), name, size, alignment, hash, flags));
        _byName.Add(name, id);
        _byClrType.Add(typeof(T), id);
        return this;
    }

    /// <summary>The handle for the already-registered Component of CLR type <typeparamref name="T"/>, for use in System definitions before <see cref="Build"/>.</summary>
    public ComponentType Component<T>() where T : unmanaged
    {
        if (!_byClrType.TryGetValue(typeof(T), out int id))
        {
            throw new ArgumentException($"{typeof(T).Name} is not registered as a Component; call AddComponent first.", nameof(T));
        }

        return new ComponentType(id);
    }

    /// <summary>The handle for the already-registered Component named <paramref name="name"/>.</summary>
    public ComponentType Component(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (!_byName.TryGetValue(name, out int id))
        {
            throw new ArgumentException($"No Component named '{name}' is registered; call AddComponent first.", nameof(name));
        }

        return new ComponentType(id);
    }

    /// <summary>
    /// Turns debug validation on or off for Worlds of this schema: structural-version checks on chunk
    /// views and refs, poisoning of freed memory and a leak report on dispose (spec section 4.2). The
    /// default follows the Framework build configuration.
    /// </summary>
    public WorldBuilder DebugValidation(bool enabled)
    {
        ThrowIfBuilt();
        _debugValidation = enabled;
        return this;
    }

    /// <summary>Validates the registrations and freezes them into a schema. The builder cannot be reused.</summary>
    public WorldSchema Build()
    {
        ThrowIfBuilt();
        _built = true;
        var schedule = SystemSchedule.Build(_systems, _phases, _components.Count);
        return new WorldSchema(_components, _byClrType, _byName, _phases, schedule, _debugValidation);
    }

    private void ThrowIfBuilt()
    {
        if (_built)
        {
            throw new InvalidOperationException("The WorldBuilder has already been built; registration is closed.");
        }
    }

    /// <summary>The offset of <c>Value</c> equals the natural alignment of <typeparamref name="T"/>.</summary>
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct AlignmentProbe<T> where T : unmanaged
    {
#pragma warning disable CS0169, CA1823, IDE0051 // Fields are read by the layout only.
        private byte _pad;
        private T _value;
#pragma warning restore CS0169, CA1823, IDE0051
    }
}
