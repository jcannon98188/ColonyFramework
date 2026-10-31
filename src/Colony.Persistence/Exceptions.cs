namespace Colony.Persistence;

/// <summary>The save is not a container this version understands, or a section is malformed (spec section 7.1).</summary>
public class SaveFormatException : IOException
{
    public SaveFormatException()
    {
    }

    public SaveFormatException(string message)
        : base(message)
    {
    }

    public SaveFormatException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>A section's bytes do not match their recorded hash or cannot be decompressed; <see cref="Section"/> names it.</summary>
public sealed class SaveCorruptionException : SaveFormatException
{
    public SaveCorruptionException()
    {
    }

    public SaveCorruptionException(string message)
        : base(message)
    {
    }

    public SaveCorruptionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public SaveCorruptionException(string section, string message)
        : base(message)
    {
        Section = section;
    }

    public SaveCorruptionException(string section, string message, Exception innerException)
        : base(message, innerException)
    {
        Section = section;
    }

    /// <summary>The container section that failed, as named in the table of contents.</summary>
    public string? Section { get; }
}

/// <summary>A saved Component's layout differs from the schema's and no registered migration chain connects them (spec section 7.1).</summary>
public sealed class MigrationException : InvalidOperationException
{
    public MigrationException()
    {
    }

    public MigrationException(string message)
        : base(message)
    {
    }

    public MigrationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
