/*
 * The C ABI conformance suite (spec sections 8 and 10.3; TASK-28). One source, two builds:
 *
 *   - a shared library: exports colony_plugin_get (a Native Plugin with three Systems: an Input handler, a chunk
 *     job and an End-Phase summariser) and colony_conformance_run (a C host that builds a schema with that Plugin,
 *     drives Worlds through the function table and records which entries it exercised). The managed tests load
 *     it in-process on JIT and hand it Colony.Plugins' table; the determinism harness loads only the Plugin.
 *   - with CONFORMANCE_STANDALONE, an executable that loads the published Native AOT Colony.Native, asks it for
 *     the table with colony_get_api and runs the same host.
 *
 * Every api call goes through CALL(), which marks the entry's index; colony_conformance_run reports the marks so
 * the test can assert that every entry of the table was exercised (AC #2). Determinism rules apply to the Plugin:
 * integer arithmetic only, randomness from the streams the context hands out, keyed by Entity, never by thread.
 */
#include "colony.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#ifdef _WIN32
#define CONFORMANCE_EXPORT __declspec(dllexport)
#else
#define CONFORMANCE_EXPORT __attribute__((visibility("default")))
#endif

/* ---- the Plugin ---- */

typedef struct { int32_t x, y; } position;              /* native.position  8/4 */
typedef struct { int32_t dx, dy; } velocity;            /* native.velocity  8/4 */
typedef struct { int32_t bound, pad; } config;          /* native.config    8/4 value Resource */
typedef struct { uint64_t entity; } moved;              /* native.moved     8/8 transient event */
typedef struct { int32_t count, tick; } summary;        /* native.summary   8/4 persistent event */
typedef struct { int32_t count, species; } spawn;       /* native.spawn     8/4 Input Command */
typedef struct { int32_t speed, mass_q16; uint32_t label; } species; /* native.species 12/4 Definition */

static const char* const species_json =
    "{ \"native.species\": {"
    "    \"hare\":     { \"speed\": 7, \"mass\": 1.5,   \"label\": \"Hare\" },"
    "    \"tortoise\": { \"speed\": 2, \"mass\": 40.25, \"label\": \"Tortoise\" }"
    "} }";

static const colony_api* g_api;
static uint8_t g_covered[COLONY_API_COUNT];
static char g_error[1024];

typedef struct plugin_ids {
    int32_t position, velocity, config, moved, summary, spawn;
} plugin_ids;

static plugin_ids g_ids;

#define CALL(INDEX, name, ...) (g_covered[COLONY_API_##INDEX] = 1, g_api->name(__VA_ARGS__))

static int fail(const char* what, int status)
{
    char detail[512] = {0};
    int32_t length = 0;
    CALL(LAST_ERROR, last_error, detail, (int32_t)sizeof detail, &length);
    snprintf(g_error, sizeof g_error, "%s returned %d: %s", what, status, detail);
    return status == COLONY_OK ? COLONY_ERROR : status;
}

#define CHECK(expr) do { int status_ = (expr); if (status_ != COLONY_OK) return fail(#expr, status_); } while (0)
#define EXPECT(cond) do { if (!(cond)) { snprintf(g_error, sizeof g_error, "expectation failed: %s (line %d)", #cond, __LINE__); return COLONY_ERROR; } } while (0)

