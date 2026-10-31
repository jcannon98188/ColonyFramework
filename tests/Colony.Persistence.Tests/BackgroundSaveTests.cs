using Colony.Persistence;
using Colony.Testing.Tests.Scenarios;

namespace Colony.Persistence.Tests;

/// <summary>TASK-14 AC #5 (background save is byte-identical to a synchronous save at the same Tick) and AC #1's atomic write with backup generations.</summary>
public sealed class BackgroundSaveTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "colony-persistence-tests", Path.GetRandomFileName());

    public BackgroundSaveTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task A_background_save_matches_the_synchronous_save_at_the_same_tick_while_the_world_keeps_ticking()
    {
        var scenario = new ColonistHarnessScenario { Colonists = 3_000, Food = 150 };
        using var world = ScenarioWorlds.Create(scenario, workers: 2);
        for (int i = 0; i < 5; i++)
        {
            world.Tick();
        }

        byte[] synchronous = world.SaveToBytes();
        string path = Path.Combine(_directory, "world.save");
        var task = world.SaveInBackground(path);
        for (int i = 0; i < 10; i++)
        {
            world.Tick();
        }

        await task;

        Assert.Equal(synchronous, File.ReadAllBytes(path));
        Assert.Equal(5, WorldPersistence.ReadManifest(path).Tick);
        Assert.NotEqual(synchronous, world.SaveToBytes());
    }

    [Fact]
    public void A_snapshot_encodes_the_same_bytes_as_a_synchronous_save_at_its_tick()
    {
        using var world = TablesWorld.Compose().Create();
        var snapshot = world.Snapshot();
        byte[] expected = world.SaveToBytes();

        Assert.Equal(expected, snapshot.Encode());
        Assert.Equal(world.Hash(), snapshot.WorldHash);
        Assert.Equal(world.CurrentTick, snapshot.Tick);
    }

    [Fact]
    public void Saves_are_written_atomically_and_rotate_backup_generations()
    {
        using var world = TablesWorld.Compose().Create();
        string path = Path.Combine(_directory, "nested", "world.save");
        var options = new SaveOptions { BackupGenerations = 2 };

        byte[] first = world.SaveToBytes(options);
        world.Save(path, options);
        world.Tick();
        byte[] second = world.SaveToBytes(options);
        world.Save(path, options);
        world.Tick();
        byte[] third = world.SaveToBytes(options);
        world.Save(path, options);
        world.Tick();
        byte[] fourth = world.SaveToBytes(options);
        world.Save(path, options);

        Assert.Equal(fourth, File.ReadAllBytes(path));
        Assert.Equal(third, File.ReadAllBytes(path + ".bak1"));
        Assert.Equal(second, File.ReadAllBytes(path + ".bak2"));
        Assert.False(File.Exists(path + ".bak3"));
        Assert.False(File.Exists(path + ".tmp"));
        Assert.NotEqual(first, second);

        using var loaded = WorldPersistence.Load(path, TablesWorld.Compose().Schema);
        Assert.Equal(world.Hash(), loaded.Hash());
    }

    [Fact]
    public void Zero_backup_generations_keeps_only_the_latest_save()
    {
        using var world = TablesWorld.Compose().Create();
        string path = Path.Combine(_directory, "world.save");
        var options = new SaveOptions { BackupGenerations = 0 };
        world.Save(path, options);
        world.Tick();
        world.Save(path, options);

        Assert.True(File.Exists(path));
        Assert.False(File.Exists(path + ".bak1"));
    }
}
