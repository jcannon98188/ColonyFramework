using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Colony.Diagnostics.Inspection;

/// <summary>What a debug command does to the World.</summary>
public enum DebugAction
{
    /// <summary>Creates an Entity with the listed Components, zero-initialised.</summary>
    Create = 1,

    /// <summary>Destroys the Entity.</summary>
    Destroy = 2,

    /// <summary>Adds the Component with the given raw value, or overwrites it when present.</summary>
    Add = 3,

    /// <summary>Removes the Component.</summary>
    Remove = 4,
}

/// <summary>
/// Debug mutations as Input Commands (spec section 10.2: "mutation only as debug commands through the command
/// buffer, recorded in the replay log"). <see cref="AddDebugCommands"/> registers the Input Command
/// <see cref="InputCommandName"/> and its Input-Phase handler System <see cref="SystemName"/>, which applies each
/// command through its command buffer. Because they are ordinary Input Commands, a <c>ReplayRecorder</c> records
/// them and a replay re-applies them at the same Tick, so a debugged run stays reproducible. The inspection server
/// submits them from <see cref="Source"/> when the host opted into <c>AllowMutation</c>; a Game that never calls
/// <see cref="AddDebugCommands"/> cannot be mutated by tooling at all.
/// </summary>
public static class DebugCommands
{
    public const string InputCommandName = "colony.debug";

    public const string SystemName = "colony.debug.apply";

    /// <summary>Largest raw Component value (or Component id list for Create) one debug command carries.</summary>
    public const int PayloadCapacity = 240;

    /// <summary>Rejection codes on the <see cref="WorldSchema.CommandRejections"/> queue.</summary>
    public const int RejectUnknownComponent = 0xDB01;

    public const int RejectBadValue = 0xDB02;

    public const int RejectUnknownAction = 0xDB03;

    /// <summary>The Input source debug commands are submitted from; Games must not use it for players.</summary>
    public static InputSource Source { get; } = new(0xFFFF_DB60);

    /// <summary>
    /// Registers the debug Input Command and its handler System. Call once, while composing the schema. Through the
    /// Input permission table (TASK-35) the command is denied to every source but <see cref="Source"/>, so a client
    /// submitting it is rejected at the Tick boundary like any other denied command; a Game may grant it to another
    /// source with <see cref="WorldBuilder.GrantInput"/>.
    /// </summary>
    public static InputCommandType AddDebugCommands(this WorldBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var type = builder.AddInputCommand<DebugCommand>(InputCommandName);
        builder.SetInputAccess(type, InputAccess.Deny);
        builder.GrantInput(Source, type);
        builder.AddSystem(new SystemDefinition
        {
            Name = SystemName,
            Phase = Phase.Input,
            Begin = (in SystemContext context) => Apply(in context, type),
        });
        return type;
    }

    /// <summary>The registered debug command type, if the schema has one.</summary>
    public static bool TryGetDebugCommands(this WorldSchema schema, out InputCommandType type)
    {
        ArgumentNullException.ThrowIfNull(schema);
        return schema.TryGetInputCommand(InputCommandName, out type);
    }

    internal static DebugCommand Create(ReadOnlySpan<ComponentType> components)
    {
        var command = new DebugCommand { Action = DebugAction.Create, Length = components.Length * sizeof(int) };
        if (command.Length > PayloadCapacity)
        {
            throw new ArgumentException($"A debug Create carries at most {PayloadCapacity / sizeof(int)} Components.", nameof(components));
        }

        Span<int> ids = MemoryMarshal.Cast<byte, int>(command.Payload[..command.Length]);
        for (int i = 0; i < components.Length; i++)
        {
            ids[i] = components[i].Id;
        }

        return command;
    }

    internal static DebugCommand Destroy(EntityId entity) => new() { Action = DebugAction.Destroy, Entity = entity.Value };

    internal static DebugCommand Remove(EntityId entity, ComponentType component) => new() { Action = DebugAction.Remove, Entity = entity.Value, Component = component.Id };

    internal static DebugCommand Add(EntityId entity, ComponentType component, ReadOnlySpan<byte> value)
    {
        if (value.Length > PayloadCapacity)
        {
            throw new ArgumentException($"A debug Add carries at most {PayloadCapacity} bytes; the value has {value.Length}.", nameof(value));
        }

        var command = new DebugCommand { Action = DebugAction.Add, Entity = entity.Value, Component = component.Id, Length = value.Length };
        value.CopyTo(command.Payload);
        return command;
    }

    private static void Apply(in SystemContext context, InputCommandType type)
    {
        var schema = context.World.Schema;
        Span<ComponentType> components = stackalloc ComponentType[PayloadCapacity / sizeof(int)];
        foreach (var entry in context.Inputs<DebugCommand>(type))
        {
            var command = entry.Command;
            var entity = new EntityId(command.Entity);
            switch (command.Action)
            {
                case DebugAction.Create:
                    var ids = MemoryMarshal.Cast<byte, int>(command.Payload[..Math.Clamp(command.Length, 0, PayloadCapacity)]);
                    bool known = true;
                    for (int i = 0; i < ids.Length; i++)
                    {
                        known &= (uint)ids[i] < (uint)schema.Components.Length;
                        components[i] = known ? schema.Components[ids[i]].Type : default;
                    }

                    if (!known)
                    {
                        context.Reject(in entry, RejectUnknownComponent);
                        break;
                    }

                    context.Commands.Create(ComponentMask.Of(components[..ids.Length]));
                    break;
                case DebugAction.Destroy:
                    context.Commands.Destroy(entity);
                    break;
                case DebugAction.Add:
                case DebugAction.Remove:
                    if ((uint)command.Component >= (uint)schema.Components.Length)
                    {
                        context.Reject(in entry, RejectUnknownComponent);
                        break;
                    }

                    var descriptor = schema.Components[command.Component];
                    if (command.Action == DebugAction.Remove)
                    {
                        context.Commands.Remove(entity, descriptor.Type);
                    }
                    else if (command.Length != descriptor.Size || descriptor.IsBuffer)
                    {
                        context.Reject(in entry, RejectBadValue);
                    }
                    else
                    {
                        context.Commands.Add(entity, descriptor.Type, command.Payload[..command.Length]);
                    }

                    break;
                default:
                    context.Reject(in entry, RejectUnknownAction);
                    break;
            }
        }
    }
}

/// <summary>The fixed-size Input Command behind <see cref="DebugCommands"/> (unmanaged, so it is saved, hashed and replayed like any other).</summary>
internal struct DebugCommand
{
    public DebugAction Action;
    public int Component;
    public ulong Entity;
    public int Length;
    public DebugPayload Payload;
}

[InlineArray(DebugCommands.PayloadCapacity)]
internal struct DebugPayload
{
    private byte _element0;
}
