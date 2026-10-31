using Colony.Persistence;

namespace Colony.Diagnostics.Inspection;

/// <summary>
/// How a host starts its <see cref="InspectionServer"/> (spec section 10.2). The server is off unless the host
/// constructs one; it is read-only unless <see cref="AllowMutation"/> is set, and every connection must present
/// <see cref="Token"/> in its <c>hello</c> before any other request.
/// </summary>
public sealed class InspectionServerOptions
{
    /// <summary>The shared secret clients must present. Null generates a random 128-bit token (read it back from <see cref="InspectionServer.Token"/>); empty is refused.</summary>
    public string? Token { get; init; }

    /// <summary>Lets clients submit debug commands (<see cref="DebugCommands"/>). Off by default: the server only reads.</summary>
    public bool AllowMutation { get; init; }

    /// <summary>
    /// The host's replay recorder for this World, if it records one. Enables <c>rewind</c> (a past Tick is rebuilt
    /// from the log and inspected) and makes debug commands part of a replay. The host must Tick through it
    /// (<see cref="ReplayRecorder.Tick"/>, or <c>World.Tick</c> plus <see cref="ReplayRecorder.RecordTick"/>).
    /// </summary>
    public ReplayRecorder? Replay { get; init; }

    /// <summary>The gate <c>pause</c>/<c>step</c>/<c>resume</c> act on; null uses the World's own <see cref="World.TickGate"/>.</summary>
    public TickGate? TickGate { get; init; }

    /// <summary>Upper bound on Entities, cells or hits one read returns (protects the host from huge replies).</summary>
    public int MaxItemsPerReply { get; init; } = 4096;
}
