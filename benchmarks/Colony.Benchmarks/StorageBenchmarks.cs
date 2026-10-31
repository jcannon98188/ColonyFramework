using BenchmarkDotNet.Attributes;
using Colony.Scenario;

namespace Colony.Benchmarks;

/// <summary>Entity storage (spec section 4.1): churn through command buffers, archetype moves, and query iteration over 100k colonists.</summary>
public class StorageBenchmarks
{
    public const int Colonists = 100_000;
    public const int Batch = 1_000;

    private World _world = null!;
    private ComponentType _position;
    private ComponentType _target;
    private ComponentType _hunger;
    private ComponentType _health;
    private ComponentMask _colonist;
    private readonly EntityId[] _victims = new EntityId[Batch];

    [GlobalSetup]
    public void Setup()
    {
        var schema = ColonistScenario.BuildSchema();
        _world = ColonistScenario.CreateWorld(schema, workers: 1, colonists: Colonists);
        _position = schema.Component<ColonistPosition>();
        _target = schema.Component<ColonistTarget>();
        _hunger = schema.Component<ColonistHunger>();
        _health = schema.Component<ColonistHealth>();
        _colonist = ComponentMask.Of(_position, _target, _hunger, _health);
    }

    [GlobalCleanup]
    public void Cleanup() => _world.Dispose();

    /// <summary>Destroys 1000 colonists and creates 1000 new ones in one playback: the scenario's churn, isolated.</summary>
    [Benchmark(OperationsPerInvoke = Batch)]
    public void ChurnThroughCommandBuffer()
    {
        PickVictims();
        var buffer = _world.CreateCommandBuffer();
        for (int i = 0; i < Batch; i++)
        {
            buffer.Destroy(_victims[i]);
            var born = buffer.Create(_colonist);
            buffer.Add(born, _position, new ColonistPosition { X = i, Y = i });
            buffer.Add(born, _target, new ColonistTarget { X = i, Y = i });
            buffer.Add(born, _hunger, new ColonistHunger { Value = 0 });
            buffer.Add(born, _health, new ColonistHealth { Value = 100 });
        }

        _world.Playback(buffer);
        _world.ResetScratch();
    }

    /// <summary>Removes a Component from 1000 colonists and adds it back: two archetype moves per Entity.</summary>
    [Benchmark(OperationsPerInvoke = Batch)]
    public void RemoveAndReAddComponent()
    {
        PickVictims();
        var remove = _world.CreateCommandBuffer();
        for (int i = 0; i < Batch; i++)
        {
            remove.Remove(_victims[i], _health);
        }

        _world.Playback(remove);
        var add = _world.CreateCommandBuffer();
        for (int i = 0; i < Batch; i++)
        {
            add.Add(_victims[i], _health, new ColonistHealth { Value = 100 });
        }

        _world.Playback(add);
        _world.ResetScratch();
    }

    /// <summary>Visits every chunk of the colonist archetype and sums one column.</summary>
    [Benchmark(OperationsPerInvoke = Colonists)]
    public long QuerySumHealth()
    {
        long sum = 0;
        foreach (var chunk in _world.Query(_colonist))
        {
            foreach (ref readonly var health in chunk.Column<ColonistHealth>(_health))
            {
                sum += health.Value;
            }
        }

        return sum;
    }

    private void PickVictims()
    {
        int n = 0;
        foreach (var chunk in _world.Query(_colonist))
        {
            var entities = chunk.Entities;
            for (int i = 0; i < entities.Length && n < Batch; i++)
            {
                _victims[n++] = entities[i];
            }

            if (n == Batch)
            {
                return;
            }
        }
    }
}
