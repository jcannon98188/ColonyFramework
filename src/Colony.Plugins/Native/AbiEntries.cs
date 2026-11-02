using System.Collections.Immutable;
using System.Runtime.InteropServices;
using Colony.Numerics;
using Colony.Persistence;

namespace Colony.Plugins.Native;

/// <summary>
/// The implementations behind the function table, one <c>[UnmanagedCallersOnly]</c> method per entry of
/// <see cref="AbiDefinition.Entries"/>. Every entry returns a status (<see cref="ColonyAbi.Ok"/> or an error code)
/// and never lets an exception escape; results travel through out-pointers. Entries a System may call from a chunk
/// job (commands, context, random) allocate nothing after the first call.
/// </summary>
internal static unsafe class AbiEntries
{
    private const int MaxPhaseNameBytes = 64;

    // ---- host ----

    [UnmanagedCallersOnly]
    public static int HostCreate(nint* outHost)
    {
        try
        {
            if (outHost == null)
            {
                return ColonyAbi.Invalid("out_host is null.");
            }

            *outHost = NativeHandles.GetOrAdd(new NativeHost());
            return ColonyAbi.Ok;
        }
        catch (Exception e)
        {
            return ColonyAbi.Fail(e);
        }
    }

    [UnmanagedCallersOnly]
    public static int HostAddPlugin(nint host, ColonyPluginDesc* plugin)
    {
        try
        {
            if (plugin == null)
            {
                return ColonyAbi.Invalid("plugin is null.");
            }

            Host(host).Plugins.Add(new NativePlugin(in *plugin));
            return ColonyAbi.Ok;
        }
        catch (Exception e)
        {
            return ColonyAbi.Fail(e);
        }
    }

    [UnmanagedCallersOnly]
    public static int HostBuild(nint host, nint* outSchema)
    {
        try
        {
            if (outSchema == null)
            {
                return ColonyAbi.Invalid("out_schema is null.");
            }

            var native = Host(host);
            var schema = native.Plugins.Build(native.Builder);
            *outSchema = NativeHandles.GetOrAdd(new NativeSchema(schema, native.Plugins.Definitions));
            return ColonyAbi.Ok;
        }
        catch (Exception e)
        {
            return ColonyAbi.Fail(e);
        }
    }

    [UnmanagedCallersOnly]
    public static int HostDestroy(nint host)
    {
        try
        {
            NativeHandles.Release(Host(host));
            return ColonyAbi.Ok;
        }
        catch (Exception e)
        {
            return ColonyAbi.Fail(e);
        }
    }

    // ---- registration (inside a Native Plugin's register callback) ----

    [UnmanagedCallersOnly]
    public static int RegisterComponent(nint registration, byte* name, int size, int alignment, uint flags, int* outId)
    {
        try
        {
            var r = Registration(registration);
            var options = (flags & AbiDefinition.ComponentNonFragmenting) != 0 ? ComponentOptions.NonFragmenting : ComponentOptions.None;
            var type = r.Registration.Builder.AddComponentLayout(Utf8Strings.ReadRequired(name, "name"), size, alignment, options);
            return Out(outId, type.Id);
        }
        catch (Exception e)
        {
            return ColonyAbi.Fail(e);
        }
    }

    [UnmanagedCallersOnly]
    public static int RegisterComponentData(nint registration, int component, ColonyComponentRange* ranges, int count)
    {
        try
        {
            var r = Registration(registration);
            if (count < 0 || (count > 0 && ranges == null))
            {
                return ColonyAbi.Invalid("ranges is null or count is negative.");
            }

            var list = new (int Offset, int Size)[count];
            for (int i = 0; i < count; i++)
            {
                list[i] = (ranges[i].Offset, ranges[i].Size);
            }

            r.Registration.Builder.DeclareComponentData(new ComponentType(component), list);
            return ColonyAbi.Ok;
        }
        catch (Exception e)
        {
            return ColonyAbi.Fail(e);
        }
    }

    [UnmanagedCallersOnly]
    public static int RegisterResource(nint registration, byte* name, int size, int alignment, uint flags, int* outId)
    {
        try
        {
            var r = Registration(registration);
            bool persistent = (flags & AbiDefinition.ResourceTransient) == 0;
            var type = r.Registration.Builder.AddResourceLayout(Utf8Strings.ReadRequired(name, "name"), size, alignment, ResourceKind.Value, persistent);
            return Out(outId, type.Id);
        }
        catch (Exception e)
        {
            return ColonyAbi.Fail(e);
        }
    }

