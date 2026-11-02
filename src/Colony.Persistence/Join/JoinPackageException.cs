namespace Colony.Persistence;

/// <summary>Why a join package was refused (<see cref="JoinPackageException.Reason"/>).</summary>
public enum JoinFailureReason
{
    /// <summary>A chunk does not start with the join chunk magic, or a package image is not a join package.</summary>
    NotAJoinPackage,

    /// <summary>The chunk or package format version is not this build's.</summary>
    UnsupportedVersion,

    /// <summary>A chunk is shorter than its header or than the payload it declares.</summary>
    ChunkTruncated,

    /// <summary>A chunk's bytes do not match its hash.</summary>
    ChunkCorrupt,

    /// <summary>A chunk belongs to a different package (length, hash or chunk count disagree), or repeats an index with other bytes.</summary>
    ChunkFromAnotherPackage,

    /// <summary>The package was completed before every chunk arrived.</summary>
    ChunksMissing,

    /// <summary>The reassembled package does not match the whole-package hash, or a section fails its own hash.</summary>
    PackageCorrupt,

    /// <summary>The package's sections decode to something inconsistent (truncated section, Ticks out of range...).</summary>
    Malformed,

    /// <summary>The embedded save does not load or does not hash to the World hash it records.</summary>
    SaveCorrupt,

    /// <summary>The joining peer does not match the host (<see cref="JoinPackageException.Handshake"/> lists why).</summary>
    IdentityMismatch,

    /// <summary>Playing the input log forward did not reproduce a recorded checkpoint hash (<see cref="JoinPackageException.Tick"/>).</summary>
    Diverged,
}

/// <summary>A join package could not be received or joined; <see cref="Reason"/> says why, the message says where.</summary>
public sealed class JoinPackageException : Exception
{
    public JoinPackageException()
    {
    }

    public JoinPackageException(string message)
        : base(message)
    {
    }

    public JoinPackageException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public JoinPackageException(JoinFailureReason reason, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Reason = reason;
    }

    public JoinFailureReason Reason { get; init; }

    /// <summary>The handshake comparison, for <see cref="JoinFailureReason.IdentityMismatch"/>.</summary>
    public HandshakeResult? Handshake { get; init; }

    /// <summary>The Tick that failed verification, for <see cref="JoinFailureReason.Diverged"/>.</summary>
    public long? Tick { get; init; }

    /// <summary>The container section that failed, when one did.</summary>
    public string? Section { get; init; }
}
