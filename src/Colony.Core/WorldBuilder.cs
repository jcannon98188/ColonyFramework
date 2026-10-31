using System.Runtime.CompilerServices;

namespace Colony;

/// <summary>
/// Composition root for a World (spec section 11): registers Components and validates them, then
/// <see cref="Build"/> freezes everything into an immutable <see cref="WorldSchema"/> that many Worlds
/// can share. Systems, Fields, Plugins and budgets are registered here by later tickets.
/// </summary>
public sealed class WorldBuilder
{
    private readonly List<ComponentDescriptor> _components = [];
    private readonly Dictionary<Type, int> _byClrType = [];
    private readonly Dictionary<string, int> _byName = new(StringComparer.Ordinal);
    private bool _debugValidation = WorldSchema.DefaultDebugValidation;
    private bool _built;

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

        ulong hash = Fnv1a.Hash(name);
        hash = Fnv1a.Hash((ulong)size, hash);
        hash = Fnv1a.Hash((ulong)alignment, hash);
        _components.Add(new ComponentDescriptor(new ComponentType(id), name, size, alignment, hash, flags));
        _byName.Add(name, id);
        _byClrType.Add(typeof(T), id);
        return this;
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
        return new WorldSchema(_components, _byClrType, _byName, _debugValidation);
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