    [UnmanagedCallersOnly]
    public static int RegisterEvent(nint registration, byte* name, int size, int alignment, uint flags, int* outId)
    {
        try
        {
            var r = Registration(registration);
            bool persistent = (flags & AbiDefinition.EventPersistent) != 0;
            var type = r.Registration.Builder.AddResourceLayout(Utf8Strings.ReadRequired(name, "name"), size, alignment, ResourceKind.EventQueue, persistent);
            return Out(outId, type.Id);
        }
        catch (Exception e)
        {
            return ColonyAbi.Fail(e);
        }
    }

    [UnmanagedCallersOnly]
    public static int RegisterInput(nint registration, byte* name, int size, int alignment, int* outId)
    {
        try
        {
            var r = Registration(registration);
            var type = r.Registration.Builder.AddInputCommandLayout(Utf8Strings.ReadRequired(name, "name"), size, alignment);
            return Out(outId, type.Id);
        }
        catch (Exception e)
        {
            return ColonyAbi.Fail(e);
        }
    }

    [UnmanagedCallersOnly]
    public static int RegisterDefinitionType(nint registration, byte* typeName, int size, int alignment, ColonyDefinitionField* fields, int fieldCount)
    {
        try
        {
            var r = Registration(registration);
            if (fieldCount > 0 && fields == null)
            {
                return ColonyAbi.Invalid("fields is null.");
            }

            var list = ImmutableArray.CreateBuilder<RawDefinitionField>(Math.Max(fieldCount, 0));
            for (int i = 0; i < fieldCount; i++)
            {
                list.Add(new RawDefinitionField(Utf8Strings.ReadRequired(fields[i].Name, "field name"), (RawFieldKind)fields[i].Kind, fields[i].Offset));
            }

            r.Registration.Definitions.AddRawType(Utf8Strings.ReadRequired(typeName, "type_name"), size, alignment, list.ToImmutable());
            return ColonyAbi.Ok;
        }
        catch (Exception e)
        {
            return ColonyAbi.Fail(e);
        }
    }

    [UnmanagedCallersOnly]
    public static int RegisterDefinitionSource(nint registration, byte* json, int length, byte* origin)
    {
        try
        {
            var r = Registration(registration);
            if (json == null || length < 0)
            {
                return ColonyAbi.Invalid("json is null or has a negative length.");
            }

            var bytes = new ReadOnlySpan<byte>(json, length).ToArray();
            r.Registration.Definitions.AddSource(JsonDefinitionSource.FromUtf8(bytes, Utf8Strings.Read(origin) ?? "native json"));
            return ColonyAbi.Ok;
        }
        catch (Exception e)
        {
            return ColonyAbi.Fail(e);
        }
    }

    [UnmanagedCallersOnly]
    public static int RegisterSystem(nint registration, ColonySystemDesc* system)
    {
        try
        {
            var r = Registration(registration);
            if (system == null)
            {
                return ColonyAbi.Invalid("system is null.");
            }

            var native = new NativeSystem(r.Registration.Builder, in *system);
            r.Registration.AddSystem(native.Definition);
            return ColonyAbi.Ok;
        }
        catch (Exception e)
        {
            return ColonyAbi.Fail(e);
        }
    }

    [UnmanagedCallersOnly]
    public static int RegisterFindComponent(nint registration, byte* name, int* outId)
    {
        try
        {
            var builder = Registration(registration).Registration.Builder;
            string text = Utf8Strings.ReadRequired(name, "name");
            try
            {
                return Out(outId, builder.Component(text).Id);
            }
            catch (ArgumentException)
            {
                return NotFound(outId);
            }
        }
        catch (Exception e)
        {
            return ColonyAbi.Fail(e);
        }
    }

    [UnmanagedCallersOnly]
    public static int RegisterFindResource(nint registration, byte* name, int* outId)
    {
        try
        {
            var builder = Registration(registration).Registration.Builder;
            string text = Utf8Strings.ReadRequired(name, "name");
            try
            {
                return Out(outId, builder.Resource(text).Id);
            }
            catch (ArgumentException)
            {
                return NotFound(outId);
            }
        }
        catch (Exception e)
        {
            return ColonyAbi.Fail(e);
        }
    }

    [UnmanagedCallersOnly]
    public static int RegisterFindInput(nint registration, byte* name, int* outId)
    {
        try
        {
            var builder = Registration(registration).Registration.Builder;
            string text = Utf8Strings.ReadRequired(name, "name");
            try
            {
                return Out(outId, builder.InputCommand(text).Id);
            }
            catch (ArgumentException)
            {
                return NotFound(outId);
            }
        }
        catch (Exception e)
        {
            return ColonyAbi.Fail(e);
        }
    }

