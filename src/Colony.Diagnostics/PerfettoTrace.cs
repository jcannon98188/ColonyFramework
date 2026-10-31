using System.Buffers;
using System.Text.Json;

namespace Colony.Diagnostics;

/// <summary>
/// Exports a <see cref="TraceCapture"/> as Chrome Trace Event JSON (<c>{"traceEvents": [...]}</c>), the
/// format the Perfetto UI (ui.perfetto.dev) and <c>chrome://tracing</c> open. One process holds a Tick lane,
/// a Phase lane, a System lane and one lane per worker. Times are microseconds; the captured Ticks are
/// laid back to back because records hold durations, not wall-clock starts (spec section 10.1: no
/// wall-clock time in the records).
/// </summary>
/// <remarks>
/// At <see cref="DiagnosticsLevel.Detailed"/> the worker lanes carry one slice per chunk job and a System's
/// slice spans its first job start to its last job end. At <see cref="DiagnosticsLevel.Standard"/> there is no
/// per-job trace: Systems are placed one after another inside their Phase in schedule order using their wall
/// time (an approximation, flagged with <c>args.approximate</c>) and each worker lane shows one busy-time slice
/// per Tick. Export runs on the host, off the Tick, so it may allocate; capture does not.
/// </remarks>
public static class PerfettoTrace
{
    public const int ProcessId = 1;

    public const int TickLane = 1;

    public const int PhaseLane = 2;

    public const int SystemLane = 3;

    private const int FirstWorkerLane = 100;

    private static readonly JsonWriterOptions WriterOptions = new() { SkipValidation = false };

    /// <summary>The lane (tid) of a worker; worker 0 is the Tick thread.</summary>
    public static int WorkerLane(int worker) => FirstWorkerLane + worker;

    /// <summary>Writes the trace to <paramref name="destination"/> as UTF-8 JSON and flushes it.</summary>
    public static void Write(TraceCapture capture, Stream destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        using var writer = new Utf8JsonWriter(destination, WriterOptions);
        Write(capture, writer);
        writer.Flush();
    }

    /// <summary>Writes the trace to a buffer writer as UTF-8 JSON.</summary>
    public static void Write(TraceCapture capture, IBufferWriter<byte> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        using var writer = new Utf8JsonWriter(destination, WriterOptions);
        Write(capture, writer);
        writer.Flush();
    }

    /// <summary>Writes the trace to a file (created or truncated).</summary>
    public static void WriteFile(TraceCapture capture, string path)
    {
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        Write(capture, stream);
    }

