namespace Colony.Plugins.Native;

/// <summary>
/// A System declared through <c>colony_system_desc</c>: Core runs its hooks like any other System's; each hook
/// builds a <c>colony_context</c> (and a <c>colony_chunk</c> over the declared read columns) on the stack and calls
/// the C function pointer. A non-zero return faults the World like a thrown exception.
/// </summary>
internal sealed unsafe class NativeSystem
{
    private readonly delegate* unmanaged<void*, ColonyContext*, int> _begin;
    private readonly delegate* unmanaged<void*, ColonyContext*, ColonyChunk*, int> _forEachChunk;
    private readonly delegate* unmanaged<void*, ColonyContext*, int> _end;
    private readonly void* _userData;
    private readonly ComponentType[] _reads;

    internal NativeSystem(WorldBuilder builder, in ColonySystemDesc desc)
    {
        string name = Utf8Strings.ReadRequired(desc.Name, "colony_system_desc.name");
        string? phaseName = Utf8Strings.Read(desc.Phase);
        _begin = desc.Begin;
        _forEachChunk = desc.ForEachChunk;
        _end = desc.End;
        _userData = desc.UserData;
        _reads = Components(desc.Reads, desc.ReadsCount);

        Definition = new SystemDefinition
        {
            Name = name,
            Phase = phaseName is null ? Phase.Simulate : builder.PhaseNamed(phaseName),
            Reads = ComponentMask.Of(_reads),
            All = ComponentMask.Of(Components(desc.With, desc.WithCount)),
            None = ComponentMask.Of(Components(desc.Without, desc.WithoutCount)),
            ReadsResources = ResourceMask.Of(Resources(desc.ReadsResources, desc.ReadsResourcesCount)),
            WritesResources = ResourceMask.Of(Resources(desc.Emits, desc.EmitsCount)),
            After = Names(desc.After, desc.AfterCount),
            Before = Names(desc.Before, desc.BeforeCount),
            Every = desc.Every <= 0 ? 1 : desc.Every,
            Offset = desc.Offset,
            Begin = _begin == null ? null : Begin,
            ForEachChunk = _forEachChunk == null ? null : ForEachChunk,
            End = _end == null ? null : End,
        };
        Handle = NativeHandles.GetOrAdd(this);
    }

    public SystemDefinition Definition { get; }

    public nint Handle { get; }

    private void Begin(in SystemContext context)
    {
        var ctx = Context(in context);
        Check(_begin(_userData, &ctx), "begin");
    }

    private void End(in SystemContext context)
    {
        var ctx = Context(in context);
        Check(_end(_userData, &ctx), "end");
    }

    private void ForEachChunk(in SystemContext context, in ChunkView chunk)
    {
        var ctx = Context(in context);
        var columns = stackalloc ColonyColumn[Math.Max(_reads.Length, 1)];
        for (int i = 0; i < _reads.Length; i++)
        {
            columns[i].Component = _reads[i].Id;
            columns[i].Data = chunk.ColumnAddress(_reads[i], out columns[i].Stride);
        }

        var view = new ColonyChunk
        {
            Count = chunk.Count,
            Index = chunk.ChunkIndex,
            Entities = (ulong*)chunk.EntitiesAddress,
            ColumnCount = _reads.Length,
            Columns = columns,
        };
        Check(_forEachChunk(_userData, &ctx, &view), "for_each_chunk");
    }

    private ColonyContext Context(in SystemContext context) => new()
    {
        Tick = context.Tick,
        SystemId = context.SystemId,
        JobIndex = context.JobIndex,
        JobCount = context.JobCount,
        Random = new ColonyRandom { Seed = context.Random.Seed, Key = context.Random.Key },
        Commands = NativeHandles.GetOrAdd(context.Commands),
        World = NativeHandles.GetOrAdd(context.World),
        System = Handle,
    };

    private void Check(int status, string hook)
    {
        if (status != ColonyAbi.Ok)
        {
            throw new InvalidOperationException($"Native System '{Definition.Name}' returned status {status} from {hook}: {ColonyAbi.LastError}");
        }
    }

    private static ComponentType[] Components(int* ids, int count)
    {
        var result = count <= 0 ? [] : new ComponentType[count];
        for (int i = 0; i < result.Length; i++)
        {
            result[i] = new ComponentType(ids[i]);
        }

        return result;
    }

    private static ResourceType[] Resources(int* ids, int count)
    {
        var result = count <= 0 ? [] : new ResourceType[count];
        for (int i = 0; i < result.Length; i++)
        {
            result[i] = new ResourceType(ids[i]);
        }

        return result;
    }

    private static string[] Names(byte** names, int count)
    {
        var result = count <= 0 ? [] : new string[count];
        for (int i = 0; i < result.Length; i++)
        {
            result[i] = Utf8Strings.ReadRequired(names[i], "System name");
        }

        return result;
    }
}
