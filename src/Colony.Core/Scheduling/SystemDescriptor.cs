namespace Colony;

/// <summary>
/// Where the scheduler placed one System (spec section 4.3): its Phase, its position in the Phase's
/// stable order and the wave it runs in. Systems in one wave never conflict and run concurrently.
/// </summary>
public readonly record struct SystemDescriptor(
    int Id,
    string Name,
    Phase Phase,
    int Order,
    int Wave,
    ComponentMask Reads,
    ComponentMask Writes,
    int Every,
    int Offset);
