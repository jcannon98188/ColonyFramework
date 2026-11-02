using System.Collections.Immutable;

namespace Colony.Plugins.Native;

/// <summary>One function of the table: its C name, the <see cref="AbiEntries"/> method behind it, its address and its C parameter list (every entry returns an <c>int</c> status).</summary>
internal sealed record AbiEntry(string Name, string MethodName, nint Address, string Summary, ImmutableArray<string> Parameters);

/// <summary>One struct of the header: C name, the size the C compiler must agree on, and its fields as (C type, name).</summary>
internal sealed record AbiStruct(string Name, int Size, string Summary, ImmutableArray<(string Type, string Name)> Fields);

/// <summary>A named integer constant of the header.</summary>
internal sealed record AbiConstant(string Name, long Value, string? Summary);

/// <summary>
/// The C ABI definition (spec section 8): the constants, structs and function entries that <see cref="HeaderGenerator"/>
/// turns into <c>colony.h</c> and <see cref="ColonyAbi"/> lays out as the function table, in this order. Adding an
/// entry means: an <c>[UnmanagedCallersOnly]</c> method in <see cref="AbiEntries"/>, one line here, a regenerated
/// header (<c>COLONY_ACCEPT_HEADER=1 dotnet test tests/Colony.Native.Tests</c>), a call in the conformance host,
/// and a version bump when an existing entry changes.
/// </summary>
internal static unsafe class AbiDefinition
{
    public const uint ComponentNonFragmenting = 1;
    public const uint ComponentIsRelation = 2;
    public const uint ComponentIsBuffer = 4;
    public const uint ResourceTransient = 1;
    public const uint ResourceIsEvent = 2;
    public const uint ResourceIsPersistent = 4;
    public const uint EventPersistent = 1;

    public static ImmutableArray<AbiConstant> StatusCodes { get; } =
    [
        new("COLONY_OK", ColonyAbi.Ok, "The call succeeded."),
        new("COLONY_ERROR", ColonyAbi.Error, "The call failed; colony_last_error has the message."),
        new("COLONY_INVALID_ARGUMENT", ColonyAbi.InvalidArgument, "A null pointer, a dead handle, a size mismatch or an id the schema does not have."),
        new("COLONY_NOT_FOUND", ColonyAbi.NotFound, "A lookup by name found nothing."),
        new("COLONY_VERSION_MISMATCH", ColonyAbi.VersionMismatch, "colony_get_api was asked for another ABI version."),
    ];

    public static ImmutableArray<AbiConstant> Constants { get; } =
    [
        new("COLONY_COMPONENT_NON_FRAGMENTING", ComponentNonFragmenting, "register_component flag / type_info flag: stored outside archetype chunks (never in a colony_chunk column)."),
        new("COLONY_COMPONENT_IS_RELATION", ComponentIsRelation, "type_info flag: the Component behind a Relation."),
        new("COLONY_COMPONENT_IS_BUFFER", ComponentIsBuffer, "type_info flag: a buffer Component."),
        new("COLONY_RESOURCE_TRANSIENT", ResourceTransient, "register_resource flag: neither saved nor hashed."),
        new("COLONY_RESOURCE_IS_EVENT", ResourceIsEvent, "type_info flag of a Resource: an event queue, not a value."),
        new("COLONY_RESOURCE_IS_PERSISTENT", ResourceIsPersistent, "type_info flag of a Resource: saved and hashed."),
        new("COLONY_EVENT_PERSISTENT", EventPersistent, "register_event flag: the queue's pending events are saved and hashed."),
        new("COLONY_FIELD_INT32", (long)RawFieldKind.Int32, "Definition field kinds (colony_definition_field.kind): the encoding of the bytes at the field's offset."),
        new("COLONY_FIELD_INT64", (long)RawFieldKind.Int64, null),
        new("COLONY_FIELD_UINT32", (long)RawFieldKind.UInt32, null),
        new("COLONY_FIELD_UINT8", (long)RawFieldKind.UInt8, null),
        new("COLONY_FIELD_BOOL", (long)RawFieldKind.Bool, "One byte, 0 or 1."),
        new("COLONY_FIELD_FLOAT32", (long)RawFieldKind.Float32, null),
        new("COLONY_FIELD_FLOAT64", (long)RawFieldKind.Float64, null),
        new("COLONY_FIELD_STRING", (long)RawFieldKind.StringId, "uint32 string id of the Definition registry; read it with world_definition_string."),
        new("COLONY_FIELD_FIXED32", (long)RawFieldKind.Fixed32, "Q16.16 fixed point in an int32."),
        new("COLONY_FIELD_FIXED64", (long)RawFieldKind.Fixed64, "Q32.32 fixed point in an int64."),
    ];

