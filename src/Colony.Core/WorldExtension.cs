using Colony.Hashing;

namespace Colony;

/// <summary>
/// Schema-level registration of a World extension: state that lives beside the Entity storage (Field
/// chunks, a spatial index, pathfinding caches) and takes part in the Tick and the World hash without Core
/// knowing its shape. Registered with <see cref="WorldBuilder.AddExtension"/>; <see cref="Freeze"/> runs at
/// <see cref="WorldBuilder.Build"/>, after which <see cref="LayoutHash"/> is folded into the schema hash
/// and <see cref="Create"/> is called once per World.
/// </summary>
public abstract class WorldExtensionDefinition
{
    /// <summary>Stable name; unique within a schema.</summary>
    public abstract string Name { get; }

    /// <summary>Identity of the registered layout (names, cell layouts, dimensions). Part of the schema hash, so read after <see cref="Freeze"/>.</summary>
    public abstract ulong LayoutHash { get; }

    /// <summary>
    /// A content version the save manifest records beside <see cref="LayoutHash"/> (for example the generation
    /// version, spec section 9.3). Unlike the layout hash it is neither part of the schema hash nor compared on
    /// load: the extension decides what a mismatch means. Null (the default) records nothing.
    /// </summary>
    public virtual string? Version => null;

    /// <summary>Closes registration; called once from <see cref="WorldBuilder.Build"/> before <see cref="LayoutHash"/> is read.</summary>
    public virtual void Freeze()
    {
    }

    /// <summary>Creates the per-World state. Called from the World constructor; the World's Entity storage is ready but empty.</summary>
    public abstract IWorldExtension Create(World world);
}

/// <summary>
/// Per-World state of a <see cref="WorldExtensionDefinition"/>. All callbacks run serially on the Tick thread;
/// extensions are called in registration order. Fetch it with <see cref="World.Extension{T}"/>.
/// </summary>
public interface IWorldExtension : IDisposable
{
    /// <summary>Start of a Tick, before the first Phase.</summary>
    public void BeginTick(long tick);

    /// <summary>A Phase finished and its command buffers were played back.</summary>
    public void EndPhase(Phase phase);

    /// <summary>End of a Tick, after the last Phase and before the scratch arena resets.</summary>
    public void EndTick(long tick);

    /// <summary>
    /// Appends this extension's simulated state to the World hash (spec section 6): canonical order, exact
    /// bytes, nothing derived or cached. Called after the Entity storage and before the RNG state.
    /// </summary>
    public void Hash(ref Hash64State state);

    /// <summary>
    /// Adds this extension's state to the hierarchical World hash (<see cref="World.HashTree"/>, spec section
    /// 10.2) as items, and chunk leaves where the state is chunked, under a group the World already opened with
    /// the extension's name. Must cover exactly the bytes <see cref="Hash"/> covers, split the same way on every
    /// peer. The default adds one leafless item <c>state</c> holding the digest of <see cref="Hash"/>.
    /// </summary>
    public void HashTree(HashTreeBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var state = new Hash64State(WorldHashTree.NodeSeed);
        Hash(ref state);
        builder.AddItem("state", state.Finish());
    }

    /// <summary>
    /// Writes this extension's saved state as named sections (spec section 7.1): exactly the state
    /// <see cref="Hash"/> covers, in a layout the extension owns (Field chunks write one section per chunk).
    /// Derived state (a spatial index, caches) writes nothing and rebuilds in <see cref="Load"/>. Called at
    /// a Tick boundary on the Tick thread; the default saves nothing.
    /// </summary>
    public void Save(ExtensionStateWriter writer)
    {
    }

    /// <summary>
    /// Restores the state written by <see cref="Save"/> into a freshly created World whose Entity storage,
    /// Resources and tables are already loaded; extensions load in registration order. Throw a clear error
    /// when a section is missing or malformed. The default restores nothing.
    /// </summary>
    public void Load(ExtensionStateReader reader)
    {
    }
}

/// <summary>Where an extension writes its saved sections (<see cref="IWorldExtension.Save"/>); the save container implements it.</summary>
public abstract class ExtensionStateWriter
{
    /// <summary>Adds one section; names are unique per extension and bytes are copied before the call returns.</summary>
    public abstract void WriteSection(string name, ReadOnlySpan<byte> payload);
}

/// <summary>The saved sections of one extension (<see cref="IWorldExtension.Load"/>), verified against their hashes before they are handed over.</summary>
public abstract class ExtensionStateReader
{
    /// <summary>The name of the extension whose sections these are, for error messages.</summary>
    public abstract string ExtensionName { get; }

    public abstract int SectionCount { get; }

    public abstract string SectionName(int index);

    /// <summary>The payload of a section, or false when the save has none by that name.</summary>
    public abstract bool TryGetSection(string name, out ReadOnlySpan<byte> payload);

    /// <summary>The payload of a section; throws a clear error naming the extension and section when it is missing.</summary>
    public ReadOnlySpan<byte> Section(string name)
    {
        if (!TryGetSection(name, out var payload))
        {
            throw new InvalidDataException($"The save has no section '{name}' for extension '{ExtensionName}'.");
        }

        return payload;
    }
}

/// <summary>
/// An index-keyed parallel job for <see cref="World.RunParallel"/>. <see cref="Execute"/> is called exactly
/// once per index, concurrently from any worker; results must depend on the index alone, never on the thread.
/// </summary>
public interface IIndexedJob
{
    public void Execute(int index);
}
