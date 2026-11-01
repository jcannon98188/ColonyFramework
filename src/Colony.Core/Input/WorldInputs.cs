using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Colony.Numerics;

namespace Colony;

/// <summary>
/// Input Command intake and the canonical input list of a World (spec section 7.3). <see cref="Submit{T}(InputSource, in T, long)"/>
/// may be called from any thread, during or between Ticks; each command is stamped with an effective
/// Tick (the next Tick boundary by default) and a per-source sequence. When a Tick starts, every command
/// due for it is moved into the canonical list sorted by (effective Tick, source, sequence), which handler
/// Systems read through <see cref="SystemContext.Inputs{T}"/> during that Tick and the host reads through
/// <see cref="Current"/> after it (the replay log records exactly this list). The list is the same whatever
/// the submission interleaving or thread count, because the sort key is complete. Pending and canonical
/// inputs are host-side state: neither saved nor hashed; the replay log is their persistent form.
/// </summary>
public sealed class WorldInputs
{
    /// <summary>Stream key of every Input Command's random source ("input").</summary>
    private const string RandomKey = "input";

    private readonly World _world;
    private readonly WorldSchema _schema;
    private readonly Lock _lock = new();
    private readonly Dictionary<uint, uint> _nextSequence = [];
    private Entry[] _pending = new Entry[16];
    private int _pendingCount;
    private byte[] _pendingBytes = new byte[256];
    private int _pendingBytesUsed;
    private byte[] _compactBytes = new byte[256];
    private Entry[] _current = new Entry[16];
    private int _currentCount;
    private byte[] _currentBytes = new byte[256];
    private long _currentTick = -1;
    private long _nextTick;

    internal WorldInputs(World world)
    {
        _world = world;
        _schema = world.Schema;
    }

    /// <summary>The earliest Tick that still accepts commands: the default effective Tick of a submission.</summary>
    public long NextTick
    {
        get
        {
            lock (_lock)
            {
                return OpenTick();
            }
        }
    }

    /// <summary>Commands submitted and not yet moved into a canonical list.</summary>
    public int PendingCount
    {
        get
        {
            lock (_lock)
            {
                return _pendingCount;
            }
        }
    }

    /// <summary>
    /// The canonical list of the last Tick that started: valid from the start of that Tick until the next
    /// Tick starts, so the host records it after <see cref="World.Tick"/> returns. Empty with Tick -1 before the first Tick.
    /// </summary>
    public CanonicalInputs Current => new(this);

    /// <summary>
    /// Submits a command from <paramref name="source"/> for <paramref name="effectiveTick"/> (-1 = the next Tick
    /// boundary) with the source's next sequence number, which is returned. Thread safe. Throws when the
    /// Tick has already started.
    /// </summary>
    public uint Submit<T>(InputSource source, in T command, long effectiveTick = -1) where T : unmanaged
    {
        int type = _schema.InputCommand<T>().Id;
        lock (_lock)
        {
            uint sequence = _nextSequence.TryGetValue(source.Value, out uint next) ? next : 0u;
            SubmitLocked(source.Value, sequence, type, MemoryMarshal.AsBytes(new ReadOnlySpan<T>(in command)), effectiveTick);
            return sequence;
        }
    }

    /// <summary>
    /// Submits a command with an explicit sequence (a remote peer's command, a replayed one). Sequences of
    /// one source must increase: a sequence at or below one already submitted throws.
    /// </summary>
    public void Submit<T>(InputSource source, uint sequence, in T command, long effectiveTick) where T : unmanaged
    {
        int type = _schema.InputCommand<T>().Id;
        lock (_lock)
        {
            SubmitLocked(source.Value, sequence, type, MemoryMarshal.AsBytes(new ReadOnlySpan<T>(in command)), effectiveTick);
        }
    }

    /// <summary>
    /// Submits a command from its wire bytes (spec section 7.4): the payload must be exactly the registered
    /// size of <paramref name="command"/>. This is the ingestion path of the replay log and of lockstep networking.
    /// </summary>
    public void Submit(InputSource source, uint sequence, InputCommandType command, ReadOnlySpan<byte> payload, long effectiveTick)
    {
        var descriptor = _schema.DescribeInputCommand(command);
        if (payload.Length != descriptor.Size)
        {
            throw new ArgumentException($"Input Command '{descriptor.Name}' is {descriptor.Size} bytes; the payload has {payload.Length}.", nameof(payload));
        }

        lock (_lock)
        {
            SubmitLocked(source.Value, sequence, command.Id, payload, effectiveTick);
        }
    }

