namespace Colony.Persistence.Tests;

/// <summary>
/// Spec section 7.1: a save is atomic. A crash (simulated by a fault injected after every step the writer takes)
/// must always leave the save path holding a complete file, either the old save or the new one, never nothing.
/// </summary>
public sealed class AtomicFileTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "colony-atomic-" + Environment.ProcessId + "-" + Environment.CurrentManagedThreadId + "-" + Path.GetRandomFileName());

    public AtomicFileTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    public void A_crash_after_any_step_leaves_the_old_or_the_new_save_in_place(int backups)
    {
        byte[] old = [1, 2, 3];
        byte[] next = [4, 5, 6, 7];
        var steps = new List<string>();
        string probe = Path.Combine(_directory, "probe.sav");
        File.WriteAllBytes(probe, old);
        File.WriteAllBytes(AtomicFile.BackupPath(probe, 1), [9]);
        AtomicFile.Write(probe, next, backups, steps.Add);
        Assert.NotEmpty(steps);

        foreach (string step in steps)
        {
            string path = Path.Combine(_directory, step + ".sav");
            File.WriteAllBytes(path, old);
            File.WriteAllBytes(AtomicFile.BackupPath(path, 1), [9]);

            Assert.Throws<InjectedCrash>(() => AtomicFile.Write(path, next, backups, s =>
            {
                if (s == step)
                {
                    throw new InjectedCrash();
                }
            }));

            Assert.True(File.Exists(path), $"crash after '{step}' left no save at the path");
            byte[] read = File.ReadAllBytes(path);
            Assert.True(read.AsSpan().SequenceEqual(old) || read.AsSpan().SequenceEqual(next), $"crash after '{step}' left a torn save");
        }
    }

    [Fact]
    public void Backups_rotate_and_a_retry_after_a_crash_succeeds()
    {
        string path = Path.Combine(_directory, "world.sav");
        AtomicFile.Write(path, [1], 2);
        AtomicFile.Write(path, [2], 2);
        AtomicFile.Write(path, [3], 2);
        Assert.Equal([3], File.ReadAllBytes(path));
        Assert.Equal([2], File.ReadAllBytes(AtomicFile.BackupPath(path, 1)));
        Assert.Equal([1], File.ReadAllBytes(AtomicFile.BackupPath(path, 2)));

        Assert.Throws<InjectedCrash>(() => AtomicFile.Write(path, [4], 2, _ => throw new InjectedCrash()));
        AtomicFile.Write(path, [5], 2);
        Assert.Equal([5], File.ReadAllBytes(path));
        Assert.Equal([3], File.ReadAllBytes(AtomicFile.BackupPath(path, 1)));
    }

    private sealed class InjectedCrash : Exception;
}