/* Input Phase, Begin hook: turns each spawn command into Entities with a position and the species' velocity. */
static int spawn_begin(void* user_data, const colony_context* ctx)
{
    (void)user_data;
    int32_t count = 0, species_count = 0, stride = 0;
    const void* table = NULL;
    CHECK(CALL(CONTEXT_INPUT_COUNT, context_input_count, ctx, &count));
    CHECK(CALL(WORLD_DEFINITIONS, world_definitions, ctx->world, "native.species", &species_count, &stride, &table));
    for (int32_t i = 0; i < count; i++) {
        colony_input input;
        CHECK(CALL(CONTEXT_INPUT, context_input, ctx, i, &input));
        if (input.type != g_ids.spawn) {
            continue;
        }

        spawn command;
        EXPECT(input.size == (int32_t)sizeof command);
        memcpy(&command, input.payload, sizeof command);
        if (command.count > 100) {
            CHECK(CALL(CONTEXT_REJECT_INPUT, context_reject_input, ctx, i, 1));
            continue;
        }

        if (command.count < 0 || species_count == 0) {
            CHECK(CALL(CONTEXT_REJECT_INPUT, context_reject_input, ctx, i, 2));
            continue;
        }

        const species* kind = (const species*)((const char*)table + (size_t)(command.species % species_count) * (size_t)stride);
        for (int32_t n = 0; n < command.count; n++) {
            uint64_t entity;
            position p;
            velocity v;
            CHECK(CALL(COMMANDS_CREATE_ENTITY, commands_create_entity, ctx->commands, &entity));
            CHECK(CALL(RANDOM_I32, random_i32, &input.random, (uint64_t)(2 * n), -50, 50, &p.x));
            CHECK(CALL(RANDOM_I32, random_i32, &input.random, (uint64_t)(2 * n + 1), -50, 50, &p.y));
            v.dx = kind->speed;
            v.dy = kind->speed / 2;
            CHECK(CALL(COMMANDS_ADD, commands_add, ctx->commands, entity, g_ids.position, &p, (int32_t)sizeof p));
            CHECK(CALL(COMMANDS_ADD, commands_add, ctx->commands, entity, g_ids.velocity, &v, (int32_t)sizeof v));
        }
    }

    return COLONY_OK;
}

/* Simulate Phase, chunk job: moves every Entity with a per-Entity jitter; writes only through commands. */
static int move_chunk(void* user_data, const colony_context* ctx, const colony_chunk* chunk)
{
    (void)user_data;
    EXPECT(chunk->column_count == 2);
    const position* positions = (const position*)chunk->columns[0].data;
    const velocity* velocities = (const velocity*)chunk->columns[1].data;
    EXPECT(positions != NULL && velocities != NULL);
    EXPECT(chunk->columns[0].stride == (int32_t)sizeof(position) && chunk->columns[1].stride == (int32_t)sizeof(velocity));
    for (int32_t i = 0; i < chunk->count; i++) {
        uint64_t entity = chunk->entities[i];
        colony_random stream;
        uint32_t draw;
        CHECK(CALL(RANDOM_DERIVE, random_derive, &ctx->random, entity, &stream));
        CHECK(CALL(RANDOM_U32, random_u32, &stream, 0, &draw));
        position p = positions[i];
        p.x += velocities[i].dx + ((int32_t)(draw % 3u) - 1);
        p.y += velocities[i].dy;
        CHECK(CALL(COMMANDS_ADD, commands_add, ctx->commands, entity, g_ids.position, &p, (int32_t)sizeof p));
        moved m = { entity };
        CHECK(CALL(COMMANDS_EMIT, commands_emit, ctx->commands, g_ids.moved, &m, (int32_t)sizeof m));
        if (p.x > 120 || p.x < -120) {
            CHECK(CALL(COMMANDS_REMOVE, commands_remove, ctx->commands, entity, g_ids.velocity));
        }

        if (p.y > 90 || p.y < -90) {
            CHECK(CALL(COMMANDS_DESTROY_ENTITY, commands_destroy_entity, ctx->commands, entity));
        }
    }

    return COLONY_OK;
}

/* End Phase, Begin hook: counts this Tick's moved events into one persistent summary event. */
static int summarize_begin(void* user_data, const colony_context* ctx)
{
    (void)user_data;
    const void* data = NULL;
    int32_t count = 0;
    CHECK(CALL(CONTEXT_EVENTS, context_events, ctx, g_ids.moved, &data, &count));
    summary s = { count, (int32_t)ctx->tick };
    CHECK(CALL(COMMANDS_EMIT, commands_emit, ctx->commands, g_ids.summary, &s, (int32_t)sizeof s));
    return COLONY_OK;
}