    /// <summary>Opaque handle types: the C side only ever holds pointers to them.</summary>
    public static ImmutableArray<string> Handles { get; } = ["colony_host", "colony_registration", "colony_schema", "colony_world", "colony_commands"];

    public static ImmutableArray<AbiStruct> Structs { get; } =
    [
        new("colony_random", sizeof(ColonyRandom), "A counter-based random stream (World seed, stream key); pure functions below draw from it.",
            [("uint64_t", "seed"), ("uint64_t", "key")]),
        new("colony_type_info", sizeof(ColonyTypeInfo), "One Component, Resource or Input Command of a schema. name is owned by the schema.",
            [("const char*", "name"), ("int32_t", "id"), ("int32_t", "size"), ("int32_t", "alignment"), ("uint32_t", "flags"), ("uint64_t", "layout_hash")]),
        new("colony_system_info", sizeof(ColonySystemInfo), "Where the scheduler placed one System.",
            [("const char*", "name"), ("const char*", "phase"), ("int32_t", "id"), ("int32_t", "order"), ("int32_t", "every"), ("int32_t", "offset")]),
        new("colony_plugin_info", sizeof(ColonyPluginInfo), "One Plugin of a schema, in load order; abi_version is 0 for a C# Plugin.",
            [("const char*", "id"), ("const char*", "version"), ("int32_t", "deterministic"), ("uint32_t", "abi_version")]),
        new("colony_column", sizeof(ColonyColumn), "One read-only Component column of a chunk: count values of stride bytes, or data == NULL when the chunk lacks the Component.",
            [("int32_t", "component"), ("int32_t", "stride"), ("const void*", "data")]),
        new("colony_chunk", sizeof(ColonyChunk), "A read-only chunk view handed to for_each_chunk: live rows first, one column per declared read in declaration order. Valid only during the callback.",
            [("int32_t", "count"), ("int32_t", "index"), ("const uint64_t*", "entities"), ("int32_t", "column_count"), ("int32_t", "reserved"), ("const colony_column*", "columns")]),
        new("colony_context", sizeof(ColonyContext), "What a Native System sees while it runs: the Tick, its job (never a thread), its RNG stream, the command buffer of this job and the World. Valid only during the callback.",
            [("int64_t", "tick"), ("int32_t", "system_id"), ("int32_t", "job_index"), ("int32_t", "job_count"), ("int32_t", "reserved"), ("colony_random", "random"), ("colony_commands*", "commands"), ("colony_world*", "world"), ("void*", "system")]),
        new("colony_input", sizeof(ColonyInput), "One Input Command of this Tick's canonical list (context_input).",
            [("uint32_t", "source"), ("uint32_t", "sequence"), ("int32_t", "type"), ("int32_t", "size"), ("const void*", "payload"), ("colony_random", "random")]),
        new("colony_definition_field", sizeof(ColonyDefinitionField), "One field of a Definition type registered by layout: the JSON name, a COLONY_FIELD_* kind and the byte offset.",
            [("const char*", "name"), ("int32_t", "kind"), ("int32_t", "offset")]),
        new("colony_component_range", sizeof(ColonyComponentRange), "One run of data bytes of a Component registered by layout (register_component_data); bytes outside every range are padding.",
            [("int32_t", "offset"), ("int32_t", "size")]),
        new("colony_system_desc", sizeof(ColonySystemDesc), "A Native System with its access declared in data. phase is \"Streaming\", \"Input\", \"Begin\", \"Simulate\" (the default when NULL), \"Late\", \"End\" or a Game Phase's name; reads are the Component ids whose columns for_each_chunk receives; with/without filter the query; emits are the event queues it may emit; after/before name Systems.",
            [("const char*", "name"), ("const char*", "phase"), ("const int32_t*", "reads"), ("int32_t", "reads_count"), ("const int32_t*", "with"), ("int32_t", "with_count"),
             ("const int32_t*", "without"), ("int32_t", "without_count"), ("const int32_t*", "reads_resources"), ("int32_t", "reads_resources_count"), ("const int32_t*", "emits"), ("int32_t", "emits_count"),
             ("const char* const*", "after"), ("int32_t", "after_count"), ("const char* const*", "before"), ("int32_t", "before_count"), ("int32_t", "every"), ("int32_t", "offset"),
             ("colony_system_hook_fn", "begin"), ("colony_system_chunk_fn", "for_each_chunk"), ("colony_system_hook_fn", "end"), ("void*", "user_data")]),
        new("colony_plugin_desc", sizeof(ColonyPluginDesc), "What colony_plugin_get fills in: manifest and the register callback that runs once at World build.",
            [("uint32_t", "abi_version"), ("int32_t", "deterministic"), ("const char*", "id"), ("const char*", "version"), ("const char* const*", "dependencies"), ("int32_t", "dependency_count"), ("int32_t", "reserved"),
             ("colony_plugin_register_fn", "register_fn"), ("void*", "user_data")]),
    ];

