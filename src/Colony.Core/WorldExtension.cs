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
}

/// <summary>
/// An index-keyed parallel job for <see cref="World.RunParallel"/>. <see cref="Execute"/> is called exactly
/// once per index, concurrently from any worker; results must depend on the index alone, never on the thread.
/// </summary>
public interface IIndexedJob
{
    public void Execute(int index);
}