static int plugin_register(void* user_data, colony_registration* r)
{
    (void)user_data;
    int32_t found;
    CHECK(CALL(REGISTER_COMPONENT, register_component, r, "native.position", (int32_t)sizeof(position), 4, 0, &g_ids.position));
    CHECK(CALL(REGISTER_COMPONENT, register_component, r, "native.velocity", (int32_t)sizeof(velocity), 4, 0, &g_ids.velocity));
    {
        /* position has no padding: declaring its data bytes changes nothing but documents the call. */
        const colony_component_range position_data[2] = { { 0, 4 }, { 4, 4 } };
        CHECK(CALL(REGISTER_COMPONENT_DATA, register_component_data, r, g_ids.position, position_data, 2));
    }
    CHECK(CALL(REGISTER_RESOURCE, register_resource, r, "native.config", (int32_t)sizeof(config), 4, 0, &g_ids.config));
    CHECK(CALL(REGISTER_EVENT, register_event, r, "native.moved", (int32_t)sizeof(moved), 8, 0, &g_ids.moved));
    CHECK(CALL(REGISTER_EVENT, register_event, r, "native.summary", (int32_t)sizeof(summary), 4, COLONY_EVENT_PERSISTENT, &g_ids.summary));
    CHECK(CALL(REGISTER_INPUT, register_input, r, "native.spawn", (int32_t)sizeof(spawn), 4, &g_ids.spawn));

    colony_definition_field fields[3] = {
        { "speed", COLONY_FIELD_INT32, 0 },
        { "mass", COLONY_FIELD_FIXED32, 4 },
        { "label", COLONY_FIELD_STRING, 8 },
    };
    CHECK(CALL(REGISTER_DEFINITION_TYPE, register_definition_type, r, "native.species", (int32_t)sizeof(species), 4, fields, 3));
    CHECK(CALL(REGISTER_DEFINITION_SOURCE, register_definition_source, r, species_json, (int32_t)strlen(species_json), "conformance.c"));

    CHECK(CALL(REGISTER_FIND_COMPONENT, register_find_component, r, "native.position", &found));
    EXPECT(found == g_ids.position);
    CHECK(CALL(REGISTER_FIND_RESOURCE, register_find_resource, r, "native.moved", &found));
    EXPECT(found == g_ids.moved);
    CHECK(CALL(REGISTER_FIND_INPUT, register_find_input, r, "native.spawn", &found));
    EXPECT(found == g_ids.spawn);
    EXPECT(CALL(REGISTER_FIND_COMPONENT, register_find_component, r, "native.nothing", &found) == COLONY_NOT_FOUND);

    colony_system_desc spawn_system = {0};
    spawn_system.name = "native.spawn";
    spawn_system.phase = "Input";
    spawn_system.begin = spawn_begin;
    CHECK(CALL(REGISTER_SYSTEM, register_system, r, &spawn_system));

    int32_t reads[2] = { g_ids.position, g_ids.velocity };
    int32_t move_emits[1] = { g_ids.moved };
    colony_system_desc move_system = {0};
    move_system.name = "native.move";
    move_system.reads = reads;
    move_system.reads_count = 2;
    move_system.emits = move_emits;
    move_system.emits_count = 1;
    move_system.for_each_chunk = move_chunk;
    CHECK(CALL(REGISTER_SYSTEM, register_system, r, &move_system));

    int32_t reads_resources[1] = { g_ids.moved };
    int32_t emits[1] = { g_ids.summary };
    colony_system_desc summarize_system = {0};
    summarize_system.name = "native.summarize";
    summarize_system.phase = "End";
    summarize_system.reads_resources = reads_resources;
    summarize_system.reads_resources_count = 1;
    summarize_system.emits = emits;
    summarize_system.emits_count = 1;
    summarize_system.begin = summarize_begin;
    CHECK(CALL(REGISTER_SYSTEM, register_system, r, &summarize_system));
    return COLONY_OK;
}

/* The Native Plugin entry point: the host (managed or C) asks for the descriptor and hands over the table. */
CONFORMANCE_EXPORT int colony_plugin_get(const colony_api* api, colony_plugin_desc* out)
{
    if (api == NULL || out == NULL || api->version != COLONY_ABI_VERSION) {
        return COLONY_VERSION_MISMATCH;
    }

    g_api = api;
    memset(out, 0, sizeof *out);
    out->abi_version = COLONY_ABI_VERSION;
    out->deterministic = 1;
    out->id = "conformance.native";
    out->version = "1.0.0";
    out->register_fn = plugin_register;
    return COLONY_OK;
}

/* ---- the C host ---- */