    public static ImmutableArray<(string Name, string Signature, string Summary)> Callbacks { get; } =
    [
        ("colony_system_hook_fn", "int (*{0})(void* user_data, const colony_context* context)", "begin / end hook of a Native System; return COLONY_OK or fault the World."),
        ("colony_system_chunk_fn", "int (*{0})(void* user_data, const colony_context* context, const colony_chunk* chunk)", "Chunk job of a Native System."),
        ("colony_plugin_register_fn", "int (*{0})(void* user_data, colony_registration* registration)", "The Plugin's one registration entry point."),
        ("colony_get_api_fn", "int (*{0})(uint32_t version, const colony_api** out_api)", "Exported by the Framework library (Colony.Native) as colony_get_api."),
        ("colony_plugin_get_fn", "int (*{0})(const colony_api* api, colony_plugin_desc* out_plugin)", "Exported by a Native Plugin library as colony_plugin_get."),
    ];

    public static ImmutableArray<AbiEntry> Entries { get; } =
    [
        // host
        E("host_create", nameof(AbiEntries.HostCreate), (nint)(delegate* unmanaged<nint*, int>)&AbiEntries.HostCreate, "Creates a host: a WorldBuilder and a Plugin loader to build one schema with.", "colony_host** out_host"),
        E("host_add_plugin", nameof(AbiEntries.HostAddPlugin), (nint)(delegate* unmanaged<nint, ColonyPluginDesc*, int>)&AbiEntries.HostAddPlugin, "Adds a Native Plugin; its register callback runs during host_build in dependency order.", "colony_host* host", "const colony_plugin_desc* plugin"),
        E("host_build", nameof(AbiEntries.HostBuild), (nint)(delegate* unmanaged<nint, nint*, int>)&AbiEntries.HostBuild, "Registers every Plugin and builds the schema; registration is closed afterwards.", "colony_host* host", "colony_schema** out_schema"),
        E("host_destroy", nameof(AbiEntries.HostDestroy), (nint)(delegate* unmanaged<nint, int>)&AbiEntries.HostDestroy, "Releases the host (the schema lives on).", "colony_host* host"),
        // registration
        E("register_component", nameof(AbiEntries.RegisterComponent), (nint)(delegate* unmanaged<nint, byte*, int, int, uint, int*, int>)&AbiEntries.RegisterComponent, "Registers a Component by name, size and alignment (COLONY_COMPONENT_* flags); same identity as a C# struct registered under that name and layout.", "colony_registration* registration", "const char* name", "int32_t size", "int32_t alignment", "uint32_t flags", "int32_t* out_id"),
        E("register_resource", nameof(AbiEntries.RegisterResource), (nint)(delegate* unmanaged<nint, byte*, int, int, uint, int*, int>)&AbiEntries.RegisterResource, "Registers a value Resource (COLONY_RESOURCE_TRANSIENT or 0).", "colony_registration* registration", "const char* name", "int32_t size", "int32_t alignment", "uint32_t flags", "int32_t* out_id"),
        E("register_event", nameof(AbiEntries.RegisterEvent), (nint)(delegate* unmanaged<nint, byte*, int, int, uint, int*, int>)&AbiEntries.RegisterEvent, "Registers an event queue (COLONY_EVENT_PERSISTENT or 0).", "colony_registration* registration", "const char* name", "int32_t size", "int32_t alignment", "uint32_t flags", "int32_t* out_id"),
        E("register_input", nameof(AbiEntries.RegisterInput), (nint)(delegate* unmanaged<nint, byte*, int, int, int*, int>)&AbiEntries.RegisterInput, "Registers an Input Command type.", "colony_registration* registration", "const char* name", "int32_t size", "int32_t alignment", "int32_t* out_id"),
        E("register_definition_type", nameof(AbiEntries.RegisterDefinitionType), (nint)(delegate* unmanaged<nint, byte*, int, int, ColonyDefinitionField*, int, int>)&AbiEntries.RegisterDefinitionType, "Registers a Definition type by layout and field list; sources fill it, world_definitions reads it.", "colony_registration* registration", "const char* type_name", "int32_t size", "int32_t alignment", "const colony_definition_field* fields", "int32_t field_count"),
        E("register_definition_source", nameof(AbiEntries.RegisterDefinitionSource), (nint)(delegate* unmanaged<nint, byte*, int, byte*, int>)&AbiEntries.RegisterDefinitionSource, "Applies a JSON Definition source ({ \"type\": { \"name\": { \"field\": value } } }) now; later Plugins may patch it.", "colony_registration* registration", "const char* json_utf8", "int32_t length", "const char* origin"),
        E("register_system", nameof(AbiEntries.RegisterSystem), (nint)(delegate* unmanaged<nint, ColonySystemDesc*, int>)&AbiEntries.RegisterSystem, "Registers a Native System; the descriptor is copied.", "colony_registration* registration", "const colony_system_desc* system"),
        E("register_find_component", nameof(AbiEntries.RegisterFindComponent), (nint)(delegate* unmanaged<nint, byte*, int*, int>)&AbiEntries.RegisterFindComponent, "Id of a Component another Plugin or the Game registered (COLONY_NOT_FOUND otherwise).", "colony_registration* registration", "const char* name", "int32_t* out_id"),
        E("register_find_resource", nameof(AbiEntries.RegisterFindResource), (nint)(delegate* unmanaged<nint, byte*, int*, int>)&AbiEntries.RegisterFindResource, "Id of a Resource or event queue by name.", "colony_registration* registration", "const char* name", "int32_t* out_id"),
        E("register_find_input", nameof(AbiEntries.RegisterFindInput), (nint)(delegate* unmanaged<nint, byte*, int*, int>)&AbiEntries.RegisterFindInput, "Id of an Input Command by name.", "colony_registration* registration", "const char* name", "int32_t* out_id"),
        // schema
        E("schema_hash", nameof(AbiEntries.SchemaHash), (nint)(delegate* unmanaged<nint, ulong*, int>)&AbiEntries.SchemaHash, "The schema hash (layouts, Systems, extensions).", "const colony_schema* schema", "uint64_t* out_hash"),
        E("schema_component_count", nameof(AbiEntries.SchemaComponentCount), (nint)(delegate* unmanaged<nint, int*, int>)&AbiEntries.SchemaComponentCount, null, "const colony_schema* schema", "int32_t* out_count"),
        E("schema_component", nameof(AbiEntries.SchemaComponent), (nint)(delegate* unmanaged<nint, int, ColonyTypeInfo*, int>)&AbiEntries.SchemaComponent, "Component index == id.", "const colony_schema* schema", "int32_t index", "colony_type_info* out_info"),
        E("schema_resource_count", nameof(AbiEntries.SchemaResourceCount), (nint)(delegate* unmanaged<nint, int*, int>)&AbiEntries.SchemaResourceCount, null, "const colony_schema* schema", "int32_t* out_count"),
        E("schema_resource", nameof(AbiEntries.SchemaResource), (nint)(delegate* unmanaged<nint, int, ColonyTypeInfo*, int>)&AbiEntries.SchemaResource, "Resource index == id; id 0 is always the built-in structural-failure event queue.", "const colony_schema* schema", "int32_t index", "colony_type_info* out_info"),
        E("schema_input_count", nameof(AbiEntries.SchemaInputCount), (nint)(delegate* unmanaged<nint, int*, int>)&AbiEntries.SchemaInputCount, null, "const colony_schema* schema", "int32_t* out_count"),
        E("schema_input", nameof(AbiEntries.SchemaInput), (nint)(delegate* unmanaged<nint, int, ColonyTypeInfo*, int>)&AbiEntries.SchemaInput, "Input Command index == id.", "const colony_schema* schema", "int32_t index", "colony_type_info* out_info"),
        E("schema_system_count", nameof(AbiEntries.SchemaSystemCount), (nint)(delegate* unmanaged<nint, int*, int>)&AbiEntries.SchemaSystemCount, null, "const colony_schema* schema", "int32_t* out_count"),
        E("schema_system", nameof(AbiEntries.SchemaSystem), (nint)(delegate* unmanaged<nint, int, ColonySystemInfo*, int>)&AbiEntries.SchemaSystem, "System index == id (registration order).", "const colony_schema* schema", "int32_t index", "colony_system_info* out_info"),
        E("schema_plugin_count", nameof(AbiEntries.SchemaPluginCount), (nint)(delegate* unmanaged<nint, int*, int>)&AbiEntries.SchemaPluginCount, null, "const colony_schema* schema", "int32_t* out_count"),
        E("schema_plugin", nameof(AbiEntries.SchemaPlugin), (nint)(delegate* unmanaged<nint, int, ColonyPluginInfo*, int>)&AbiEntries.SchemaPlugin, "Plugins in load order.", "const colony_schema* schema", "int32_t index", "colony_plugin_info* out_info"),
        E("schema_find_component", nameof(AbiEntries.SchemaFindComponent), (nint)(delegate* unmanaged<nint, byte*, int*, int>)&AbiEntries.SchemaFindComponent, null, "const colony_schema* schema", "const char* name", "int32_t* out_id"),
        E("schema_find_resource", nameof(AbiEntries.SchemaFindResource), (nint)(delegate* unmanaged<nint, byte*, int*, int>)&AbiEntries.SchemaFindResource, null, "const colony_schema* schema", "const char* name", "int32_t* out_id"),
        E("schema_find_input", nameof(AbiEntries.SchemaFindInput), (nint)(delegate* unmanaged<nint, byte*, int*, int>)&AbiEntries.SchemaFindInput, null, "const colony_schema* schema", "const char* name", "int32_t* out_id"),
        E("schema_destroy", nameof(AbiEntries.SchemaDestroy), (nint)(delegate* unmanaged<nint, int>)&AbiEntries.SchemaDestroy, "Releases the schema handle and the strings it handed out; destroy its Worlds first.", "colony_schema* schema"),
        // world
        E("world_create", nameof(AbiEntries.WorldCreate), (nint)(delegate* unmanaged<nint, ulong, int, int, nint*, int>)&AbiEntries.WorldCreate, "Creates a World: seed, worker threads (0 = physical cores), initial Entity capacity (0 = default).", "const colony_schema* schema", "uint64_t seed", "int32_t workers", "int32_t initial_capacity", "colony_world** out_world"),
        E("world_load", nameof(AbiEntries.WorldLoad), (nint)(delegate* unmanaged<nint, byte*, nint*, int>)&AbiEntries.WorldLoad, "Loads a save written by world_save into a fresh World of the schema (hash verified).", "const colony_schema* schema", "const char* path", "colony_world** out_world"),
        E("world_save", nameof(AbiEntries.WorldSave), (nint)(delegate* unmanaged<nint, byte*, int>)&AbiEntries.WorldSave, "Saves the World (default SaveOptions); the manifest records the Plugin set with each Plugin's ABI version.", "colony_world* world", "const char* path"),
        E("world_tick", nameof(AbiEntries.WorldTick), (nint)(delegate* unmanaged<nint, int*, int>)&AbiEntries.WorldTick, "Runs one Tick; out_ticked is 0 when the Tick gate held. A faulted System fails the call.", "colony_world* world", "int32_t* out_ticked"),
        E("world_current_tick", nameof(AbiEntries.WorldCurrentTick), (nint)(delegate* unmanaged<nint, long*, int>)&AbiEntries.WorldCurrentTick, null, "const colony_world* world", "int64_t* out_tick"),
        E("world_hash", nameof(AbiEntries.WorldHash), (nint)(delegate* unmanaged<nint, ulong*, int>)&AbiEntries.WorldHash, "The World hash (spec section 6); call between Ticks.", "const colony_world* world", "uint64_t* out_hash"),
        E("world_entity_count", nameof(AbiEntries.WorldEntityCount), (nint)(delegate* unmanaged<nint, int*, int>)&AbiEntries.WorldEntityCount, null, "const colony_world* world", "int32_t* out_count"),
        E("world_is_alive", nameof(AbiEntries.WorldIsAlive), (nint)(delegate* unmanaged<nint, ulong, int*, int>)&AbiEntries.WorldIsAlive, null, "const colony_world* world", "uint64_t entity", "int32_t* out_alive"),
        E("world_has", nameof(AbiEntries.WorldHas), (nint)(delegate* unmanaged<nint, ulong, int, int*, int>)&AbiEntries.WorldHas, null, "const colony_world* world", "uint64_t entity", "int32_t component", "int32_t* out_has"),
        E("world_get_component", nameof(AbiEntries.WorldGetComponent), (nint)(delegate* unmanaged<nint, ulong, int, void*, int, int>)&AbiEntries.WorldGetComponent, "Copies the Entity's Component value (size must be the registered size). Host side, between Ticks.", "const colony_world* world", "uint64_t entity", "int32_t component", "void* out_value", "int32_t size"),
        E("world_get_resource", nameof(AbiEntries.WorldGetResource), (nint)(delegate* unmanaged<nint, int, void*, int, int>)&AbiEntries.WorldGetResource, "Copies a value Resource.", "const colony_world* world", "int32_t resource", "void* out_value", "int32_t size"),
        E("world_events", nameof(AbiEntries.WorldEvents), (nint)(delegate* unmanaged<nint, int, void**, int*, int>)&AbiEntries.WorldEvents, "The pending events of a queue (what the End Phase emitted, until the next Tick's End boundary): count records of the registered size.", "const colony_world* world", "int32_t queue", "const void** out_data", "int32_t* out_count"),
        E("world_random", nameof(AbiEntries.WorldRandom), (nint)(delegate* unmanaged<nint, ColonyRandom*, int>)&AbiEntries.WorldRandom, "The World's root random stream; derive from it, never use it raw.", "const colony_world* world", "colony_random* out_random"),
        E("world_create_commands", nameof(AbiEntries.WorldCreateCommands), (nint)(delegate* unmanaged<nint, nint*, int>)&AbiEntries.WorldCreateCommands, "A host command buffer; record into it between Ticks and world_playback it.", "colony_world* world", "colony_commands** out_commands"),
        E("world_playback", nameof(AbiEntries.WorldPlayback), (nint)(delegate* unmanaged<nint, nint, int>)&AbiEntries.WorldPlayback, "Applies a host command buffer (a sync point) and clears it.", "colony_world* world", "colony_commands* commands"),
        E("world_submit_input", nameof(AbiEntries.WorldSubmitInput), (nint)(delegate* unmanaged<nint, uint, uint, int, void*, int, long, int>)&AbiEntries.WorldSubmitInput, "Submits an Input Command (spec section 7.3): per-source sequences must increase; effective_tick -1 = the next Tick.", "colony_world* world", "uint32_t source", "uint32_t sequence", "int32_t type", "const void* payload", "int32_t size", "int64_t effective_tick"),
        E("world_definitions", nameof(AbiEntries.WorldDefinitions), (nint)(delegate* unmanaged<nint, byte*, int*, int*, void**, int>)&AbiEntries.WorldDefinitions, "The baked table of a Definition type: count values of stride bytes (immutable for the World's lifetime).", "const colony_world* world", "const char* type_name", "int32_t* out_count", "int32_t* out_stride", "const void** out_values"),
        E("world_definition_find", nameof(AbiEntries.WorldDefinitionFind), (nint)(delegate* unmanaged<nint, byte*, byte*, int*, int>)&AbiEntries.WorldDefinitionFind, "Index of the Definition named name in the type's table.", "const colony_world* world", "const char* type_name", "const char* name", "int32_t* out_index"),
        E("world_definition_string", nameof(AbiEntries.WorldDefinitionString), (nint)(delegate* unmanaged<nint, uint, byte*, int, int*, int>)&AbiEntries.WorldDefinitionString, "UTF-8 text of a COLONY_FIELD_STRING id; out_length is the full length even when truncated.", "const colony_world* world", "uint32_t string_id", "char* buffer", "int32_t capacity", "int32_t* out_length"),
        E("world_destroy", nameof(AbiEntries.WorldDestroy), (nint)(delegate* unmanaged<nint, int>)&AbiEntries.WorldDestroy, "Disposes the World and its command buffers.", "colony_world* world"),
        // commands
        E("commands_create_entity", nameof(AbiEntries.CommandsCreateEntity), (nint)(delegate* unmanaged<nint, ulong*, int>)&AbiEntries.CommandsCreateEntity, "Records a Create; out_deferred is a handle usable in this buffer and resolvable after playback.", "colony_commands* commands", "uint64_t* out_deferred"),
        E("commands_destroy_entity", nameof(AbiEntries.CommandsDestroyEntity), (nint)(delegate* unmanaged<nint, ulong, int>)&AbiEntries.CommandsDestroyEntity, null, "colony_commands* commands", "uint64_t entity"),
        E("commands_add", nameof(AbiEntries.CommandsAdd), (nint)(delegate* unmanaged<nint, ulong, int, void*, int, int>)&AbiEntries.CommandsAdd, "Records an Add (or overwrite) of size bytes; the only way to write a Component.", "colony_commands* commands", "uint64_t entity", "int32_t component", "const void* value", "int32_t size"),
        E("commands_remove", nameof(AbiEntries.CommandsRemove), (nint)(delegate* unmanaged<nint, ulong, int, int>)&AbiEntries.CommandsRemove, null, "colony_commands* commands", "uint64_t entity", "int32_t component"),
        E("commands_emit", nameof(AbiEntries.CommandsEmit), (nint)(delegate* unmanaged<nint, int, void*, int, int>)&AbiEntries.CommandsEmit, "Records an event of size bytes for queue (a System must list it in emits).", "colony_commands* commands", "int32_t queue", "const void* value", "int32_t size"),
        E("commands_resolve", nameof(AbiEntries.CommandsResolve), (nint)(delegate* unmanaged<nint, ulong, ulong*, int>)&AbiEntries.CommandsResolve, "The real Entity id of a deferred handle after the buffer's playback.", "colony_commands* commands", "uint64_t deferred", "uint64_t* out_entity"),
        // context
        E("context_events", nameof(AbiEntries.ContextEvents), (nint)(delegate* unmanaged<ColonyContext*, int, void**, int*, int>)&AbiEntries.ContextEvents, "The pending events of a queue the System listed in reads_resources or emits.", "const colony_context* context", "int32_t queue", "const void** out_data", "int32_t* out_count"),
        E("context_input_count", nameof(AbiEntries.ContextInputCount), (nint)(delegate* unmanaged<ColonyContext*, int*, int>)&AbiEntries.ContextInputCount, "Entries in this Tick's canonical Input Command list (all types).", "const colony_context* context", "int32_t* out_count"),
        E("context_input", nameof(AbiEntries.ContextInput), (nint)(delegate* unmanaged<ColonyContext*, int, ColonyInput*, int>)&AbiEntries.ContextInput, "One canonical entry; filter on type. random is the command's own stream (counter = use index).", "const colony_context* context", "int32_t index", "colony_input* out_input"),
        E("context_reject_input", nameof(AbiEntries.ContextRejectInput), (nint)(delegate* unmanaged<ColonyContext*, int, int, int>)&AbiEntries.ContextRejectInput, "Rejects entry index deterministically (a CommandRejected event through this job's commands).", "const colony_context* context", "int32_t index", "int32_t reason"),
        // random
        E("random_derive", nameof(AbiEntries.RandomDerive), (nint)(delegate* unmanaged<ColonyRandom*, ulong, ColonyRandom*, int>)&AbiEntries.RandomDerive, "An independent stream keyed by key (an Entity id, a System id, ...).", "const colony_random* random", "uint64_t key", "colony_random* out_random"),
        E("random_u32", nameof(AbiEntries.RandomU32), (nint)(delegate* unmanaged<ColonyRandom*, ulong, uint*, int>)&AbiEntries.RandomU32, "The counter-th 32-bit draw of the stream.", "const colony_random* random", "uint64_t counter", "uint32_t* out_value"),
        E("random_i32", nameof(AbiEntries.RandomI32), (nint)(delegate* unmanaged<ColonyRandom*, ulong, int, int, int*, int>)&AbiEntries.RandomI32, "A draw in [min_inclusive, max_exclusive).", "const colony_random* random", "uint64_t counter", "int32_t min_inclusive", "int32_t max_exclusive", "int32_t* out_value"),
        // errors
        E("last_error", nameof(AbiEntries.LastError), (nint)(delegate* unmanaged<byte*, int, int*, int>)&AbiEntries.LastError, "The message of the last failing call on this thread, UTF-8, NUL-terminated when it fits.", "char* buffer", "int32_t capacity", "int32_t* out_length"),
        // registration (appended: order is ABI)
        E("register_component_data", nameof(AbiEntries.RegisterComponentData), (nint)(delegate* unmanaged<nint, int, ColonyComponentRange*, int, int>)&AbiEntries.RegisterComponentData, "Declares the data bytes of a Component registered by layout; every other byte is padding and is zeroed where values enter the World (the World hash excludes padding). Without it a value's bytes are taken verbatim, so zero your padding.", "colony_registration* registration", "int32_t component", "const colony_component_range* ranges", "int32_t count"),
    ];

    private static AbiEntry E(string name, string methodName, nint address, string? summary, params string[] parameters)
        => new(name, methodName, address, summary ?? string.Empty, [.. parameters]);
}