    private void SubmitLocked(uint source, uint sequence, int type, ReadOnlySpan<byte> payload, long effectiveTick)
    {
        long open = OpenTick();
        if (effectiveTick < 0)
        {
            effectiveTick = open;
        }
        else if (effectiveTick < open)
        {
            throw new ArgumentOutOfRangeException(nameof(effectiveTick), effectiveTick, $"Tick {effectiveTick} has already started; the earliest Tick still accepting input is {open}.");
        }

        if (_nextSequence.TryGetValue(source, out uint next) && sequence < next)
        {
            throw new ArgumentException($"Source {source} already submitted sequence {next - 1}; sequences must increase (got {sequence}).", nameof(sequence));
        }

        _nextSequence[source] = sequence + 1;
        if (_pendingCount == _pending.Length)
        {
            Array.Resize(ref _pending, _pending.Length * 2);
        }

        if (_pendingBytesUsed + payload.Length > _pendingBytes.Length)
        {
            Array.Resize(ref _pendingBytes, Math.Max(_pendingBytes.Length * 2, _pendingBytesUsed + payload.Length));
        }

        payload.CopyTo(_pendingBytes.AsSpan(_pendingBytesUsed));
        _pending[_pendingCount++] = new Entry(effectiveTick, source, sequence, type, _pendingBytesUsed, payload.Length);
        _pendingBytesUsed += payload.Length;
    }

    private long OpenTick() => Math.Max(_nextTick, _world.CurrentTick);

    /// <summary>
    /// Builds the canonical list for <paramref name="tick"/> (the Tick boundary, called by the runner before the
    /// first Phase): every pending command due at or before this Tick, sorted by (Tick, source, sequence),
    /// with payloads copied in that order. Later commands stay pending. Allocation free once grown.
    /// </summary>
    internal void BeginTick(long tick)
    {
        lock (_lock)
        {
            _currentCount = 0;
            int kept = 0;
            int keptBytes = 0;
            for (int i = 0; i < _pendingCount; i++)
            {
                ref var e = ref _pending[i];
                if (e.Tick <= tick)
                {
                    if (_currentCount == _current.Length)
                    {
                        Array.Resize(ref _current, _current.Length * 2);
                    }

                    _current[_currentCount++] = e;
                }
                else
                {
                    if (keptBytes + e.Length > _compactBytes.Length)
                    {
                        Array.Resize(ref _compactBytes, Math.Max(_compactBytes.Length * 2, keptBytes + e.Length));
                    }

                    _pendingBytes.AsSpan(e.Offset, e.Length).CopyTo(_compactBytes.AsSpan(keptBytes));
                    _pending[kept++] = new Entry(e.Tick, e.Source, e.Sequence, e.Type, keptBytes, e.Length);
                    keptBytes += e.Length;
                }
            }

            _pendingCount = kept;
            _pendingBytesUsed = keptBytes;
            (_pendingBytes, _compactBytes) = (_compactBytes, _pendingBytes);

            var list = _current.AsSpan(0, _currentCount);
            list.Sort(default(CanonicalOrder));
            int bytes = 0;
            for (int i = 0; i < list.Length; i++)
            {
                ref var e = ref list[i];
                if (bytes + e.Length > _currentBytes.Length)
                {
                    Array.Resize(ref _currentBytes, Math.Max(_currentBytes.Length * 2, bytes + e.Length));
                }

                _compactBytes.AsSpan(e.Offset, e.Length).CopyTo(_currentBytes.AsSpan(bytes));
                e.Offset = bytes;
                bytes += e.Length;
            }

            _currentTick = tick;
            _nextTick = tick + 1;
        }
    }

    internal long CurrentTick => _currentTick;

    internal int CurrentCount => _currentCount;

    internal ref readonly Entry RecordAt(int index)
    {
        if ((uint)index >= (uint)_currentCount)
        {
            throw new ArgumentOutOfRangeException(nameof(index), index, "The canonical input list has no such entry.");
        }

        return ref _current[index];
    }

    internal ReadOnlySpan<byte> PayloadAt(int index)
    {
        ref readonly var e = ref RecordAt(index);
        return _currentBytes.AsSpan(e.Offset, e.Length);
    }