static int run_host(int32_t workers, const char* save_path, uint64_t* out_hash)
{
    colony_host* host = NULL;
    colony_schema* schema = NULL;
    colony_world* world = NULL;
    colony_world* loaded = NULL;
    colony_plugin_desc desc;

    CHECK(CALL(HOST_CREATE, host_create, &host));
    CHECK(colony_plugin_get(g_api, &desc));
    CHECK(CALL(HOST_ADD_PLUGIN, host_add_plugin, host, &desc));
    CHECK(CALL(HOST_BUILD, host_build, host, &schema));
    CHECK(CALL(HOST_DESTROY, host_destroy, host));

    /* Schema introspection: every table, every entry, by index and by name. */
    uint64_t schema_hash = 0;
    int32_t count = 0, id = -1;
    colony_type_info type;
    colony_system_info system;
    colony_plugin_info plugin;
    CHECK(CALL(SCHEMA_HASH, schema_hash, schema, &schema_hash));
    EXPECT(schema_hash != 0);
    CHECK(CALL(SCHEMA_COMPONENT_COUNT, schema_component_count, schema, &count));
    EXPECT(count >= 2);
    for (int32_t i = 0; i < count; i++) {
        CHECK(CALL(SCHEMA_COMPONENT, schema_component, schema, i, &type));
        EXPECT(type.id == i && type.name != NULL && type.size > 0);
    }

    CHECK(CALL(SCHEMA_RESOURCE_COUNT, schema_resource_count, schema, &count));
    for (int32_t i = 0; i < count; i++) {
        CHECK(CALL(SCHEMA_RESOURCE, schema_resource, schema, i, &type));
        EXPECT(type.id == i && type.name != NULL);
        if (i == g_ids.summary) {
            EXPECT((type.flags & COLONY_RESOURCE_IS_EVENT) && (type.flags & COLONY_RESOURCE_IS_PERSISTENT));
        }
    }

    CHECK(CALL(SCHEMA_INPUT_COUNT, schema_input_count, schema, &count));
    EXPECT(count == 1);
    CHECK(CALL(SCHEMA_INPUT, schema_input, schema, 0, &type));
    EXPECT(strcmp(type.name, "native.spawn") == 0 && type.size == (int32_t)sizeof(spawn));
    CHECK(CALL(SCHEMA_SYSTEM_COUNT, schema_system_count, schema, &count));
    EXPECT(count == 3);
    for (int32_t i = 0; i < count; i++) {
        CHECK(CALL(SCHEMA_SYSTEM, schema_system, schema, i, &system));
        EXPECT(system.id == i && system.name != NULL && system.phase != NULL && system.every == 1);
    }

    CHECK(CALL(SCHEMA_PLUGIN_COUNT, schema_plugin_count, schema, &count));
    EXPECT(count == 1);
    CHECK(CALL(SCHEMA_PLUGIN, schema_plugin, schema, 0, &plugin));
    EXPECT(strcmp(plugin.id, "conformance.native") == 0 && plugin.abi_version == COLONY_ABI_VERSION && plugin.deterministic == 1);
    CHECK(CALL(SCHEMA_FIND_COMPONENT, schema_find_component, schema, "native.velocity", &id));
    EXPECT(id == g_ids.velocity);
    CHECK(CALL(SCHEMA_FIND_RESOURCE, schema_find_resource, schema, "native.config", &id));
    EXPECT(id == g_ids.config);
    CHECK(CALL(SCHEMA_FIND_INPUT, schema_find_input, schema, "native.spawn", &id));
    EXPECT(id == g_ids.spawn);
    EXPECT(CALL(SCHEMA_FIND_INPUT, schema_find_input, schema, "native.nothing", &id) == COLONY_NOT_FOUND);

    /* A World, host-side writes through a command buffer, Input Commands, Ticks. */
    CHECK(CALL(WORLD_CREATE, world_create, schema, 0x5EEDull, workers, 0, &world));
    colony_random root, derived;
    uint32_t draw_a, draw_b;
    CHECK(CALL(WORLD_RANDOM, world_random, world, &root));
    CHECK(CALL(RANDOM_DERIVE, random_derive, &root, 42, &derived));
    CHECK(CALL(RANDOM_U32, random_u32, &derived, 0, &draw_a));
    CHECK(CALL(RANDOM_U32, random_u32, &derived, 0, &draw_b));
    EXPECT(draw_a == draw_b);

    colony_commands* commands = NULL;
    uint64_t deferred = 0, entity = 0;
    position start = { 1, 2 };
    velocity still = { 0, 0 };
    CHECK(CALL(WORLD_CREATE_COMMANDS, world_create_commands, world, &commands));
    CHECK(CALL(COMMANDS_CREATE_ENTITY, commands_create_entity, commands, &deferred));
    CHECK(CALL(COMMANDS_ADD, commands_add, commands, deferred, g_ids.position, &start, (int32_t)sizeof start));
    CHECK(CALL(COMMANDS_ADD, commands_add, commands, deferred, g_ids.velocity, &still, (int32_t)sizeof still));
    CHECK(CALL(WORLD_PLAYBACK, world_playback, world, commands));
    CHECK(CALL(COMMANDS_RESOLVE, commands_resolve, commands, deferred, &entity));
    int32_t alive = 0, has = 0;
    CHECK(CALL(WORLD_IS_ALIVE, world_is_alive, world, entity, &alive));
    EXPECT(alive == 1);
    CHECK(CALL(WORLD_HAS, world_has, world, entity, g_ids.velocity, &has));
    EXPECT(has == 1);
    position read_back = { 0, 0 };
    CHECK(CALL(WORLD_GET_COMPONENT, world_get_component, world, entity, g_ids.position, &read_back, (int32_t)sizeof read_back));
    EXPECT(read_back.x == 1 && read_back.y == 2);
    EXPECT(CALL(WORLD_GET_COMPONENT, world_get_component, world, entity, g_ids.position, &read_back, 3) == COLONY_INVALID_ARGUMENT);
    char message[256] = {0};
    int32_t message_length = 0;
    CHECK(CALL(LAST_ERROR, last_error, message, (int32_t)sizeof message, &message_length));
    EXPECT(message_length > 0 && message[0] != '\0');

    for (int32_t t = 0; t < 20; t++) {
        if (t < 3) {
            spawn command = { 5 + t, t };
            CHECK(CALL(WORLD_SUBMIT_INPUT, world_submit_input, world, 1, (uint32_t)(t + 1), g_ids.spawn, &command, (int32_t)sizeof command, -1));
        }

        if (t == 1) {
            spawn rejected = { 500, 0 };
            CHECK(CALL(WORLD_SUBMIT_INPUT, world_submit_input, world, 2, 1, g_ids.spawn, &rejected, (int32_t)sizeof rejected, -1));
        }

        int32_t ticked = 0;
        int64_t current = -1;
        CHECK(CALL(WORLD_TICK, world_tick, world, &ticked));
        EXPECT(ticked == 1);
        CHECK(CALL(WORLD_CURRENT_TICK, world_current_tick, world, &current));
        EXPECT(current == t + 1);
    }

    CHECK(CALL(WORLD_ENTITY_COUNT, world_entity_count, world, &count));
    EXPECT(count > 1);
    config configuration = { -1, -1 };
    CHECK(CALL(WORLD_GET_RESOURCE, world_get_resource, world, g_ids.config, &configuration, (int32_t)sizeof configuration));
    EXPECT(configuration.bound == 0 && configuration.pad == 0);
    const void* events = NULL;
    CHECK(CALL(WORLD_EVENTS, world_events, world, g_ids.summary, &events, &count));
    EXPECT(count == 1 && events != NULL);
    summary last;
    memcpy(&last, events, sizeof last);
    EXPECT(last.tick == 19 && last.count > 0);

    /* Definitions: the baked table, lookups by name, interned text. */
    int32_t species_count = 0, stride = 0, index = -1, label_length = 0;
    const void* table = NULL;
    char label[32] = {0};
    CHECK(CALL(WORLD_DEFINITIONS, world_definitions, world, "native.species", &species_count, &stride, &table));
    EXPECT(species_count == 2 && stride == (int32_t)sizeof(species) && table != NULL);
    CHECK(CALL(WORLD_DEFINITION_FIND, world_definition_find, world, "native.species", "tortoise", &index));
    EXPECT(index >= 0 && index < 2);
    species tortoise;
    memcpy(&tortoise, (const char*)table + (size_t)index * (size_t)stride, sizeof tortoise);
    EXPECT(tortoise.speed == 2 && tortoise.mass_q16 == 40 * 65536 + 16384);
    CHECK(CALL(WORLD_DEFINITION_STRING, world_definition_string, world, tortoise.label, label, (int32_t)sizeof label, &label_length));
    EXPECT(label_length == 8 && strcmp(label, "Tortoise") == 0);
    EXPECT(CALL(WORLD_DEFINITION_FIND, world_definition_find, world, "native.species", "unicorn", &index) == COLONY_NOT_FOUND);

    /* Save, load, hash: the loaded World is the saved one and continues identically. */
    uint64_t hash = 0, loaded_hash = 0;
    CHECK(CALL(WORLD_HASH, world_hash, world, &hash));
    CHECK(CALL(WORLD_SAVE, world_save, world, save_path));
    CHECK(CALL(WORLD_LOAD, world_load, schema, save_path, &loaded));
    CHECK(CALL(WORLD_HASH, world_hash, loaded, &loaded_hash));
    EXPECT(loaded_hash == hash);
    int32_t ticked = 0;
    CHECK(CALL(WORLD_TICK, world_tick, world, &ticked));
    CHECK(CALL(WORLD_TICK, world_tick, loaded, &ticked));
    CHECK(CALL(WORLD_HASH, world_hash, world, &hash));
    CHECK(CALL(WORLD_HASH, world_hash, loaded, &loaded_hash));
    EXPECT(loaded_hash == hash);

    CHECK(CALL(WORLD_DESTROY, world_destroy, loaded));
    CHECK(CALL(WORLD_DESTROY, world_destroy, world));
    CHECK(CALL(SCHEMA_DESTROY, schema_destroy, schema));
    *out_hash = hash;
    return COLONY_OK;
}