    // ---- schema introspection ----

    [UnmanagedCallersOnly]
    public static int SchemaHash(nint schema, ulong* outHash)
    {
        try
        {
            return Out(outHash, Schema(schema).Schema.SchemaHash);
        }
        catch (Exception e)
        {
            return ColonyAbi.Fail(e);
        }
    }

    [UnmanagedCallersOnly]
    public static int SchemaComponentCount(nint schema, int* outCount)
    {
        try
        {
            return Out(outCount, Schema(schema).Schema.ComponentCount);
        }
        catch (Exception e)
        {
            return ColonyAbi.Fail(e);
        }
    }

    [UnmanagedCallersOnly]
    public static int SchemaComponent(nint schema, int index, ColonyTypeInfo* outInfo)
    {
        try
        {
            var native = Schema(schema);
            var components = native.Schema.Components;
            if ((uint)index >= (uint)components.Length)
            {
                return ColonyAbi.Invalid($"Component index {index} is out of range.");
            }

            if (outInfo == null)
            {
                return ColonyAbi.Invalid("out_info is null.");
            }

            var d = components[index];
            *outInfo = new ColonyTypeInfo
            {
                Name = native.Strings.Pin(d.Name),
                Id = d.Type.Id,
                Size = d.Size,
                Alignment = d.Alignment,
                Flags = ComponentFlags(d),
                LayoutHash = d.LayoutHash,
            };
            return ColonyAbi.Ok;
        }
        catch (Exception e)
        {
            return ColonyAbi.Fail(e);
        }
    }

    [UnmanagedCallersOnly]
    public static int SchemaResourceCount(nint schema, int* outCount)
    {
        try
        {
            return Out(outCount, Schema(schema).Schema.ResourceCount);
        }
        catch (Exception e)
        {
            return ColonyAbi.Fail(e);
        }
    }

    [UnmanagedCallersOnly]
    public static int SchemaResource(nint schema, int index, ColonyTypeInfo* outInfo)
    {
        try
        {
            var native = Schema(schema);
            var resources = native.Schema.Resources;
            if ((uint)index >= (uint)resources.Length)
            {
                return ColonyAbi.Invalid($"Resource index {index} is out of range.");
            }

            if (outInfo == null)
            {
                return ColonyAbi.Invalid("out_info is null.");
            }

            var d = resources[index];
            *outInfo = new ColonyTypeInfo
            {
                Name = native.Strings.Pin(d.Name),
                Id = d.Type.Id,
                Size = d.Size,
                Alignment = d.Alignment,
                Flags = (d.IsEventQueue ? AbiDefinition.ResourceIsEvent : 0u) | (d.IsPersistent ? AbiDefinition.ResourceIsPersistent : 0u),
                LayoutHash = d.LayoutHash,
            };
            return ColonyAbi.Ok;
        }
        catch (Exception e)
        {
            return ColonyAbi.Fail(e);
        }
    }

    [UnmanagedCallersOnly]
    public static int SchemaInputCount(nint schema, int* outCount)
    {
        try
        {
            return Out(outCount, Schema(schema).Schema.InputCommandCount);
        }
        catch (Exception e)
        {
            return ColonyAbi.Fail(e);
        }
    }

    [UnmanagedCallersOnly]
    public static int SchemaInput(nint schema, int index, ColonyTypeInfo* outInfo)
    {
        try
        {
            var native = Schema(schema);
            var inputs = native.Schema.InputCommands;
            if ((uint)index >= (uint)inputs.Length)
            {
                return ColonyAbi.Invalid($"Input Command index {index} is out of range.");
            }

            if (outInfo == null)
            {
                return ColonyAbi.Invalid("out_info is null.");
            }

            var d = inputs[index];
            *outInfo = new ColonyTypeInfo
            {
                Name = native.Strings.Pin(d.Name),
                Id = d.Type.Id,
                Size = d.Size,
                Alignment = d.Alignment,
                Flags = 0,
                LayoutHash = d.LayoutHash,
            };
            return ColonyAbi.Ok;
        }
        catch (Exception e)
        {
            return ColonyAbi.Fail(e);
        }
    }

    [UnmanagedCallersOnly]
    public static int SchemaSystemCount(nint schema, int* outCount)
    {
        try
        {
            return Out(outCount, Schema(schema).Schema.Systems.Length);
        }
        catch (Exception e)
        {
            return ColonyAbi.Fail(e);
        }
    }

