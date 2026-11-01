using System.Buffers.Binary;
using Colony.Fields;
using Colony.Hashing;

namespace Colony.Pathfinding;

/// <summary>
/// The canonical request queue (spec section 9.1). Pending requests are kept sorted by (priority, id); intake
/// (End Phase) appends the Tick's requests with sequential ids, and the Begin-Phase deliver System first runs
/// searches in that order until the work budget is spent (a request is started only while the budget has room,
/// so at least one runs per Tick) and then emits every result due this Tick, a request not yet computed as
/// <see cref="PathStatus.DeadlineMissed"/>. Everything here is a function of the requests, the Tick and the
/// Fields, never of the worker count. The pending set (with computed results) is saved and hashed, so a loaded
/// World continues exactly; the delivery arena is scratch.
/// </summary>
internal sealed class PathRequestQueue
{
    internal const string SectionName = "requests";
    private const int PendingBytes = 8 + 8 + 8 + 12 + 12 + 4 + 1 + 4 + 1 + 1 + 4 + 12 + 8 + 4;

    private struct Pending
    {
        public long Id;
        public long Due;
        public PathRequest Request;
        public bool Computed;
        public PathStatus Status;
        public int Cost;
        public ChunkCoord Missing;
        public long WorkUnits;
        public int Start;
        public int Count;
    }

    private readonly PathfindingService _service;
    private Pending[] _pending = new Pending[64];
    private int _count;
    private TilePos[] _store = new TilePos[1024];
    private int _storeUsed;
    private TilePos[] _delivered = new TilePos[1024];
    private int _deliveredUsed;
    private PathResult[] _results = new PathResult[64];
    private int _resultCount;
    private long _nextId;
    private long _submitted;
    private long _computed;
    private long _deliveredCount;
    private long _deferred;
    private long _missed;
    private long _workUnits;

    public PathRequestQueue(PathfindingService service)
    {
        _service = service;
    }

    public int Count => _count;

    public PathQueueCounters Counters => new(_submitted, _computed, _deliveredCount, _deferred, _missed, _workUnits);

    /// <summary>Work units the searches of the last <see cref="Compute"/> spent.</summary>
    public long WorkUnitsThisTick { get; private set; }

    /// <summary>The results emitted at the last delivery, in emission order.</summary>
    public ReadOnlySpan<PathResult> Delivered => _results.AsSpan(0, _resultCount);

    public ReadOnlySpan<TilePos> Waypoints(in PathResult result)
    {
        if ((uint)result.WaypointOffset > (uint)_deliveredUsed || (uint)result.WaypointCount > (uint)(_deliveredUsed - result.WaypointOffset))
        {
            throw new ArgumentException("The result does not belong to the last delivery; waypoints are valid only during the Tick they were delivered in.", nameof(result));
        }

        return _delivered.AsSpan(result.WaypointOffset, result.WaypointCount);
    }

    /// <summary>End Phase: give the Tick's requests ids and file them in (priority, id) order.</summary>
    public void Intake(ReadOnlySpan<PathRequest> requests, long tick)
    {
        long due = tick + _service.Options.ResultDelayTicks;
        foreach (ref readonly var request in requests)
        {
            if (_count == _pending.Length)
            {
                Array.Resize(ref _pending, _pending.Length * 2);
            }

            // Ids rise with time, so a new request goes after every pending one of its priority or a more urgent one.
            int at = _count;
            while (at > 0 && _pending[at - 1].Request.Priority > request.Priority)
            {
                at--;
            }

            Array.Copy(_pending, at, _pending, at + 1, _count - at);
            _pending[at] = new Pending { Id = _nextId++, Due = due, Request = request };
            _count++;
            _submitted++;
        }
    }

    /// <summary>Begin Phase, first half: run pending searches in canonical order while the budget has room.</summary>
    public void Compute()
    {
        long budget = _service.Options.WorkBudgetPerTick;
        long used = 0;
        WorkUnitsThisTick = 0;
        lock (_service.SearchLock)
        {
            var search = _service.Search;
            for (int i = 0; i < _count; i++)
            {
                ref var p = ref _pending[i];
                if (p.Computed)
                {
                    continue;
                }

                if (used >= budget)
                {
                    _deferred++;
                    continue;
                }

                var map = _service.Regions(p.Request.MovementClass);
                p.Status = search.Run(map, p.Request.From, p.Request.To, _service.ResidencyFunc);
                p.Computed = true;
                p.Cost = p.Status == PathStatus.Found ? search.Cost : 0;
                p.Missing = search.MissingChunk;
                p.WorkUnits = search.WorkUnits;
                used += search.WorkUnits;
                _workUnits += search.WorkUnits;
                WorkUnitsThisTick += search.WorkUnits;
                _computed++;
                if (p.Status == PathStatus.Found)
                {
                    var waypoints = search.Waypoints;
                    EnsureStore(_storeUsed + waypoints.Length);
                    waypoints.CopyTo(_store.AsSpan(_storeUsed));
                    p.Start = _storeUsed;
                    p.Count = waypoints.Length;
                    _storeUsed += waypoints.Length;
                }
            }
        }
    }