/*
 * Runs the host against the given table. out_covered receives COLONY_API_COUNT bytes (1 = the entry was called);
 * on failure the status is returned and error (error_capacity bytes) describes the first failing call.
 */
CONFORMANCE_EXPORT int colony_conformance_run(const colony_api* api, int32_t workers, const char* save_path, uint64_t* out_hash, uint8_t* out_covered, char* error, int32_t error_capacity)
{
    if (api == NULL || out_hash == NULL || out_covered == NULL || save_path == NULL) {
        return COLONY_INVALID_ARGUMENT;
    }

    if (api->version != COLONY_ABI_VERSION || api->size != (uint32_t)sizeof(colony_api)) {
        return COLONY_VERSION_MISMATCH;
    }

    g_api = api;
    g_error[0] = '\0';
    memset(g_covered, 0, sizeof g_covered);
    int status = run_host(workers, save_path, out_hash);
    memcpy(out_covered, g_covered, sizeof g_covered);
    if (error != NULL && error_capacity > 0) {
        snprintf(error, (size_t)error_capacity, "%s", g_error);
    }

    return status;
}

#ifdef CONFORMANCE_STANDALONE
#ifdef _WIN32
#include <windows.h>
static colony_get_api_fn load_get_api(const char* path)
{
    HMODULE module = LoadLibraryA(path);
    return module == NULL ? NULL : (colony_get_api_fn)(void*)GetProcAddress(module, "colony_get_api");
}
#else
#include <dlfcn.h>
static colony_get_api_fn load_get_api(const char* path)
{
    void* module = dlopen(path, RTLD_NOW);
    return module == NULL ? NULL : (colony_get_api_fn)dlsym(module, "colony_get_api");
}
#endif

