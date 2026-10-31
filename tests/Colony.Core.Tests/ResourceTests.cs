namespace Colony.Core.Tests;

public struct ColonyClock
{
    public long Day;
    public int Hour;
}

public struct Weather
{
    public int Temperature;
}

/// <summary>TASK-8 AC #1: Resources are World-owned singleton unmanaged structs with declared access, hashed with the World.</summary>
public sealed class ResourceTests
{
    private static WorldSchema Schema() => new WorldBuilder()
        .AddComponent<Health>("health")
        .AddResource<ColonyClock>("clock")
        .AddResource<Weather>("weather")
        .Build();

    [Fact]
    public void A_registered_resource_starts_zeroed_and_is_read_and_written_by_reference()
    {
        var schema = Schema();
        using var world = schema.CreateWorld();
        var clock = schema.Resource<ColonyClock>();

        Assert.Equal(clock, schema.Resource("clock"));
        Assert.Equal(3, schema.Resources.Length); // the built-in StructuralFailures queue is id 0
        Assert.Equal(schema.StructuralFailures, schema.Resources[0].Type);
        Assert.Equal("weather", schema.DescribeResource(schema.Resource<Weather>()).Name);
        Assert.Equal(0, world.Resource<ColonyClock>(clock).Day);

        world.Resource<ColonyClock>(clock).Day = 7;
        world.Resource<ColonyClock>(clock).Hour = 3;

        Assert.Equal(7, world.Resource<ColonyClock>(clock).Day);
        Assert.Equal(3, world.Resource<ColonyClock>(clock).Hour);
        Assert.Equal(0, world.Resource<Weather>(schema.Resource<Weather>()).Temperature);
    }

    [Fact]
    public void Resource_values_are_part_of_the_world_hash_and_their_layout_is_part_of_the_schema_hash()
    {
        var schema = Schema();
        using var a = schema.CreateWorld();
        using var b = schema.CreateWorld();
        var clock = schema.Resource<ColonyClock>();
        Assert.Equal(a.Hash(), b.Hash());

        b.Resource<ColonyClock>(clock).Hour = 1;

        Assert.NotEqual(a.Hash(), b.Hash());
        var without = new WorldBuilder().AddComponent<Health>("health").Build();
        Assert.NotEqual(without.SchemaHash, schema.SchemaHash);
    }

    [Fact]
    public void Registration_is_validated()
    {
        var builder = new WorldBuilder().AddResource<ColonyClock>("clock");

        Assert.Throws<ArgumentException>(() => builder.AddResource<Weather>("clock"));
        Assert.Throws<ArgumentException>(() => builder.AddResource<ColonyClock>("again"));
        Assert.Throws<ArgumentException>(() => builder.Resource<Weather>());
        var schema = builder.Build();
        Assert.Throws<ArgumentException>(() => schema.Resource("nothing"));
        using var world = schema.CreateWorld();
        Assert.Throws<ArgumentException>(() => world.Resource<Weather>(schema.Resource<ColonyClock>()));
        Assert.Throws<ArgumentException>(() => world.Resource<ColonyClock>(new ResourceType(5)));
    }
}
