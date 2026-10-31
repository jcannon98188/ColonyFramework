using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Colony.Numerics;

namespace Colony.Core.Tests;

/// <summary>
/// The registration hook the source generator targets (spec sections 4.1 and 11): a Component that
/// implements <see cref="IComponent{TSelf}"/> registers under its layout's stable name and the layout hash
/// covers field names, types and offsets, not only size and alignment.
/// </summary>
public sealed class ComponentLayoutTests
{
    // Hand-written stand-ins for what the generator emits.
    public struct Hunger : IComponent<Hunger>
    {
        public Fixed32 Value;

        public static ComponentLayout Layout => new("hunger", ComponentOptions.None, [new ComponentField("Value", "Colony.Numerics.Fixed32", 0, 4)]);
    }

    public struct Thirst : IComponent<Thirst>
    {
        public Fixed32 Value;

        public static ComponentLayout Layout => new("hunger", ComponentOptions.None, [new ComponentField("Amount", "Colony.Numerics.Fixed32", 0, 4)]);
    }

    public struct Marker : IComponent<Marker>
    {
        public int Stamp;

        public static ComponentLayout Layout => new("marker", ComponentOptions.NonFragmenting, [new ComponentField("Stamp", "int", 0, 4)]);
    }

    [Fact]
    public void A_layout_component_registers_under_its_stable_name_with_its_fields()
    {
        var schema = new WorldBuilder().AddComponent<Hunger>().AddComponent<Marker>().Build();

        var hunger = schema.Describe(schema.Component<Hunger>());
        Assert.Equal("hunger", hunger.Name);
        Assert.Equal(4, hunger.Size);
        Assert.Equal(4, hunger.Alignment);
        Assert.Equal(Hunger.Layout.Fields, hunger.Fields);
        Assert.True(schema.Describe(schema.Component<Marker>()).IsNonFragmenting);
        Assert.Equal(schema.Component<Hunger>(), schema.Component("hunger"));
    }

    [Fact]
    public void The_layout_hash_covers_field_names_and_types_not_only_size_and_alignment()
    {
        var byName = new WorldBuilder().AddComponent<Hunger>("hunger").Build();
        var byLayout = new WorldBuilder().AddComponent<Hunger>().Build();
        var otherFieldName = new WorldBuilder().AddComponent<Thirst>().Build();

        ulong nameOnly = byName.Describe(byName.Component("hunger")).LayoutHash;
        ulong fieldLevel = byLayout.Describe(byLayout.Component("hunger")).LayoutHash;
        ulong renamedField = otherFieldName.Describe(otherFieldName.Component("hunger")).LayoutHash;

        Assert.NotEqual(nameOnly, fieldLevel);
        Assert.NotEqual(fieldLevel, renamedField);
        Assert.Empty(byName.Describe(byName.Component("hunger")).Fields);
    }

    [Fact]
    public void The_layout_hash_is_a_pure_function_of_the_layout()
    {
        var first = new WorldBuilder().AddComponent<Hunger>().Build();
        var second = new WorldBuilder().AddComponent<Marker>().AddComponent<Hunger>().Build();

        Assert.Equal(first.Describe(first.Component<Hunger>()).LayoutHash, second.Describe(second.Component<Hunger>()).LayoutHash);
        Assert.Equal(
            ComponentLayout.ComputeHash("hunger", 4, 4, Hunger.Layout.Fields),
            first.Describe(first.Component<Hunger>()).LayoutHash);
    }

    [Fact]
    public void A_layout_rejects_fields_outside_the_struct()
    {
        Assert.Throws<ArgumentException>(() => new ComponentLayout("bad", ComponentOptions.None, [new ComponentField("X", "int", 4, Unsafe.SizeOf<int>())]).Validate(4));
        Assert.Throws<ArgumentException>(() => new ComponentLayout("", ComponentOptions.None, ImmutableArray<ComponentField>.Empty));
    }

    [Fact]
    public void The_builder_resolves_phases_by_name()
    {
        var builder = new WorldBuilder();
        var weather = builder.AddPhase("Weather", Phase.Simulate);

        Assert.Equal(Phase.Simulate, builder.PhaseNamed("Simulate"));
        Assert.Equal(weather, builder.PhaseNamed("Weather"));
        Assert.Throws<ArgumentException>(() => builder.PhaseNamed("Nope"));
    }

    [Fact]
    public void Fixed32_FromRatio_rounds_to_nearest()
    {
        Assert.Equal(Fixed32.Half, Fixed32.FromRatio(1, 2));
        Assert.Equal(Fixed32.FromInt32(-3), Fixed32.FromRatio(-6, 2));
        Assert.Equal(Fixed32.FromDouble(1.0 / 600), Fixed32.FromRatio(1, 600));
        Assert.Equal(Fixed32.MaxValue, Fixed32.FromRatio(1, 0));
    }
}
