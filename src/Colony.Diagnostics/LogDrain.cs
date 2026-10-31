using System.Globalization;

namespace Colony.Diagnostics;

/// <summary>
/// Resolves a World's structured log entries to text (spec section 10.1): Systems log message ids plus
/// numeric arguments into per-thread rings; the host drains them between Ticks and only then is the
/// registered template formatted. Lines carry the Tick, the System and the job, never wall-clock time, so
/// the same run always produces the same text on any thread count.
/// </summary>
public sealed class LogDrain
{
    private readonly World _world;
    private readonly LogEntry[] _buffer;

    public LogDrain(World world, int batchSize = 256)
    {
        ArgumentNullException.ThrowIfNull(world);
        _world = world;
        _buffer = new LogEntry[Math.Max(1, batchSize)];
    }

    /// <summary>Formats one entry's template with its arguments (invariant culture).</summary>
    public static string Format(WorldSchema schema, in LogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(schema);
        var templates = schema.LogMessages;
        if ((uint)entry.Message.Id >= (uint)templates.Length)
        {
            return string.Create(CultureInfo.InvariantCulture, $"<unregistered message {entry.Message.Id}> {entry.Arg0} {entry.Arg1} {entry.Arg2} {entry.Arg3}");
        }

        return string.Format(CultureInfo.InvariantCulture, templates[entry.Message.Id], entry.Arg0, entry.Arg1, entry.Arg2, entry.Arg3);
    }

    /// <summary>One line: <c>[tick N] System: text</c> (System <c>host</c> for entries logged outside Systems, job index appended for chunk jobs).</summary>
    public static string FormatLine(WorldSchema schema, in LogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(schema);
        string system = entry.SystemId >= 0 && entry.SystemId < schema.Systems.Length ? schema.Systems[entry.SystemId].Name : "host";
        string job = entry.JobIndex >= 0 ? string.Create(CultureInfo.InvariantCulture, $"#{entry.JobIndex}") : string.Empty;
        return string.Create(CultureInfo.InvariantCulture, $"[tick {entry.Tick}] {system}{job}: {Format(schema, in entry)}");
    }

    /// <summary>Drains every pending entry, calling <paramref name="sink"/> with each entry and its text, in (Tick, System, job) order. Returns the count. Call between Ticks.</summary>
    public int Drain(Action<LogEntry, string> sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        int total = 0;
        while (true)
        {
            int n = _world.Diagnostics.DrainLogs(_buffer);
            if (n == 0)
            {
                return total;
            }

            for (int i = 0; i < n; i++)
            {
                sink(_buffer[i], Format(_world.Schema, in _buffer[i]));
            }

            total += n;
        }
    }

    /// <summary>Drains every pending entry as formatted lines to <paramref name="writer"/>. Returns the count.</summary>
    public int Drain(TextWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        int total = 0;
        while (true)
        {
            int n = _world.Diagnostics.DrainLogs(_buffer);
            if (n == 0)
            {
                return total;
            }

            for (int i = 0; i < n; i++)
            {
                writer.WriteLine(FormatLine(_world.Schema, in _buffer[i]));
            }

            total += n;
        }
    }
}