    [UnmanagedCallersOnly]
    public static int SchemaSystem(nint schema, int index, ColonySystemInfo* outInfo)
    {
        try
        {
            var native = Schema(schema);
            var systems = native.Schema.Systems;
            if ((uint)index >= (uint)systems.Length)
            {
                return ColonyAbi.Invalid($"System index {index} is out of range.");
            }

            if (outInfo == null)
            {
                return ColonyAbi.Invalid("out_info is null.");
            }

            var d = systems[index];
            *outInfo = new ColonySystemInfo
            {
                Name = native.Strings.Pin(d.Name),
                Phase = native.Strings.Pin(d.Phase.Name),
                Id = d.Id,
                Order = d.Order,
                Every = d.Every,
                Offset = d.Offset,
            };
            return ColonyAbi.Ok;
        }
        catch (Exception e)
        {
            return ColonyAbi.Fail(e);
        }
    }

    [UnmanagedCallersOnly]
    public static int SchemaPluginCount(nint schema, int* outCount)
    {
        try
        {
            return Out(outCount, Schema(schema).Schema.Plugins.Length);
        }
        catch (Exception e)
        {
            return ColonyAbi.Fail(e);
        }
    }

    [UnmanagedCallersOnly]
    public static int SchemaPlugin(nint schema, int index, ColonyPluginInfo* outInfo)
    {
        try
        {
            var native = Schema(schema);
            var plugins = native.Schema.Plugins;
            if ((uint)index >= (uint)plugins.Length)
            {
                return ColonyAbi.Invalid($"Plugin index {index} is out of range.");
            }

            if (outInfo == null)
            {
                return ColonyAbi.Invalid("out_info is null.");
            }

            var d = plugins[index];
            *outInfo = new ColonyPluginInfo
            {
                Id = native.Strings.Pin(d.Id),
                Version = native.Strings.Pin(d.Version),
                Deterministic = d.Deterministic ? 1 : 0,
                AbiVersion = (uint)d.AbiVersion,
            };
            return ColonyAbi.Ok;
        }
        catch (Exception e)
        {
            return ColonyAbi.Fail(e);
        }
    }

    [UnmanagedCallersOnly]
    public static int SchemaFindComponent(nint schema, byte* name, int* outId)
    {
        try
        {
            return Schema(schema).Schema.TryGetComponent(Utf8Strings.ReadRequired(name, "name"), out var type) ? Out(outId, type.Id) : NotFound(outId);
        }
        catch (Exception e)
        {
            return ColonyAbi.Fail(e);
        }
    }

    [UnmanagedCallersOnly]
    public static int SchemaFindResource(nint schema, byte* name, int* outId)
    {
        try
        {
            return Schema(schema).Schema.TryGetResource(Utf8Strings.ReadRequired(name, "name"), out var type) ? Out(outId, type.Id) : NotFound(outId);
        }
        catch (Exception e)
        {
            return ColonyAbi.Fail(e);
        }
    }

    [UnmanagedCallersOnly]
    public static int SchemaFindInput(nint schema, byte* name, int* outId)
    {
        try
        {
            return Schema(schema).Schema.TryGetInputCommand(Utf8Strings.ReadRequired(name, "name"), out var type) ? Out(outId, type.Id) : NotFound(outId);
        }
        catch (Exception e)
        {
            return ColonyAbi.Fail(e);
        }
    }

    [UnmanagedCallersOnly]
    public static int SchemaDestroy(nint schema)
    {
        try
        {
            var native = Schema(schema);
            NativeHandles.Release(native);
            native.Dispose();
            return ColonyAbi.Ok;
        }
        catch (Exception e)
        {
            return ColonyAbi.Fail(e);
        }
    }

    // ---- World lifecycle and host-side access ----

    [UnmanagedCallersOnly]
    public static int WorldCreate(nint schema, ulong seed, int workers, int initialCapacity, nint* outWorld)
    {
        try
        {
            if (outWorld == null)
            {
                return ColonyAbi.Invalid("out_world is null.");
            }

            var world = Schema(schema).Schema.CreateWorld(new WorldOptions
            {
                Seed = seed,
                WorkerThreads = Math.Max(workers, 0),
                InitialEntityCapacity = initialCapacity > 0 ? initialCapacity : WorldOptions.Default.InitialEntityCapacity,
            });
            *outWorld = NativeHandles.GetOrAdd(world);
            return ColonyAbi.Ok;
        }
        catch (Exception e)
        {
            return ColonyAbi.Fail(e);
        }
    }

