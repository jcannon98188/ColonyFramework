namespace Colony.ProcGen;

/// <summary>
/// One named stage of the generation pipeline (spec section 9.3). A stage declares the Fields it reads, writes
/// and reads through a halo, and its order relative to other stages by name; <see cref="Generate"/> then fills
/// one chunk through the <see cref="ChunkGenerationContext"/>, which enforces the declarations. A stage must be
/// a pure function of the context: no instance state written during <see cref="Generate"/> (it runs concurrently
/// for different chunks), no clocks, no <see cref="System.Random"/>. Its randomness comes from
/// <see cref="ChunkGenerationContext.Key"/> (world-space noise and region-keyed placement) and
/// <see cref="ChunkGenerationContext.Random"/> (chunk-keyed draws), both keyed by the stage name, so adding,
/// removing or reordering other stages never shifts its numbers.
/// </summary>
public abstract class GenerationStage
{
    /// <summary>Stable name: it keys the stage's randomness and placement ids, and orders other stages (<see cref="After"/>, <see cref="Before"/>).</summary>
    public abstract string Name { get; }

    /// <summary>Fields read inside the chunk. Some earlier stage must write each of them.</summary>
    public virtual IReadOnlyList<string> Reads => [];

    /// <summary>Fields written. Two stages writing one Field, or one writing what another reads, must be ordered by name.</summary>
    public virtual IReadOnlyList<string> Writes => [];

    /// <summary>
    /// Fields read across the chunk edge (<see cref="ChunkGenerationContext.Halo{T}"/>). Halo reads see the
    /// neighbours' base data only: every stage writing such a Field must run before this one and must itself
    /// depend on no halo read, so a neighbour's base data is generated from its own coordinate alone.
    /// </summary>
    public virtual IReadOnlyList<string> HaloReads => [];

    /// <summary>Names of stages this one runs after (stages that are not registered fail the pipeline build).</summary>
    public virtual IReadOnlyList<string> After => [];

    /// <summary>Names of stages this one runs before.</summary>
    public virtual IReadOnlyList<string> Before => [];

    /// <summary>
    /// True when the stage's output is deliberately keyed by its own chunk (for example chunk-keyed point
    /// placement that never crosses the chunk edge). The seam check skips such stages; everything else must be
    /// a function of world position and produce identical cells across chunk boundaries.
    /// </summary>
    public virtual bool ChunkLocal => false;

    /// <summary>Generates this stage's part of one chunk.</summary>
    public abstract void Generate(ref ChunkGenerationContext context);
}

/// <summary>
/// A world-level pre-pass (spec section 9.3): a deterministic global solve (a climate, a drainage graph summary)
/// computed once per (pipeline, seed), cached by the <see cref="ChunkProvider"/> and stored in a generated
/// Resource of every World. Stages read it with <see cref="ChunkGenerationContext.PrePass{T}"/>.
/// </summary>
public abstract class GenerationPrePass
{
    private protected GenerationPrePass()
    {
    }

    /// <summary>Stable name; keys the pre-pass randomness and names its Resource (<see cref="GenerationBuilder.PrePassResourcePrefix"/>).</summary>
    public abstract string Name { get; }

    internal abstract Type ValueType { get; }

    internal abstract int ValueSize { get; }

    internal abstract void ComputeInto(in PrePassContext context, Span<byte> destination);

    internal abstract void RegisterResource(WorldBuilder builder, string name);

    internal abstract void StoreResource(World world, ResourceType type, ReadOnlySpan<byte> value);
}

/// <summary>A pre-pass whose result is one unmanaged value <typeparamref name="T"/>.</summary>
public abstract class GenerationPrePass<T> : GenerationPrePass
    where T : unmanaged
{
    /// <summary>Computes the value from the context alone (seed, World size, earlier pre-passes).</summary>
    public abstract T Compute(in PrePassContext context);

    internal override Type ValueType => typeof(T);

    internal override int ValueSize => System.Runtime.CompilerServices.Unsafe.SizeOf<T>();

    internal override void ComputeInto(in PrePassContext context, Span<byte> destination)
    {
        T value = Compute(in context);
        System.Runtime.InteropServices.MemoryMarshal.Write(destination, in value);
    }

    internal override void RegisterResource(WorldBuilder builder, string name) => builder.AddResource<T>(name, ResourceOptions.Transient);

    internal override void StoreResource(World world, ResourceType type, ReadOnlySpan<byte> value)
        => world.Resource<T>(type) = System.Runtime.InteropServices.MemoryMarshal.Read<T>(value);
}
