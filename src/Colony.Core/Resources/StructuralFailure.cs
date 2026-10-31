namespace Colony;

/// <summary>A structural operation recorded in a <see cref="CommandBuffer"/>.</summary>
public enum StructuralOp : byte
{
    Destroy = 1,
    Add = 2,
    Remove = 3,

    /// <summary>A <see cref="CommandBuffer.Relate"/> whose target was dead: <see cref="StructuralFailure.Entity"/> names the target.</summary>
    Relate = 4,
}

/// <summary>Why a structural op was not applied.</summary>
public enum StructuralFailureReason : byte
{
    /// <summary>The Entity was destroyed earlier in the same Phase by an op that came first in stable merge order (spec section 4.1).</summary>
    Conflict = 1,

    /// <summary>The Entity was already dead before this Phase: the id was stale when the op was recorded.</summary>
    Stale = 2,
}

/// <summary>
/// The deterministic failure event (spec section 4.1) a structural op receives when it loses: delivered on
/// the built-in <see cref="WorldSchema.StructuralFailures"/> queue at the Phase boundary where the op was
/// merged, so it is readable from the next Phase. <see cref="Component"/> is default for a Destroy;
/// <see cref="SystemId"/> is -1 for a buffer the host played back.
/// </summary>
public readonly record struct StructuralFailure(
    EntityId Entity,
    StructuralOp Op,
    ComponentType Component,
    int SystemId,
    StructuralFailureReason Reason);
