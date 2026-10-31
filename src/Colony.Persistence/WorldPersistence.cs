namespace Colony.Persistence;

/// <summary>
/// Save and load of Worlds (spec section 7.1): <c>world.Save(path)</c> writes the sectioned container
/// atomically; <see cref="Load(string, WorldSchema, LoadOptions?)"/> builds a fresh World from it. A save
/// happens at a Tick boundary on the Tick thread; a load produces a World that hashes like the saved one and
/// continues identically. <see cref="SaveInBackground"/> snapshots synchronously and writes on a worker.
/// </summary>
public static class WorldPersistence
{
    /// <summary>Copies the World's saved state out at this Tick boundary; encode or write it any time later.</summary>
    public static WorldSnapshot Snapshot(this World world)
    {
        ArgumentNullException.ThrowIfNull(world);
        return WorldSnapshot.Capture(world);
    }

    /// <summary>Saves the World to <paramref name="path"/>: temp file, flush, backup rotation, rename.</summary>
    public static void Save(this World world, string path, SaveOptions? options = null)
        => Snapshot(world).WriteFile(path, options);

    public static void Save(this World world, Stream destination, SaveOptions? options = null)
        => Snapshot(world).Write(destination, options);

    /// <summary>The container image a synchronous save would write.</summary>
    public static byte[] SaveToBytes(this World world, SaveOptions? options = null)
        => Snapshot(world).Encode(options);

    /// <summary>
    /// Snapshots the World now (on the calling thread, between Ticks) and writes the container on a worker
    /// thread while the host keeps simulating. The file is byte-identical to <see cref="Save(World, string, SaveOptions?)"/>
    /// at the same Tick.
    /// </summary>
    public static Task SaveInBackground(this World world, string path, SaveOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var snapshot = Snapshot(world);
        return Task.Factory.StartNew(() => snapshot.WriteFile(path, options), CancellationToken.None, TaskCreationOptions.DenyChildAttach, TaskScheduler.Default);
    }

    /// <summary>Builds a fresh World of <paramref name="schema"/> from the save at <paramref name="path"/>.</summary>
    public static World Load(string path, WorldSchema schema, LoadOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Load(File.ReadAllBytes(path), schema, options);
    }

    public static World Load(Stream source, WorldSchema schema, LoadOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        using var memory = new MemoryStream();
        source.CopyTo(memory);
        return Load(memory.ToArray(), schema, options);
    }

    public static World Load(ReadOnlyMemory<byte> image, WorldSchema schema, LoadOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(schema);
        var reader = new SaveContainer.Reader(image);
        return WorldLoader.Load(reader, schema, options ?? LoadOptions.Default);
    }

    /// <summary>Reads only the manifest (schema table, Tick, World hash, seed) of a save.</summary>
    public static SaveManifest ReadManifest(ReadOnlyMemory<byte> image) => new SaveContainer.Reader(image).Manifest;

    public static SaveManifest ReadManifest(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return ReadManifest(File.ReadAllBytes(path));
    }

    /// <summary>The section names of a save in container order, for tooling and tests.</summary>
    public static IReadOnlyList<string> SectionNames(ReadOnlyMemory<byte> image)
    {
        var reader = new SaveContainer.Reader(image);
        var names = new string[reader.SectionCount];
        for (int i = 0; i < names.Length; i++)
        {
            names[i] = reader.SectionName(i);
        }

        return names;
    }

    /// <summary>True when the named section is stored compressed.</summary>
    public static bool IsSectionCompressed(ReadOnlyMemory<byte> image, string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var reader = new SaveContainer.Reader(image);
        return reader.Has(name) && reader.IsCompressed(name);
    }
}