    [UnmanagedCallersOnly]
    public static int WorldLoad(nint schema, byte* path, nint* outWorld)
    {
        try
        {
            if (outWorld == null)
            {
                return ColonyAbi.Invalid("out_world is null.");
            }

            var world = WorldPersistence.Load(Utf8Strings.ReadRequired(path, "path"), Schema(schema).Schema);
            *outWorld = NativeHandles.GetOrAdd(world);
            return ColonyAbi.Ok;
        }
        catch (Exception e)
        {
            return ColonyAbi.Fail(e);
        }
    }

    [UnmanagedCallersOnly]
    public static int WorldSave(nint world, byte* path)
    {
        try
        {
            World(world).Save(Utf8Strings.ReadRequired(path, "path"));
            return ColonyAbi.Ok;
        }
        catch (Exception e)
        {
            return ColonyAbi.Fail(e);
        }
    }

    [UnmanagedCallersOnly]
    public static int WorldTick(nint world, int* outTicked)
    {
        try
        {
            var w = World(world);
            bool ticked = w.Tick();
            if (w.IsFaulted)
            {
                var fault = w.Fault!.Value;
                throw new InvalidOperationException($"The World faulted at Tick {fault.Tick} in System '{fault.SystemName}': {fault.Exception.Message}");
            }

            return Out(outTicked, ticked ? 1 : 0);
        }
        catch (Exception e)
        {
            return ColonyAbi.Fail(e);
        }
    }

    [UnmanagedCallersOnly]
    public static int WorldCurrentTick(nint world, long* outTick)
    {
        try
        {
            return Out(outTick, World(world).CurrentTick);
        }
        catch (Exception e)
        {
            return ColonyAbi.Fail(e);
        }
    }

    [UnmanagedCallersOnly]
    public static int WorldHash(nint world, ulong* outHash)
    {
        try
        {
            return Out(outHash, World(world).Hash());
        }
        catch (Exception e)
        {
            return ColonyAbi.Fail(e);
        }
    }

    [UnmanagedCallersOnly]
    public static int WorldEntityCount(nint world, int* outCount)
    {
        try
        {
            return Out(outCount, World(world).EntityCount);
        }
        catch (Exception e)
        {
            return ColonyAbi.Fail(e);
        }
    }

    [UnmanagedCallersOnly]
    public static int WorldIsAlive(nint world, ulong entity, int* outAlive)
    {
        try
        {
            return Out(outAlive, World(world).IsAlive(new EntityId(entity)) ? 1 : 0);
        }
        catch (Exception e)
        {
            return ColonyAbi.Fail(e);
        }
    }

    [UnmanagedCallersOnly]
    public static int WorldHas(nint world, ulong entity, int component, int* outHas)
    {
        try
        {
            var w = World(world);
            return Out(outHas, w.Has(new EntityId(entity), Component(w.Schema, component)) ? 1 : 0);
        }
        catch (Exception e)
        {
            return ColonyAbi.Fail(e);
        }
    }

    [UnmanagedCallersOnly]
    public static int WorldGetComponent(nint world, ulong entity, int component, void* outValue, int size)
    {
        try
        {
            var w = World(world);
            var type = Component(w.Schema, component);
            int expected = w.Schema.Describe(type).Size;
            if (outValue == null || size != expected)
            {
                return ColonyAbi.Invalid($"Component '{w.Schema.Describe(type).Name}' is {expected} bytes; {size} bytes were given.");
            }

            byte* source = w.ComponentAddress(new EntityId(entity), type);
            new ReadOnlySpan<byte>(source, size).CopyTo(new Span<byte>(outValue, size));
            return ColonyAbi.Ok;
        }
        catch (Exception e)
        {
            return ColonyAbi.Fail(e);
        }
    }

    [UnmanagedCallersOnly]
    public static int WorldGetResource(nint world, int resource, void* outValue, int size)
    {
        try
        {
            var w = World(world);
            var type = Resource(w.Schema, resource);
            int expected = w.Schema.DescribeResource(type).Size;
            if (outValue == null || size != expected)
            {
                return ColonyAbi.Invalid($"Resource '{w.Schema.DescribeResource(type).Name}' is {expected} bytes; {size} bytes were given.");
            }

            new ReadOnlySpan<byte>(w.ResourceAddress(type), size).CopyTo(new Span<byte>(outValue, size));
            return ColonyAbi.Ok;
        }
        catch (Exception e)
        {
            return ColonyAbi.Fail(e);
        }
    }

    [UnmanagedCallersOnly]
    public static int WorldEvents(nint world, int queue, void** outData, int* outCount)
    {
        try
        {
            var w = World(world);
            if (outData == null || outCount == null)
            {
                return ColonyAbi.Invalid("out_data or out_count is null.");
            }

            *outData = w.EventsAddress(Resource(w.Schema, queue), out int count);
            *outCount = count;
            return ColonyAbi.Ok;
        }
        catch (Exception e)
        {
            return ColonyAbi.Fail(e);
        }
    }

