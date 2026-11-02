namespace Colony.Persistence;

/// <summary>Per-section compression of the save container (spec section 7.1).</summary>
public enum SaveCompression
{
    /// <summary>Sections are stored as written.</summary>
    None = 0,

    /// <summary>Sections at or above <see cref="SaveOptions.CompressionThreshold"/> bytes are LZ4 block compressed when that makes them smaller.</summary>
    Lz4 = 1,
}

/// <summary>Options of <c>world.Save(...)</c>. The defaults compress and keep two backup generations.</summary>
public sealed class SaveOptions
{
    public static SaveOptions Default { get; } = new();

    public SaveCompression Compression { get; init; } = SaveCompression.Lz4;

    /// <summary>Sections smaller than this many bytes are never compressed.</summary>
    public int CompressionThreshold { get; init; } = 64;

    /// <summary>
    /// How many previous saves to keep beside the file (<c>name.bak1</c> is the newest). The new save is written
    /// to a temporary file, flushed to disk and renamed into place, so an interrupted save never damages the old one.
    /// </summary>
    public int BackupGenerations { get; init; } = 2;
}

/// <summary>Options of <c>World.Load(...)</c>.</summary>
public sealed class LoadOptions
{
    public static LoadOptions Default { get; } = new();

    /// <summary>Per-Component migrations applied when a saved layout hash differs from the schema's.</summary>
    public MigrationRegistry Migrations { get; init; } = new();

    /// <summary>
    /// Recompute the World hash after loading and compare it with the saved one (on by default). Only possible
    /// when the saved schema hash equals the schema's: a migrated save hashes differently by definition.
    /// </summary>
    public bool VerifyWorldHash { get; init; } = true;

    /// <summary>Host settings of the new World (threads, diagnostics, gate). The seed and the Entity capacity come from the save.</summary>
    public WorldOptions WorldOptions { get; init; } = WorldOptions.Default;

    /// <summary>
    /// Load a save made with Plugins the schema does not have (spec section 8): Components and persistent
    /// Resources the save records and the schema lacks are dropped and the rest loads. Off by default, when such a
    /// save fails with <see cref="MissingPluginException"/>. Dropping changes the schema, so the saved World hash is
    /// not verified.
    /// </summary>
    public bool DropUnknownComponents { get; init; }

    /// <summary>
    /// Runs once on the loaded World after every section and extension is restored and before
    /// <see cref="VerifyWorldHash"/> checks the hash (whether or not it does), then Load returns the World. Use it to
    /// supply host-owned state the save does not hold but the hash depends on, such as a host-set
    /// <c>FieldSet.ChunkSource</c>: never-generated Unloaded chunks are not saved and hash through the source.
    /// If it throws, the World is disposed and the exception propagates.
    /// </summary>
    public Action<World>? PrepareWorld { get; init; }
}