/* conformance <Colony.Native library> <save path> [workers]; prints "hash 0x<16 hex> covered <n>/<count>". */
int main(int argc, char** argv)
{
    if (argc < 3) {
        fprintf(stderr, "usage: conformance <colony-native-library> <save-path> [workers]\n");
        return 2;
    }

    colony_get_api_fn get_api = load_get_api(argv[1]);
    if (get_api == NULL) {
        fprintf(stderr, "cannot load colony_get_api from %s\n", argv[1]);
        return 2;
    }

    const colony_api* api = NULL;
    if (get_api(999u, &api) != COLONY_VERSION_MISMATCH) {
        fprintf(stderr, "colony_get_api accepted ABI version 999\n");
        return 1;
    }

    if (get_api(COLONY_ABI_VERSION, &api) != COLONY_OK || api == NULL) {
        fprintf(stderr, "colony_get_api refused ABI version %u\n", COLONY_ABI_VERSION);
        return 1;
    }

    uint64_t hash = 0;
    uint8_t covered[COLONY_API_COUNT];
    char error[1024];
    int workers = argc > 3 ? atoi(argv[3]) : 1;
    int status = colony_conformance_run(api, workers, argv[2], &hash, covered, error, (int32_t)sizeof error);
    if (status != COLONY_OK) {
        fprintf(stderr, "conformance failed with status %d: %s\n", status, error);
        return 1;
    }

    int exercised = 0;
    for (int i = 0; i < COLONY_API_COUNT; i++) {
        exercised += covered[i];
    }

    printf("hash 0x%016llX covered %d/%d\n", (unsigned long long)hash, exercised, (int)COLONY_API_COUNT);
    return 0;
}
#endif