    [UnmanagedCallersOnly]
    public static int WorldRandom(nint world, ColonyRandom* outRandom)
    {
        try
        {
            if (outRandom == null)
            {
                return ColonyAbi.Invalid("out_random is null.");
            }

            var random = World(world).Random;
            *outRandom = new ColonyRandom { Seed = random.Seed, Key = random.Key };
            return ColonyAbi.Ok;
        }
        catch (Exception e)
        {
            return ColonyAbi.Fail(e);
        }
    }

    [UnmanagedCallersOnly]
    public static int WorldCreateCommands(nint world, nint* outCommands)
    {
        try
        {
            if (outCommands == null)
            {
                return ColonyAbi.Invalid("out_commands is null.");
            }

            *outCommands = NativeHandles.GetOrAdd(World(world).CreateCommandBuffer());
            return ColonyAbi.Ok;
        }
        catch (Exception e)
        {
            return ColonyAbi.Fail(e);
        }
    }

    [UnmanagedCallersOnly]
    public static int WorldPlayback(nint world, nint commands)
    {
        try
        {
            World(world).Playback(Commands(commands));
            return ColonyAbi.Ok;
        }
        catch (Exception e)
        {
            return ColonyAbi.Fail(e);
        }
    }

    [UnmanagedCallersOnly]
    public static int WorldSubmitInput(nint world, uint source, uint sequence, int type, void* payload, int size, long effectiveTick)
    {
        try
        {
            var w = World(world);
            if (payload == null || size < 0)
            {
                return ColonyAbi.Invalid("payload is null or has a negative size.");
            }

            w.Inputs.Submit(new InputSource(source), sequence, Input(w.Schema, type), new ReadOnlySpan<byte>(payload, size), effectiveTick);
            return ColonyAbi.Ok;
        }
        catch (Exception e)
        {
            return ColonyAbi.Fail(e);
        }
    }

    [UnmanagedCallersOnly]
    public static int WorldDefinitions(nint world, byte* typeName, int* outCount, int* outStride, void** outValues)
    {
        try
        {
            var table = Definitions(World(world), typeName);
            if (table is null)
            {
                return NotFound(outCount);
            }

            if (outCount == null || outStride == null || outValues == null)
            {
                return ColonyAbi.Invalid("an out pointer is null.");
            }

            var values = table.RawValues;
            *outCount = table.Count;
            *outStride = table.Stride;
            *outValues = values.IsEmpty ? null : System.Runtime.CompilerServices.Unsafe.AsPointer(ref MemoryMarshal.GetReference(values));
            return ColonyAbi.Ok;
        }
        catch (Exception e)
        {
            return ColonyAbi.Fail(e);
        }
    }

    [UnmanagedCallersOnly]
    public static int WorldDefinitionFind(nint world, byte* typeName, byte* name, int* outIndex)
    {
        try
        {
            var table = Definitions(World(world), typeName);
            if (table is null)
            {
                return NotFound(outIndex);
            }

            return table.TryFind(Utf8Strings.ReadRequired(name, "name"), out int index) ? Out(outIndex, index) : NotFound(outIndex);
        }
        catch (Exception e)
        {
            return ColonyAbi.Fail(e);
        }
    }

    [UnmanagedCallersOnly]
    public static int WorldDefinitionString(nint world, uint stringId, byte* buffer, int capacity, int* outLength)
    {
        try
        {
            var registry = World(world).Definitions().Registry;
            var text = registry.Strings.Get(new StringId(stringId));
            return Out(outLength, Utf8Strings.CopyTo(text, buffer, capacity));
        }
        catch (Exception e)
        {
            return ColonyAbi.Fail(e);
        }
    }

    [UnmanagedCallersOnly]
    public static int WorldDestroy(nint world)
    {
        try
        {
            var w = World(world);
            NativeHandles.Release(w);
            w.Dispose();
            return ColonyAbi.Ok;
        }
        catch (Exception e)
        {
            return ColonyAbi.Fail(e);
        }
    }

    // ---- command buffers: the only write path ----

    [UnmanagedCallersOnly]
    public static int CommandsCreateEntity(nint commands, ulong* outDeferred)
    {
        try
        {
            return Out(outDeferred, Commands(commands).Create().Value);
        }
        catch (Exception e)
        {
            return ColonyAbi.Fail(e);
        }
    }

