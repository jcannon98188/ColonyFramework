namespace Colony;

/// <summary>
/// Thrown by <see cref="World.Tick"/> when a System threw (spec section 4.3): the Tick is aborted and the
/// World stays faulted (<see cref="World.Fault"/>) until disposed. The System is named; the original
/// exception is <see cref="Exception.InnerException"/>.
/// </summary>
public sealed class SystemFaultException : Exception
{
    public SystemFaultException()
        : base("A System faulted.")
    {
    }

    public SystemFaultException(string message)
        : base(message)
    {
    }

    public SystemFaultException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    internal SystemFaultException(in WorldFault fault)
        : base($"System '{fault.SystemName}' threw during Tick {fault.Tick} in Phase '{fault.Phase.Name}'; the Tick was aborted and the World is faulted.", fault.Exception)
    {
        SystemName = fault.SystemName;
        Tick = fault.Tick;
        Fault = fault;
    }

    public string? SystemName { get; }

    public long Tick { get; }

    internal WorldFault? Fault { get; }
}

/// <summary>Why a World is faulted: the System that threw, when, and what it threw.</summary>
public readonly record struct WorldFault(string SystemName, int SystemId, Phase Phase, long Tick, Exception Exception);
