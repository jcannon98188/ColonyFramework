using Colony.Plugins.Native;

namespace Colony.Native.Tests;

/// <summary>
/// TASK-42 AC #2: <c>colony_commands_emit</c> on a System's job command buffer is checked against the queues the
/// System declared (<c>emits</c>, i.e. <see cref="SystemDefinition.WritesResources"/>), like
/// <see cref="SystemContext.Emit{T}"/>. A buffer the host made is not a System's and is not checked.
/// </summary>
[Collection(NativeTestGroup.Name)]
public sealed unsafe class EmitDeclarationTests
{
    private struct Ping
    {
        public int Value;
    }

    /// <summary>Same size as <see cref="Ping"/>, so <see cref="Emit"/> fits either queue; only its type is read.</summary>
    private struct Pong
    {
#pragma warning disable CS0649 // never assigned: only the size matters
        public int Value;
#pragma warning restore CS0649
    }

    private sealed class Observed
    {
        public int DeclaredStatus = -1;
        public int UndeclaredStatus = -1;
        public string UndeclaredError = string.Empty;
    }

    private static int Emit(nint commands, ResourceType queue)
    {
        var ping = new Ping { Value = 7 };
        var emit = (delegate* unmanaged<nint, int, void*, int, int>)AbiDefinition.Entries.First(e => e.Name == "commands_emit").Address;
        return emit(commands, queue.Id, &ping, sizeof(Ping));
    }

    [Fact]
    public void A_system_emitting_through_the_abi_on_an_undeclared_queue_is_rejected()
    {
        var observed = new Observed();
        var builder = new WorldBuilder();
        builder.AddEvent<Ping>("declared");
        builder.AddEvent<Pong>("undeclared");
        var declared = builder.Resource<Ping>();
        var undeclared = builder.Resource<Pong>();

        // A System ahead of the emitter that does declare the queue, so the check must find the emitter's own declaration.
        builder.AddSystem(new SystemDefinition
        {
            Name = "abi.declares-everything",
            WritesResources = ResourceMask.Of(declared, undeclared),
            Begin = static (in SystemContext context) => { },
        });
        builder.AddSystem(new SystemDefinition
        {
            Name = "abi.emitter",
            WritesResources = ResourceMask.Of(declared),
            Begin = (in SystemContext context) =>
            {
                nint commands = NativeHandles.GetOrAdd(context.Commands, context.World);
                observed.DeclaredStatus = Emit(commands, declared);
                observed.UndeclaredStatus = Emit(commands, undeclared);
                observed.UndeclaredError = ColonyAbi.LastError;
            },
        });
        using var world = builder.Build().CreateWorld(new WorldOptions { WorkerThreads = 1 });

        Assert.True(world.Tick());

        Assert.Equal(ColonyAbi.Ok, observed.DeclaredStatus);
        Assert.NotEqual(ColonyAbi.Ok, observed.UndeclaredStatus);
        Assert.Contains("abi.emitter", observed.UndeclaredError, StringComparison.Ordinal);
        Assert.Contains("undeclared", observed.UndeclaredError, StringComparison.Ordinal);
    }

    [Fact]
    public void A_host_command_buffer_emits_on_any_event_queue()
    {
        var builder = new WorldBuilder();
        builder.AddEvent<Ping>("free");
        var queue = builder.Resource<Ping>();
        using var world = builder.Build().CreateWorld(new WorldOptions { WorkerThreads = 1 });
        nint commands = NativeHandles.GetOrAdd(world.CreateCommandBuffer(), world);

        Assert.Equal(ColonyAbi.Ok, Emit(commands, queue));
    }
}