    /// <summary>A handler's view of the current list filtered to <typeparamref name="T"/>.</summary>
    internal InputList<T> ListOf<T>(InputCommandType type) where T : unmanaged
    {
        var descriptor = _schema.DescribeInputCommand(type);
        if (Unsafe.SizeOf<T>() != descriptor.Size)
        {
            throw new ArgumentException($"{typeof(T).Name} is {Unsafe.SizeOf<T>()} bytes but Input Command '{descriptor.Name}' is {descriptor.Size} bytes.", nameof(T));
        }

        return new InputList<T>(this, type.Id, _world.Random.Derive(RandomKey));
    }

    /// <summary>The random stream of one command: (World seed, "input", source, sequence); the counter is the use index.</summary>
    internal static RandomSource RandomFor(RandomSource root, uint source, uint sequence) => root.Derive(source).Derive(sequence);

    /// <summary>The random stream handlers draw from for the command (<paramref name="source"/>, <paramref name="sequence"/>).</summary>
    public RandomSource RandomFor(InputSource source, uint sequence) => RandomFor(_world.Random.Derive(RandomKey), source.Value, sequence);

    /// <summary>
    /// Copies every pending command (submitted, not yet in a canonical list) as (effective Tick, source,
    /// sequence, Input Command id, payload), in (source, sequence) order: the order a fresh World can resubmit
    /// them in. Used by join-in-progress packages (TASK-33); host side, between Ticks.
    /// </summary>
    internal (long Tick, uint Source, uint Sequence, int Type, byte[] Payload)[] CopyPending()
    {
        lock (_lock)
        {
            var copy = new (long Tick, uint Source, uint Sequence, int Type, byte[] Payload)[_pendingCount];
            for (int i = 0; i < _pendingCount; i++)
            {
                ref var e = ref _pending[i];
                copy[i] = (e.Tick, e.Source, e.Sequence, e.Type, _pendingBytes.AsSpan(e.Offset, e.Length).ToArray());
            }

            Array.Sort(copy, static (a, b) => a.Source != b.Source ? a.Source.CompareTo(b.Source) : a.Sequence.CompareTo(b.Sequence));
            return copy;
        }
    }

    internal struct Entry
    {
        public long Tick;
        public uint Source;
        public uint Sequence;
        public int Type;
        public int Offset;
        public int Length;

        public Entry(long tick, uint source, uint sequence, int type, int offset, int length)
        {
            Tick = tick;
            Source = source;
            Sequence = sequence;
            Type = type;
            Offset = offset;
            Length = length;
        }
    }

    /// <summary>(effective Tick, source, sequence): the complete canonical order (spec section 7.3).</summary>
    private readonly struct CanonicalOrder : IComparer<Entry>
    {
        public int Compare(Entry x, Entry y)
        {
            int c = x.Tick.CompareTo(y.Tick);
            if (c != 0)
            {
                return c;
            }

            c = x.Source.CompareTo(y.Source);
            return c != 0 ? c : x.Sequence.CompareTo(y.Sequence);
        }
    }
}

/// <summary>
/// The canonical input list of the last Tick that started (<see cref="WorldInputs.Current"/>): records in
/// canonical order with their payload bytes. This is the per-Tick input frame the replay log stores and
/// lockstep peers exchange (spec section 7.4). Valid until the next Tick starts.
/// </summary>
public readonly ref struct CanonicalInputs
{
    private readonly WorldInputs _inputs;

    internal CanonicalInputs(WorldInputs inputs)
    {
        _inputs = inputs;
    }

    /// <summary>The Tick this list was built for (-1 before the first Tick).</summary>
    public long Tick => _inputs.CurrentTick;

    public int Count => _inputs.CurrentCount;

    public InputRecord this[int index]
    {
        get
        {
            ref readonly var e = ref _inputs.RecordAt(index);
            return new InputRecord(e.Tick, new InputSource(e.Source), e.Sequence, new InputCommandType(e.Type));
        }
    }

    /// <summary>The command bytes of entry <paramref name="index"/> (exactly the registered size).</summary>
    public ReadOnlySpan<byte> Payload(int index) => _inputs.PayloadAt(index);

    /// <summary>The command of entry <paramref name="index"/> as <typeparamref name="T"/>, which must have the registered size.</summary>
    public T Command<T>(int index) where T : unmanaged
    {
        var payload = _inputs.PayloadAt(index);
        if (payload.Length != Unsafe.SizeOf<T>())
        {
            throw new ArgumentException($"{typeof(T).Name} is {Unsafe.SizeOf<T>()} bytes but the command is {payload.Length} bytes.", nameof(T));
        }

        return Unsafe.ReadUnaligned<T>(in payload[0]);
    }
}