    [UnmanagedCallersOnly]
    public static int CommandsDestroyEntity(nint commands, ulong entity)
    {
        try
        {
            Commands(commands).Destroy(new EntityId(entity));
            return ColonyAbi.Ok;
        }
        catch (Exception e)
        {
            return ColonyAbi.Fail(e);
        }
    }

    [UnmanagedCallersOnly]
    public static int CommandsAdd(nint commands, ulong entity, int component, void* value, int size)
    {
        try
        {
            var buffer = Commands(commands);
            buffer.AddRaw(new EntityId(entity), Component(buffer.World.Schema, component), (byte*)value, size);
            return ColonyAbi.Ok;
        }
        catch (Exception e)
        {
            return ColonyAbi.Fail(e);
        }
    }

    [UnmanagedCallersOnly]
    public static int CommandsRemove(nint commands, ulong entity, int component)
    {
        try
        {
            var buffer = Commands(commands);
            buffer.Remove(new EntityId(entity), Component(buffer.World.Schema, component));
            return ColonyAbi.Ok;
        }
        catch (Exception e)
        {
            return ColonyAbi.Fail(e);
        }
    }

    [UnmanagedCallersOnly]
    public static int CommandsEmit(nint commands, int queue, void* value, int size)
    {
        try
        {
            var buffer = Commands(commands);
            buffer.EmitRaw(Resource(buffer.World.Schema, queue), (byte*)value, size);
            return ColonyAbi.Ok;
        }
        catch (Exception e)
        {
            return ColonyAbi.Fail(e);
        }
    }

    [UnmanagedCallersOnly]
    public static int CommandsResolve(nint commands, ulong deferred, ulong* outEntity)
    {
        try
        {
            return Out(outEntity, Commands(commands).Resolve(new EntityId(deferred)).Value);
        }
        catch (Exception e)
        {
            return ColonyAbi.Fail(e);
        }
    }

    // ---- inside a System: events and Input Commands of this Tick ----

    [UnmanagedCallersOnly]
    public static int ContextEvents(ColonyContext* context, int queue, void** outData, int* outCount)
    {
        try
        {
            if (context == null || outData == null || outCount == null)
            {
                return ColonyAbi.Invalid("context, out_data or out_count is null.");
            }

            var system = NativeHandles.Get<NativeSystem>(context->System, "colony_system");
            var w = World(context->World);
            var type = Resource(w.Schema, queue);
            if (!system.Definition.ReadsResources.Contains(type) && !system.Definition.WritesResources.Contains(type))
            {
                return ColonyAbi.Invalid($"System '{system.Definition.Name}' reads events '{w.Schema.DescribeResource(type).Name}' without declaring them in reads_resources.");
            }

            *outData = w.EventsAddress(type, out int count);
            *outCount = count;
            return ColonyAbi.Ok;
        }
        catch (Exception e)
        {
            return ColonyAbi.Fail(e);
        }
    }

    [UnmanagedCallersOnly]
    public static int ContextInputCount(ColonyContext* context, int* outCount)
    {
        try
        {
            if (context == null)
            {
                return ColonyAbi.Invalid("context is null.");
            }

            return Out(outCount, World(context->World).Inputs.HandlerCount); // denied commands (TASK-35) are not shown to handlers
        }
        catch (Exception e)
        {
            return ColonyAbi.Fail(e);
        }
    }

    [UnmanagedCallersOnly]
    public static int ContextInput(ColonyContext* context, int index, ColonyInput* outInput)
    {
        try
        {
            if (context == null || outInput == null)
            {
                return ColonyAbi.Invalid("context or out_input is null.");
            }

            var inputs = World(context->World).Inputs;
            int canonical = inputs.HandlerIndex(index);
            ref readonly var entry = ref inputs.RecordAt(canonical);
            var payload = inputs.PayloadAt(canonical);
            var random = inputs.RandomFor(new InputSource(entry.Source), entry.Sequence);
            *outInput = new ColonyInput
            {
                Source = entry.Source,
                Sequence = entry.Sequence,
                Type = entry.Type,
                Size = payload.Length,
                Payload = payload.IsEmpty ? null : System.Runtime.CompilerServices.Unsafe.AsPointer(ref MemoryMarshal.GetReference(payload)),
                Random = new ColonyRandom { Seed = random.Seed, Key = random.Key },
            };
            return ColonyAbi.Ok;
        }
        catch (Exception e)
        {
            return ColonyAbi.Fail(e);
        }
    }