    private static void Write(TraceCapture capture, Utf8JsonWriter writer)
    {
        ArgumentNullException.ThrowIfNull(capture);
        var schema = capture.World.Schema;
        double microsPerTick = 1_000_000.0 / TickRecord.TicksPerSecond;

        writer.WriteStartObject();
        writer.WriteString("displayTimeUnit", "ms");
        writer.WritePropertyName("traceEvents");
        writer.WriteStartArray();

        WriteMetadata(writer, "process_name", 0, "Colony World");
        WriteLane(writer, TickLane, "Ticks", 0);
        WriteLane(writer, PhaseLane, "Phases", 1);
        WriteLane(writer, SystemLane, "Systems", 2);
        for (int w = 0; w < capture.WorkerCount; w++)
        {
            WriteLane(writer, WorkerLane(w), $"Worker {w}", 10 + w);
        }

        var systemStarts = new long[schema.Systems.Length];
        var systemEnds = new long[schema.Systems.Length];
        var order = ScheduleOrder(schema);
        long tickStart = 0;
        for (int i = 0; i < capture.Count; i++)
        {
            var tick = capture.Oldest(i);
            ref readonly var record = ref tick.Tick;
            long number = record.Tick;
            string tickName = $"Tick {number}";

            writer.WriteStartObject();
            writer.WriteString("ph", "i");
            writer.WriteString("s", "g");
            writer.WriteString("name", tickName);
            writer.WriteString("cat", "tick");
            writer.WriteNumber("pid", ProcessId);
            writer.WriteNumber("tid", TickLane);
            writer.WriteNumber("ts", tickStart * microsPerTick);
            writer.WriteEndObject();

            writer.WriteStartObject();
            WriteSliceHeader(writer, tickName, "tick", TickLane, tickStart, record.WallTicks, microsPerTick);
            writer.WriteStartObject("args");
            writer.WriteNumber("tick", number);
            writer.WriteNumber("jobs", record.Jobs);
            writer.WriteNumber("chunksVisited", record.ChunksVisited);
            writer.WriteNumber("chunksSkipped", record.ChunksSkipped);
            writer.WriteNumber("entitiesTouched", record.EntitiesTouched);
            writer.WriteNumber("commands", record.Commands);
            writer.WriteNumber("allocatedBytes", record.AllocatedBytes);
            writer.WriteNumber("entities", record.EntityCount);
            writer.WriteNumber("chunksInUse", record.ChunksInUse);
            writer.WriteBoolean("slow", record.Slow);
            writer.WriteEndObject();
            writer.WriteEndObject();

            // Phases run one after another on the Tick thread; their starts are the running sum of wall times.
            bool detailed = tick.Jobs.Length > 0;
            Array.Fill(systemStarts, long.MaxValue);
            Array.Fill(systemEnds, long.MinValue);
            foreach (ref readonly var job in tick.Jobs)
            {
                if ((uint)job.SystemId < (uint)systemStarts.Length)
                {
                    systemStarts[job.SystemId] = Math.Min(systemStarts[job.SystemId], job.StartTicks);
                    systemEnds[job.SystemId] = Math.Max(systemEnds[job.SystemId], job.EndTicks);
                }
            }

            long phaseStart = 0;
            for (int p = 0; p < tick.Phases.Length; p++)
            {
                ref readonly var phase = ref tick.Phases[p];
                writer.WriteStartObject();
                WriteSliceHeader(writer, schema.Phases[p].Name, "phase", PhaseLane, tickStart + phaseStart, phase.WallTicks, microsPerTick);
                writer.WriteStartObject("args");
                writer.WriteNumber("tick", number);
                writer.WriteNumber("waves", phase.Waves);
                writer.WriteNumber("jobs", phase.Jobs);
                writer.WriteNumber("commands", phase.Commands);
                writer.WriteNumber("playbackMicros", phase.PlaybackTicks * microsPerTick);
                writer.WriteEndObject();
                writer.WriteEndObject();

                long cursor = phaseStart;
                foreach (int s in order)
                {
                    ref readonly var descriptor = ref schema.Systems[s];
                    if (descriptor.Phase.Id != p)
                    {
                        continue;
                    }

                    ref readonly var system = ref tick.Systems[s];
                    if (!system.Ran)
                    {
                        continue;
                    }

                    long start;
                    long duration;
                    bool approximate;
                    if (detailed && systemStarts[s] != long.MaxValue)
                    {
                        start = systemStarts[s];
                        duration = systemEnds[s] - systemStarts[s];
                        approximate = false;
                    }
                    else
                    {
                        start = cursor;
                        duration = system.WallTicks;
                        cursor += duration;
                        approximate = true;
                    }

                    writer.WriteStartObject();
                    WriteSliceHeader(writer, descriptor.Name, "system", SystemLane, tickStart + start, duration, microsPerTick);
                    writer.WriteStartObject("args");
                    writer.WriteNumber("tick", number);
                    writer.WriteNumber("jobs", system.Jobs);
                    writer.WriteNumber("chunksVisited", system.ChunksVisited);
                    writer.WriteNumber("chunksSkipped", system.ChunksSkipped);
                    writer.WriteNumber("entitiesTouched", system.EntitiesTouched);
                    writer.WriteNumber("commands", system.Commands);
                    writer.WriteNumber("cpuMicros", system.CpuTicks * microsPerTick);
                    writer.WriteNumber("allocatedBytes", system.AllocatedBytes);
                    writer.WriteBoolean("approximate", approximate);
                    writer.WriteEndObject();
                    writer.WriteEndObject();
                }

                phaseStart += phase.WallTicks;
            }

            if (detailed)
            {
                foreach (ref readonly var job in tick.Jobs)
                {
                    string name = (uint)job.SystemId < (uint)schema.Systems.Length ? schema.Systems[job.SystemId].Name : "?";
                    writer.WriteStartObject();
                    WriteSliceHeader(writer, name, "job", WorkerLane(job.Worker), tickStart + job.StartTicks, job.EndTicks - job.StartTicks, microsPerTick);
                    writer.WriteStartObject("args");
                    writer.WriteNumber("tick", number);
                    writer.WriteNumber("system", job.SystemId);
                    writer.WriteNumber("archetype", job.Archetype);
                    writer.WriteNumber("chunk", job.Chunk);
                    writer.WriteNumber("job", job.JobIndex);
                    writer.WriteEndObject();
                    writer.WriteEndObject();
                }
            }
            else
            {
                foreach (ref readonly var worker in tick.Workers)
                {
                    writer.WriteStartObject();
                    WriteSliceHeader(writer, "busy", "worker", WorkerLane(worker.Worker), tickStart, worker.BusyTicks, microsPerTick);
                    writer.WriteStartObject("args");
                    writer.WriteNumber("tick", number);
                    writer.WriteNumber("jobs", worker.Jobs);
                    writer.WriteNumber("spinMicros", worker.SpinTicks * microsPerTick);
                    writer.WriteNumber("parkMicros", worker.ParkTicks * microsPerTick);
                    writer.WriteNumber("allocatedBytes", worker.AllocatedBytes);
                    writer.WriteEndObject();
                    writer.WriteEndObject();
                }
            }

            tickStart += Math.Max(record.WallTicks, 1);
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    /// <summary>System ids in the order the scheduler runs them: Phase, wave, then order.</summary>
    private static int[] ScheduleOrder(WorldSchema schema)
    {
        var ids = new int[schema.Systems.Length];
        var keys = new long[ids.Length];
        for (int i = 0; i < ids.Length; i++)
        {
            ref readonly var d = ref schema.Systems[i];
            ids[i] = d.Id;
            keys[i] = ((long)d.Phase.Id << 42) | ((long)d.Wave << 21) | (uint)d.Order;
        }

        Array.Sort(keys, ids);
        return ids;
    }

    private static void WriteSliceHeader(Utf8JsonWriter writer, string name, string category, int lane, long startTicks, long durationTicks, double microsPerTick)
    {
        writer.WriteString("ph", "X");
        writer.WriteString("name", name);
        writer.WriteString("cat", category);
        writer.WriteNumber("pid", ProcessId);
        writer.WriteNumber("tid", lane);
        writer.WriteNumber("ts", startTicks * microsPerTick);
        writer.WriteNumber("dur", Math.Max(durationTicks, 0) * microsPerTick);
    }

    private static void WriteLane(Utf8JsonWriter writer, int lane, string name, int sortIndex)
    {
        WriteMetadata(writer, "thread_name", lane, name);
        writer.WriteStartObject();
        writer.WriteString("ph", "M");
        writer.WriteString("name", "thread_sort_index");
        writer.WriteNumber("pid", ProcessId);
        writer.WriteNumber("tid", lane);
        writer.WriteStartObject("args");
        writer.WriteNumber("sort_index", sortIndex);
        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    private static void WriteMetadata(Utf8JsonWriter writer, string kind, int lane, string name)
    {
        writer.WriteStartObject();
        writer.WriteString("ph", "M");
        writer.WriteString("name", kind);
        writer.WriteNumber("pid", ProcessId);
        writer.WriteNumber("tid", lane);
        writer.WriteStartObject("args");
        writer.WriteString("name", name);
        writer.WriteEndObject();
        writer.WriteEndObject();
    }
}
