namespace Colony.Generators.Tests;

/// <summary>
/// Generated Systems (ticket AC #1, #2 and #3): the definition with its access set, rate and ordering, the typed
/// query and the chunk entry. Each test's output is pinned by a snapshot under <c>Snapshots/</c>.
/// </summary>
public sealed class SystemGenerationTests
{
    private const string Components = """

        namespace Game
        {
            [Colony.Component] public partial struct Hunger { public Colony.Numerics.Fixed32 Value; }
            [Colony.Component] public partial struct Food { public int Units; }
            [Colony.Component] public partial struct Position { public int X; public int Y; }
            [Colony.Component] public partial struct Dead { }
        }
        """;

    [Fact]
    public void A_per_entity_system_with_a_ref_parameter_writes_that_component()
    {
        GeneratorHarness.Verify("""
            using Colony;
            using Colony.Numerics;

            namespace Game
            {
                public static partial class ColonySystems
                {
                    [System(NamedPhase.Simulate, Every = 1)]
                    public static void Starve(ref Hunger hunger) => hunger.Value += Fixed32.FromRatio(1, 600);
                }
            }
            """ + Components);
    }

    [Fact]
    public void A_per_entity_system_with_reads_writes_entity_and_context_declares_the_full_access_set()
    {
        GeneratorHarness.Verify("""
            using Colony;

            namespace Game
            {
                public static partial class MovementSystems
                {
                    [System(NamedPhase.Simulate, Every = 4, Offset = 1, After = ["Starve"], Before = ["Cleanup"], SkipAsleepChunks = true)]
                    [Without(typeof(Dead))]
                    [ChangeFilter(typeof(Position))]
                    public static void Move(in SystemContext context, EntityId entity, in Position position, ref Hunger hunger, in Food food)
                    {
                        hunger.Value = Colony.Numerics.Fixed32.FromInt32(position.X + food.Units);
                    }
                }
            }
            """ + Components);
    }

    [Fact]
    public void A_chunk_level_system_takes_the_chunk_view_and_declares_access_with_attributes()
    {
        GeneratorHarness.Verify("""
            using Colony;

            namespace Game
            {
                public static partial class BatchSystems
                {
                    [System(NamedPhase.Late, Name = "Cleanup")]
                    [Reads(typeof(Position))]
                    [Writes(typeof(Hunger))]
                    [With(typeof(Food))]
                    public static void Sweep(in SystemContext context, in ChunkView chunk)
                    {
                        _ = chunk.Count;
                    }

                    [System(NamedPhase.Late)]
                    [Writes(typeof(Food))]
                    public static void Regrow(in ChunkView chunk)
                    {
                        _ = chunk.Count;
                    }
                }
            }
            """ + Components);
    }

    [Fact]
    public void A_typed_query_system_gets_spans_per_declared_column_and_per_entity_ref_access()
    {
        GeneratorHarness.Verify("""
            using Colony;

            namespace Game
            {
                public static partial class QuerySystems
                {
                    [System(NamedPhase.Simulate)]
                    [Reads(typeof(Position))]
                    [Writes(typeof(Hunger))]
                    public static void Digest(in SystemContext context, in DigestQuery query)
                    {
                        for (int row = 0; row < query.Count; row++)
                        {
                            query.HungerAt(row).Value = Colony.Numerics.Fixed32.FromInt32(query.PositionAt(row).X);
                        }
                    }
                }
            }
            """ + Components);
    }

    [Fact]
    public void A_serial_system_with_only_a_context_becomes_a_begin_hook_in_a_game_phase()
    {
        GeneratorHarness.Verify("""
            using Colony;

            namespace Game
            {
                public static partial class Bookkeeping
                {
                    [System("Weather")]
                    [Reads(typeof(Position), typeof(Hunger))]
                    public static void Census(in SystemContext context)
                    {
                        _ = context.Tick;
                    }
                }
            }
            """ + Components);
    }

    [Fact]
    public void Systems_in_a_nested_partial_struct_in_the_global_namespace_are_generated()
    {
        GeneratorHarness.Verify("""
            using Colony;

            public partial class Outer
            {
                public partial struct Inner
                {
                    [System]
                    public static void Tick(ref Game.Hunger hunger) { }
                }
            }
            """ + Components);
    }
}