    [UnmanagedCallersOnly]
    public static int ContextRejectInput(ColonyContext* context, int index, int reason)
    {
        try
        {
            if (context == null)
            {
                return ColonyAbi.Invalid("context is null.");
            }

            var w = World(context->World);
            ref readonly var entry = ref w.Inputs.RecordAt(w.Inputs.HandlerIndex(index));
            var rejected = new CommandRejected(new InputSource(entry.Source), entry.Sequence, new InputCommandType(entry.Type), reason);
            Commands(context->Commands).Emit(w.Schema.CommandRejections, in rejected);
            return ColonyAbi.Ok;
        }
        catch (Exception e)
        {
            return ColonyAbi.Fail(e);
        }
    }

    // ---- RNG streams (pure functions over colony_random) ----

    [UnmanagedCallersOnly]
    public static int RandomDerive(ColonyRandom* random, ulong key, ColonyRandom* outRandom)
    {
        if (random == null || outRandom == null)
        {
            return ColonyAbi.Invalid("random or out_random is null.");
        }

        var derived = new RandomSource(random->Seed, random->Key).Derive(key);
        *outRandom = new ColonyRandom { Seed = derived.Seed, Key = derived.Key };
        return ColonyAbi.Ok;
    }

    [UnmanagedCallersOnly]
    public static int RandomU32(ColonyRandom* random, ulong counter, uint* outValue)
    {
        if (random == null || outValue == null)
        {
            return ColonyAbi.Invalid("random or out_value is null.");
        }

        *outValue = new RandomSource(random->Seed, random->Key).UInt32At(counter);
        return ColonyAbi.Ok;
    }

    [UnmanagedCallersOnly]
    public static int RandomI32(ColonyRandom* random, ulong counter, int minInclusive, int maxExclusive, int* outValue)
    {
        try
        {
            if (random == null || outValue == null)
            {
                return ColonyAbi.Invalid("random or out_value is null.");
            }

            *outValue = new RandomSource(random->Seed, random->Key).Int32At(counter, minInclusive, maxExclusive);
            return ColonyAbi.Ok;
        }
        catch (Exception e)
        {
            return ColonyAbi.Fail(e);
        }
    }

    // ---- errors ----

    [UnmanagedCallersOnly]
    public static int LastError(byte* buffer, int capacity, int* outLength)
    {
        try
        {
            return Out(outLength, Utf8Strings.CopyTo(ColonyAbi.LastError, buffer, capacity));
        }
        catch (Exception e)
        {
            return ColonyAbi.Fail(e);
        }
    }

    // ---- helpers ----

    private static NativeHost Host(nint handle) => NativeHandles.Get<NativeHost>(handle, "colony_host");

    private static NativeRegistration Registration(nint handle) => NativeHandles.Get<NativeRegistration>(handle, "colony_registration");

    private static NativeSchema Schema(nint handle) => NativeHandles.Get<NativeSchema>(handle, "colony_schema");

    private static World World(nint handle) => NativeHandles.Get<World>(handle, "colony_world");

    private static CommandBuffer Commands(nint handle) => NativeHandles.Get<CommandBuffer>(handle, "colony_commands");

    private static ComponentType Component(WorldSchema schema, int id)
        => (uint)id < (uint)schema.ComponentCount ? new ComponentType(id) : throw new ArgumentException($"Component id {id} is not in the schema.");

    private static ResourceType Resource(WorldSchema schema, int id)
        => (uint)id < (uint)schema.ResourceCount ? new ResourceType(id) : throw new ArgumentException($"Resource id {id} is not in the schema.");

    private static InputCommandType Input(WorldSchema schema, int id)
        => (uint)id < (uint)schema.InputCommandCount ? new InputCommandType(id) : throw new ArgumentException($"Input Command id {id} is not in the schema.");

    private static DefinitionTable? Definitions(World world, byte* typeName)
    {
        var registry = world.Definitions().Registry;
        int index = registry.IndexOfType(Utf8Strings.ReadRequired(typeName, "type_name"));
        return index < 0 ? null : registry.TableAt(index);
    }

    private static uint ComponentFlags(in ComponentDescriptor d)
        => (d.IsNonFragmenting ? AbiDefinition.ComponentNonFragmenting : 0u)
         | (d.IsRelation ? AbiDefinition.ComponentIsRelation : 0u)
         | (d.IsBuffer ? AbiDefinition.ComponentIsBuffer : 0u);

    private static int Out<T>(T* target, T value) where T : unmanaged
    {
        if (target == null)
        {
            return ColonyAbi.Invalid("The out pointer is null.");
        }

        *target = value;
        return ColonyAbi.Ok;
    }

    private static int NotFound<T>(T* target) where T : unmanaged
    {
        if (target != null)
        {
            *target = default;
        }

        return ColonyAbi.NotFound;
    }
}