    /// <summary>Begin Phase, second half: emit every result due this Tick (canonical order) and drop it from the queue.</summary>
    public void Deliver(in SystemContext context, ResourceType resultQueue, long tick)
    {
        _deliveredUsed = 0;
        _resultCount = 0;
        int keep = 0;
        for (int i = 0; i < _count; i++)
        {
            ref var p = ref _pending[i];
            if (p.Due > tick)
            {
                if (keep != i)
                {
                    _pending[keep] = p;
                }

                keep++;
                continue;
            }

            PathResult result;
            if (!p.Computed)
            {
                _missed++;
                result = new PathResult(p.Id, p.Request.Requester, p.Request.Tag, PathStatus.DeadlineMissed, 0, _deliveredUsed, 0, default, 0);
            }
            else
            {
                if (_delivered.Length < _deliveredUsed + p.Count)
                {
                    Array.Resize(ref _delivered, Math.Max(_delivered.Length * 2, _deliveredUsed + p.Count));
                }

                _store.AsSpan(p.Start, p.Count).CopyTo(_delivered.AsSpan(_deliveredUsed));
                result = new PathResult(p.Id, p.Request.Requester, p.Request.Tag, p.Status, p.Cost, _deliveredUsed, p.Count, p.Missing, p.WorkUnits);
                _deliveredUsed += p.Count;
            }

            if (_resultCount == _results.Length)
            {
                Array.Resize(ref _results, _results.Length * 2);
            }

            _results[_resultCount++] = result;
            _deliveredCount++;
            context.Emit(resultQueue, in result);
        }

        _count = keep;
        CompactStore();
    }

    /// <summary>Whether the queue has ever taken a request in; an untouched queue adds nothing to the hash or the save.</summary>
    public bool IsUsed => _nextId != 0 || _count != 0;

    public void Hash(ref Hash64State state)
    {
        if (!IsUsed)
        {
            return;
        }

        state.Append(in _nextId);
        state.Append(in _count);
        for (int i = 0; i < _count; i++)
        {
            ref readonly var p = ref _pending[i];
            state.Append(in p.Id);
            state.Append(in p.Due);
            AppendRequest(ref state, in p.Request);
            state.Append(p.Computed ? (byte)1 : (byte)0);
            state.Append((byte)p.Status);
            state.Append(in p.Cost);
            state.Append(in p.Count);
        }
    }

    public void Save(ExtensionStateWriter writer)
    {
        if (!IsUsed)
        {
            return;
        }

        int bytes = 8 + 4;
        for (int i = 0; i < _count; i++)
        {
            bytes += PendingBytes + (12 * _pending[i].Count);
        }

        var buffer = new byte[bytes];
        var span = buffer.AsSpan();
        BinaryPrimitives.WriteInt64LittleEndian(span, _nextId);
        BinaryPrimitives.WriteInt32LittleEndian(span[8..], _count);
        int at = 12;
        for (int i = 0; i < _count; i++)
        {
            ref readonly var p = ref _pending[i];
            WriteInt64(span, ref at, p.Id);
            WriteInt64(span, ref at, p.Due);
            WriteInt64(span, ref at, (long)p.Request.Requester.Value);
            WriteTile(span, ref at, p.Request.From);
            WriteTile(span, ref at, p.Request.To);
            WriteInt32(span, ref at, p.Request.MovementClass.Id);
            span[at++] = p.Request.Priority;
            WriteInt32(span, ref at, (int)p.Request.Tag);
            span[at++] = p.Computed ? (byte)1 : (byte)0;
            span[at++] = (byte)p.Status;
            WriteInt32(span, ref at, p.Cost);
            WriteInt32(span, ref at, p.Missing.X);
            WriteInt32(span, ref at, p.Missing.Y);
            WriteInt32(span, ref at, p.Missing.Z);
            WriteInt64(span, ref at, p.WorkUnits);
            WriteInt32(span, ref at, p.Count);
            for (int w = 0; w < p.Count; w++)
            {
                WriteTile(span, ref at, _store[p.Start + w]);
            }
        }

        writer.WriteSection(SectionName, span[..at]);
    }

