using System.Runtime.InteropServices;
using Colony.Diagnostics;

namespace Colony.Core.Tests;

public struct Ledger
{
    public long Gold;
    public int Debt;
}

/// <summary>
/// TASK-29 (spec section 10.2): the Core seams the inspection protocol reads and writes through. Raw bytes
/// by registered Component or Resource (the protocol never knows CLR types), a wire-form Add through the
/// command buffer (debug mutations are ordinary structural ops) and inspectors a Plugin registers in Register.
/// </summary>
public sealed class InspectionSeamTests
{
    private static WorldSchema Schema() => new WorldBuilder()
        .AddComponent<Position>("position")
        .AddComponent<Hunger>("hunger")
        .AddComponent<Marked>("marked", ComponentOptions.NonFragmenting)
        .AddResource<Ledger>("ledger")
        .DebugValidation(false)
        .Build();

    [Fact]
    public void Component_bytes_are_the_raw_value_for_fragmenting_and_non_fragmenting_components()
    {
        var schema = Schema();
        using var world = schema.CreateWorld();
        var position = schema.Component<Position>();
        var marked = schema.Component<Marked>();
        var commands = world.CreateCommandBuffer();
        var deferred = commands.Create(ComponentMask.Of(position));
        commands.Add(deferred, position, new Position { X = 7, Y = -2 });
        commands.Add(deferred, marked, new Marked { Stamp = 99 });
        world.Playback(commands);
        var id = commands.Resolve(deferred);

        var positionBytes = world.ComponentBytes(id, position);
        var markedBytes = world.ComponentBytes(id, marked);

        Assert.Equal(8, positionBytes.Length);
        Assert.Equal(7, MemoryMarshal.Read<int>(positionBytes));
        Assert.Equal(-2, MemoryMarshal.Read<int>(positionBytes[4..]));
        Assert.Equal(99, MemoryMarshal.Read<int>(markedBytes));
        Assert.Throws<InvalidOperationException>(() => world.ComponentBytes(id, schema.Component<Hunger>()));
        Assert.Throws<InvalidOperationException>(() => world.ComponentBytes(EntityId.Null, position));
    }

    [Fact]
    public void A_wire_form_add_through_the_command_buffer_overwrites_the_component()
    {
        var schema = Schema();
        using var world = schema.CreateWorld();
        var position = schema.Component<Position>();
        var commands = world.CreateCommandBuffer();
        var deferred = commands.Create(ComponentMask.Of(position));
        world.Playback(commands);
        var id = commands.Resolve(deferred);

        Span<byte> payload = stackalloc byte[8];
        MemoryMarshal.Write(payload, 11);
        MemoryMarshal.Write(payload[4..], 13);
        commands.Add(id, position, payload);
        world.Playback(commands);

        Assert.Equal(11, world.Get<Position>(id, position).X);
        Assert.Equal(13, world.Get<Position>(id, position).Y);
        Assert.Throws<ArgumentException>(() => commands.Add(id, position, new byte[3]));
    }

    [Fact]
    public void Resource_bytes_are_the_raw_value()
    {
        var schema = Schema();
        using var world = schema.CreateWorld();
        var ledger = schema.Resource<Ledger>();
        world.Resource<Ledger>(ledger).Gold = 1234;
        world.Resource<Ledger>(ledger).Debt = -5;

        var bytes = world.ResourceBytes(ledger);

        Assert.Equal(16, bytes.Length);
        Assert.Equal(1234L, MemoryMarshal.Read<long>(bytes));
        Assert.Equal(-5, MemoryMarshal.Read<int>(bytes[8..]));
        Assert.Throws<ArgumentException>(() => world.ResourceBytes(schema.StructuralFailures));
    }

    [Fact]
    public void Inspectors_registered_on_the_builder_appear_in_the_schema_and_run_against_a_world()
    {
        var builder = new WorldBuilder().AddComponent<Position>("position").AddResource<Ledger>("ledger");
        var ledger = builder.Resource<Ledger>();
        builder.AddInspector("economy", (world, output) =>
        {
            output.Add("gold", world.Resource<Ledger>(ledger).Gold);
            output.Add("ratio", 0.5);
            output.Add("mood", "fine");
            output.Add("richest", new EntityId(3, 1));
        });
        Assert.Throws<ArgumentException>(() => builder.AddInspector("economy", (_, _) => { }));
        var schema = builder.Build();
        using var world = schema.CreateWorld();
        world.Resource<Ledger>(ledger).Gold = 42;

        Assert.Single(schema.Inspectors.ToArray());
        var inspector = schema.Inspector("economy");
        Assert.Equal(0, inspector.Id);
        var output = new InspectorOutput();
        inspector.Run(world, output);

        Assert.Equal(4, output.Entries.Count);
        Assert.Equal(("gold", InspectorValueKind.Whole), (output.Entries[0].Name, output.Entries[0].Kind));
        Assert.Equal(42L, output.Entries[0].Whole);
        Assert.Equal(0.5, output.Entries[1].Number);
        Assert.Equal("fine", output.Entries[2].Text);
        Assert.Equal(new EntityId(3, 1), output.Entries[3].Entity);
        Assert.Throws<ArgumentException>(() => schema.Inspector("nope"));
    }
}