    public void Load(ReadOnlySpan<byte> payload, string extensionName)
    {
        if (payload.Length < 12)
        {
            throw new InvalidDataException($"Extension '{extensionName}': section '{SectionName}' is truncated.");
        }

        _nextId = BinaryPrimitives.ReadInt64LittleEndian(payload);
        int count = BinaryPrimitives.ReadInt32LittleEndian(payload[8..]);
        if (count < 0)
        {
            throw new InvalidDataException($"Extension '{extensionName}': section '{SectionName}' holds a negative request count.");
        }

        if (_pending.Length < count)
        {
            _pending = new Pending[count];
        }

        _count = count;
        _storeUsed = 0;
        _deliveredUsed = 0;
        _resultCount = 0;
        int at = 12;
        for (int i = 0; i < count; i++)
        {
            if (payload.Length - at < PendingBytes)
            {
                throw new InvalidDataException($"Extension '{extensionName}': section '{SectionName}' is truncated at request {i}.");
            }

            ref var p = ref _pending[i];
            p.Id = ReadInt64(payload, ref at);
            p.Due = ReadInt64(payload, ref at);
            var requester = new EntityId((ulong)ReadInt64(payload, ref at));
            var from = ReadTile(payload, ref at);
            var to = ReadTile(payload, ref at);
            var movementClass = new MovementClass(ReadInt32(payload, ref at));
            byte priority = payload[at++];
            uint tag = (uint)ReadInt32(payload, ref at);
            p.Request = new PathRequest(requester, from, to, movementClass, priority, tag);
            p.Computed = payload[at++] != 0;
            p.Status = (PathStatus)payload[at++];
            p.Cost = ReadInt32(payload, ref at);
            p.Missing = new ChunkCoord(ReadInt32(payload, ref at), ReadInt32(payload, ref at), ReadInt32(payload, ref at));
            p.WorkUnits = ReadInt64(payload, ref at);
            p.Count = ReadInt32(payload, ref at);
            if (p.Count < 0 || payload.Length - at < 12 * p.Count)
            {
                throw new InvalidDataException($"Extension '{extensionName}': section '{SectionName}' is truncated in the waypoints of request {i}.");
            }

            EnsureStore(_storeUsed + p.Count);
            p.Start = _storeUsed;
            for (int w = 0; w < p.Count; w++)
            {
                _store[_storeUsed++] = ReadTile(payload, ref at);
            }
        }

        if (at != payload.Length)
        {
            throw new InvalidDataException($"Extension '{extensionName}': section '{SectionName}' has {payload.Length - at} trailing bytes.");
        }
    }

    private static void AppendRequest(ref Hash64State state, in PathRequest request)
    {
        ulong requester = request.Requester.Value;
        state.Append(in requester);
        state.Append(request.From.X);
        state.Append(request.From.Y);
        state.Append(request.From.Z);
        state.Append(request.To.X);
        state.Append(request.To.Y);
        state.Append(request.To.Z);
        state.Append(request.MovementClass.Id);
        state.Append(request.Priority);
        state.Append(request.Tag);
    }

    private void EnsureStore(int needed)
    {
        if (_store.Length < needed)
        {
            Array.Resize(ref _store, Math.Max(_store.Length * 2, needed));
        }
    }

    /// <summary>Packs the waypoints of the kept requests to the front of the store, in queue order.</summary>
    private void CompactStore()
    {
        int used = 0;
        for (int i = 0; i < _count; i++)
        {
            ref var p = ref _pending[i];
            if (p.Count == 0)
            {
                continue;
            }

            if (p.Start != used)
            {
                Array.Copy(_store, p.Start, _store, used, p.Count);
                p.Start = used;
            }

            used += p.Count;
        }

        _storeUsed = used;
    }

    private static void WriteInt64(Span<byte> span, ref int at, long value)
    {
        BinaryPrimitives.WriteInt64LittleEndian(span[at..], value);
        at += 8;
    }

    private static void WriteInt32(Span<byte> span, ref int at, int value)
    {
        BinaryPrimitives.WriteInt32LittleEndian(span[at..], value);
        at += 4;
    }

    private static void WriteTile(Span<byte> span, ref int at, TilePos tile)
    {
        WriteInt32(span, ref at, tile.X);
        WriteInt32(span, ref at, tile.Y);
        WriteInt32(span, ref at, tile.Z);
    }

    private static long ReadInt64(ReadOnlySpan<byte> span, ref int at)
    {
        long value = BinaryPrimitives.ReadInt64LittleEndian(span[at..]);
        at += 8;
        return value;
    }

    private static int ReadInt32(ReadOnlySpan<byte> span, ref int at)
    {
        int value = BinaryPrimitives.ReadInt32LittleEndian(span[at..]);
        at += 4;
        return value;
    }

    private static TilePos ReadTile(ReadOnlySpan<byte> span, ref int at)
    {
        int x = ReadInt32(span, ref at);
        int y = ReadInt32(span, ref at);
        int z = ReadInt32(span, ref at);
        return new TilePos(x, y, z);
    }
}
