---
id: doc-1
title: 'Colony Framework architecture spec (v1), part 1 of 2'
type: specification
created_date: '2026-10-31 01:46'
updated_date: '2026-10-31 01:46'
tags:
  - architecture
  - spec
  - v1
---
# Colony Framework: architecture spec (v1)

Status: draft for implementation hand-off, assembled from the closed decision tickets under the Wayfinder map "Map: Colony Framework architecture". Each section names the ticket that holds the detail and its reasoning. Prototype evidence lives in `docs/research/`. Terms follow `GLOSSARY.md`.

## 1. What this is

Colony Framework is a headless, modern C# (.NET 11, Native AOT-friendly) framework for Dwarf Fortress / RimWorld-style colony simulations. Game authors build games on it; it is not a game, and it is closer to MonoGame than Unity: a library, not an engine with an editor.

It provides: an archetype ECS, chunked memory and Fields, a deterministic parallel scheduler, save/load and replay, a Plugin model with a C ABI, colony-sim building blocks (pathfinding, jobs/AI, procedural generation), and development tooling (diagnostics, inspection, test harnesses).

Scale targets: 100k+ Entities (1M for stress), a bounded World around 512x512x256 tiles, 16 ms Tick budget on a desktop CPU.

### v1 non-goals

- Rendering, input devices, audio, UI (headless); a sample game.
- GPU compute. v1 is CPU-only (section 12).
- A scripting language or mod format (only extension points).
- Networking implementation (only enablers, section 7.4).
- Unbounded/infinite Worlds; rollback netcode; state-replication networking.
- Behavior trees and GOAP/HTN (they can be Plugin libraries on the same building blocks).
- Navmesh/any-angle pathfinding, crowd avoidance, steering, multi-agent collision.
- macOS and ARM64 as guaranteed platforms (best effort, tested in CI).

## 2. Principles

1. **Determinism is the contract.** Tier 1: for the same Framework build, Plugin set, seed and input log, World state is bit-identical every Tick across thread counts, CPU instruction sets and Windows/Linux x86-64 (section 6).
2. **No managed allocation in a steady-state Tick.** Hot data is unmanaged; `unsafe` is confined to Framework internals.
3. **Building blocks, not a blessed game model.** The Framework supplies mechanisms (claims, work index, queries); Game authors supply content and policy.
4. **Data in, data out.** Components, Fields, Resources and commands are plain unmanaged structs, so they can be saved, hashed, replayed, inspected and passed through a C ABI.
5. **Measured, not assumed.** Design choices were validated by throwaway prototypes (section 13).
6. **Two extension surfaces only:** the C# API and the C ABI.

## 3. Packages and dependency direction

Decided in "Decide package and assembly layout and public API shape". All packages are .NET 11 only, lockstep-versioned (SemVer, 0.x unstable), `IsAotCompatible` and `IsTrimmable` with warnings as errors.

| Package | Contents |
|---|---|
| `Colony.Core` | Entity/Component storage, memory model, scheduler, command buffers, Resources, events, Relations, World/Tick API, math layer, fixed-point, RNG, hashing. One package on purpose: ECS, memory and scheduler are tightly coupled. |
| `Colony.Fields` | Field chunks, spatial index, change feed, ChunkProvider, streaming residency manager. |
| `Colony.Persistence` | Save/load container, migrations, replay log and verifier, Input Command ingestion and wire form. |
| `Colony.Pathfinding` | Region IDs, chunk graph and HPA*, flow Fields, request queue. |
| `Colony.Colony` | Claims, work index, hauling matcher, priority-table and utility helpers. |
| `Colony.ProcGen` | Stage pipeline, noise utilities, region/halo machinery; optional graph toolkit and WFC helper. |
| `Colony.Plugins` | Plugin manifest and loader, Definition registry, Overridable Systems, C ABI host. |
| `Colony.Abstractions` | Only contracts Plugins or separately versioned packages compile against. |
| `Colony.Diagnostics` | Profiling sinks (EventSource/Meter, Perfetto trace), logging drain, inspection server, `colony-inspect` CLI. |
| `Colony.Generators` | Roslyn incremental source generator and analyzers (netstandard2.0). |
| `Colony.Testing` | Determinism harness, allocation harness, test content package, ABI conformance tooling. |
| `Colony.Native` | AOT shared library exposing the C ABI, plus checked-in generated `colony.h`. |
| `Colony` | Meta-package referencing the common set. |

Dependency rules, enforced by a CI dependency check:

- `Core` depends only on the BCL.
- `Fields`, `Persistence`, `Plugins` depend on `Core`.
- `Pathfinding`, `Colony`, `ProcGen` depend on `Core` and `Fields` (and `Persistence` only where they save state).
- `Diagnostics` plugs into `Core` through hook interfaces and struct callbacks; `Core` never references it.
- Nothing depends on `Diagnostics`, `Testing` or `Plugins`, so a Game can run without the Plugin machinery.

Public vs internal: the public API is safe by default (typed handles, ref structs, Span views, generated typed queries). `unsafe` appears only in `internal` code, plus a small documented `[Experimental]` `Colony.Unsafe` namespace (raw chunk pointers for custom SIMD). `InternalsVisibleTo` is for tests only. No runtime reflection anywhere; no static state or singletons, so many Worlds can coexist in a process.

## 4. Core: data model, memory, scheduling

### 4.1 Data model ("Decide Entity and Component data model")

- **Storage:** archetype/chunk structure-of-arrays with 64-byte aligned columns. Opt-in non-fragmenting mode for Components that flip often.
- **Entity ID:** 64-bit (32-bit index, 32-bit generation), stable across save/load; stale IDs fail the generation check.
- **Frequent state:** enable bits or data fields are the standard; add/remove tag Components only for rare structural traits.
- **References and Relations:** plain `EntityId` fields in unmanaged Components; a built-in non-fragmenting relation index for reverse lookup; parent/child hierarchy is a built-in Relation with optional cascade delete. Claim is a built-in Relation (section 9.2).
- **Structural change:** during a Tick only through per-System command buffers merged at Phase boundaries in System order then record order. Bulk playback paths and deferred Entity handles from Create. Conflicting ops on one Entity in a Phase resolve first-in-stable-order; the loser gets a deterministic failure event the next Phase.
- **Queries:** source-generated typed queries with declared read/write access feeding the scheduler. Chunk-level iteration is the fast path; per-Entity ref access sits on top.
- **Variable data:** inline fixed arrays; buffer Components (inline then overflow to a World-owned size-class allocator); items as Entities.
- **Identity:** Component identity is a stable registered name plus a layout hash, not the CLR type.
- **Resources:** first-class World-owned singleton unmanaged structs in a keyed store, saved with the World, with declared access.

### 4.2 Memory ("Decide memory model")

- 64 KiB chunks, 64-byte aligned columns padded to a multiple of 16 elements independent of CPU vector width; layout never depends on the CPU.
- World reserves ~4 MiB native slabs carved into chunks (free list); a per-Tick scratch bump arena (reset each Tick) for command buffers and temporaries; a configurable World memory budget; allocation only at sync points.
- Budgets in v1: World memory, pathfinding Field memory (LRU) and resident-chunk count (streaming eviction).
- Release builds are unchecked. Debug builds validate every ref/Span against the chunk structural version, poison freed memory and report leaks on dispose. Rule: "a ref is valid until the next structural change."
- Interned string table (32-bit `StringId`, deterministic order) and an immutable blob store (`BlobRef<T>`), both saved with the World.
- Zero managed allocation per steady-state Tick, enforced by a harness (section 11.3) and an analyzer. Guidance: Workstation non-concurrent GC with DATAS off.

### 4.3 Scheduler and Tick ("Decide scheduler, tick model and parallelism")

- **Systems** are source-generated: the author writes a method or struct with a typed query; the generator emits a static entry, the declared access set and ordering metadata. No virtual dispatch in the hot path.
- **Parallelism:** across non-conflicting Systems (waves) and across chunks within a System, on the Framework's own fixed work-stealing pool. No DAG scheduler in v1. Defaults: worker count = physical cores; allocation-free parking after a short spin; chunk-to-worker affinity (chunk i to worker i mod N, steal on imbalance).
- **Determinism under parallelism:** a System writes only its declared columns, partitioned by chunk; cross-Entity effects go through command buffers or events; reductions combine per-chunk results in chunk order.
- **Rates:** `every N Ticks` with optional offset (default derived from System ID); chunk-level skipping via change versions and an optional "asleep" bit.
- **The host owns the loop.** It calls `World.Tick()`. A `TickClock` helper handles accumulator, pause, speed and catch-up cap. Wall-clock time never enters the simulation. A shared Tick gate (also used by inspection pause/step and lockstep input waiting) lets the host hold a Tick.
- **Events:** typed event queues as Resources, written via per-System buffers merged in System order, cleared at a stated point, saved only if persistent. No direct callbacks.
- **Cross-Tick work:** deferred-completion Systems start in Tick T and apply at a fixed later Tick T+k (pathfinding requests, background loads).
- **Faults:** a System exception aborts the Tick and marks the World faulted naming the System.

#### Phase order

Each Tick runs these Phases in order. Command buffers merge at each Phase boundary.

1. **Streaming:** apply completed chunk loads, residency transitions, evictions and catch-up (section 5.3).
2. **Input:** build the canonical input list; handler Systems apply Input Commands (section 7.3).
3. **Begin:** apply deferred-completion results due this Tick; pathfinding request/result delivery; claim resolution of the previous decision round.
4. **Simulate:** main game logic Systems.
5. **Late:** derived-data Systems (spatial index and region upkeep from the change feed, work index).
6. **End:** events cleared, diagnostics record finalised, checkpoint hash if due.

Systems order within a Phase by After/Before constraints (stable topological sort, ties by System ID); cycles and ambiguous conflicting pairs fail at World build. Game authors may add Phases only between the named ones through the builder.

## 5. World, Fields, spatial index, streaming

### 5.1 World and Fields ("Decide World, Field and spatial indexing design")

- Bounded dimensions fixed at creation (documented maximum such as 4096x4096x512), int tile coordinates, depth 1 = 2D. Tiles are not Entities.
- **Field chunks** are 16x16x16 tiles (build-time constant), row-major cells inside the chunk (SIMD along x), whole chunks iterated and saved in Morton order of chunk coordinate. Uniform chunks hold a single value with no allocation.
- A Field is a named grid of one unmanaged cell struct with a stable name and layout hash. Multi-property tiles are several Fields. Fields are CPU-authoritative in v1.
- **Stencils:** double-buffered by default; in-place ordered mode for cellular automata using fixed chunk colouring. Both are deterministic regardless of thread count.
- **Grid Entities** carry a `TilePos` (3 ints) with optional fixed-point sub-tile offset (no floats).
- **Spatial index:** per-chunk Entity buckets updated incrementally; queries by tile, box, radius and nearest-k with predicate; results ordered by distance then Entity ID.
- **Change tracking:** per-chunk per-Field change versions plus a World change feed of changed chunks per Tick in deterministic order. Region upkeep, the work index and renderers subscribe.

### 5.2 Field interface and the future GPU seam

Fields sit behind a Field accessor and interface so storage can change without touching Systems. CPU formulations use integer or fixed-point math and the same `u16` distance layout a GPU package would reuse. See section 12.

### 5.3 Streaming ("Decide streaming worlds")

- v1 ships the seams plus a basic residency manager; the bounded World with everything resident is the "never unload" policy. It is exercised in tests by forcing chunks out and back.
- **Residency states:** Active (simulated), Hibernating (resident, not simulated), Unloaded (on disk or not yet generated). Transitions occur only in the Streaming Phase, in canonical chunk order, driven by Game focus points and radii. Focus points are Input Commands, so the schedule is replayable.
- **Entities** are chunk-owned by position and hibernate/unload with their chunk, except Entities a Game marks **pinned** (global store, may sit in unloaded space). IDs stay stable.
- **Catch-up** is frozen by default. Systems opt in with a deterministic function of (state, elapsed Ticks), run in the Streaming Phase under a work budget, with a cap on replayed Ticks.
- **Boundary:** a ring of Hibernating chunks always surrounds Active ones so halo reads are valid. Stencil exchange treats the frozen side as a fixed boundary (documented limitation).
- **Persistence:** an unloaded chunk is exactly its save section. Never-generated chunks are not stored; they are generated on demand from (seed, coordinate). Unloaded chunks keep a small pathfinding summary (portals, per-class region labels).
- **Loading:** background workers with a bounded queue; completed loads apply after a fixed N-Tick delay (configurable). A load not ready by its deadline stalls the Tick (reported by diagnostics). Games may submit prefetch hints.
- **Budgets:** a resident-chunk budget with deterministic eviction (farthest from focus first, ties by chunk coordinate; Hibernating before Active) and a budget-pressure event.
- Chunk coordinates stay 64-bit-safe so unbounded Worlds can be added later.

## 6. Determinism ("Decide determinism contract")

- **Tier 1** (guaranteed): bit-identical across thread counts, CPU widths (SSE/AVX/AVX-512) and Windows/Linux x86-64 for the same build, Plugin set, seed and inputs. **Tier 2** (best effort): ARM64 and macOS. No guarantee across Framework versions.
- **Float policy:** simulation float goes through a Framework math layer: fixed logical SIMD width, fixed-tree reductions, no implicit FMA contraction, defined float-to-int saturation, own deterministic transcendentals (never `MathF`). Every SIMD path has a Vector128 fallback; AOT guidance sets `IlcInstructionSet=x86-64-v3`.
- **Fixed-point:** 32-bit (16 fractional bits) and 64-bit types with vectors and deterministic trig/sqrt tables; used for TilePos offsets, pathfinding costs and generation noise.
- **RNG:** counter-based (Philox-like), addressed by (World seed, stream key, counter). No global `Random`.
- **Analyzers** ban `Math`/`MathF` transcendentals, `Vector<T>`, `DateTime`/`Stopwatch`, `Guid.NewGuid`, reference-type `GetHashCode`, unordered managed collections and `System.Random` in Systems. All orderings end with an Entity ID tiebreak.
- **Plugins** declare Deterministic (default) or NonDeterministic; any NonDeterministic Plugin makes the World report non-deterministic and drops the replay guarantee.
- **World hash:** from chunk hashes (Field chunks in Morton order, cells in storage order; archetype chunks by archetype ID then chunk order), covering Component columns, Fields, Resources and RNG state; excluding padding, change versions and caches. Cheap enough to run every N Ticks.
- Load-then-continue is bit-identical to never saving.

## 7. Persistence, input, replay

### 7.1 Save/load ("Decide save/load and versioning")

- A save is a snapshot of canonical chunk memory (little-endian, CPU-independent layout) in a single sectioned container: header, manifest (schema, Plugin set, Tick, World hash, seed), table of contents, one section per Field chunk and per archetype. Rebuildable state (spatial index, Relation index, change versions, caches, work index, region caches) is not saved.
- **Schema and migration:** the save embeds a schema table. Matching layout hashes bulk-copy; mismatches run registered per-Component migrations N to N+1 (chained). No migration means a clear failure. Framework Components have Framework migrations from 1.0; none promised during 0.x.
- **Plugin set** is in the manifest; a missing Plugin fails by default, with an opt-in "drop unknown Components" mode.
- **Integrity:** per-section xxHash; World hash verified on load by default. Compression per section (LZ4-class, optional Zstd). Atomic write (temp file, fsync, rename) with backup generations.
- **Timing:** saves at Tick boundaries, synchronous or background via copy-on-write snapshot. Delta saves are optional in v1 (format admits them).
- **Managed Plugin state:** Plugins register serialize/restore callbacks or declare it derivable. Resources are unmanaged structs.
- **API:** host-side `world.Save(destination, options)` and `World.Load(source, options, plugins)`; Load builds a fresh World.

### 7.2 Replay log ("Decide input and command ingestion, and the replay log")

Header (versions, Plugin set, generation version, seed or starting-save reference, tick rate), canonical input list per non-empty Tick, periodic checkpoint World hashes, optional full-save checkpoints. Same sectioned container as saves. Modes: record, playback with hash verification, seek via checkpoints, fork to live input. No outputs recorded.

### 7.3 Input Commands

A typed, schema-registered unmanaged struct. `world.Submit(command)` from any thread, stamped with an effective Tick (next boundary by default). At each Tick boundary a canonical list is built, sorted by (effective Tick, source ID, per-source sequence). Handler Systems run in the Input Phase and write through command buffers. Rejection is deterministic and yields a `CommandRejected{source, seq, reason}` event; rejected commands stay in the log. Handlers use counter-based RNG keyed by (source, sequence, use index). Native and script Plugins submit through the same entry.

### 7.4 Networking enablers (not networking)

The Framework implements no networking. It is built to support Game authors' lockstep networking: a stable serialisable wire form for per-Tick input frames (the replay format), remote players as source IDs, explicit future effective Ticks for input delay, the shared Tick gate, checkpoint hash exchange with the hash-trail finder for desync, join-in-progress via save plus input log, and build/Plugin identity in the header for peer matching. Not promised: rollback netcode, state replication.

## 8. Extension: Plugins, Definitions, C ABI

("Decide Plugin and extension architecture")

- **C# API** for Game authors and libraries: a Plugin has a manifest (stable ID, version, required API version, dependencies, Deterministic declaration) and one `Register` entry point called once at World setup; registration closes before the first Tick. It registers Components, Resources, Relations, Fields, Systems, Events, Input Commands, generation stages, inspectors, counters and Definitions. Runtime loading of C# assemblies is the Game author's concern.
- **C ABI** for Modders and script hosts: a narrow versioned function table loaded in-process (host opt-in). It exposes schema introspection, read-only chunk views valid within a Tick, command-buffer submission as the only write path, RNG streams, events, Definition lookups and Input Command submission. Native Plugin Systems run under the same scheduler with access declared in data. Caveat: a Plugin that is itself a Native AOT .NET library carries its own runtime; two AOT runtimes in one process is untested.
- **Ordering and conflicts:** load order from the dependency graph (ties by Plugin ID); duplicate names, cycles and unorderable writers fail at World build; a Plugin may replace another's System only if marked Overridable (recorded in the save manifest).
- **Definitions:** a typed registry of schema-bound unmanaged-struct definitions with interned names, loaded through a pluggable `IDefinitionSource` (a reference JSON source ships), patchable by later Plugins and baked into immutable blobs.
- **Scripting:** no language shipped. A script runtime is a Plugin over one handle-based bindings surface; script Systems are ordinary Systems under the scheduler and determinism rules.
- **Isolation:** no sandbox claimed. No runtime unloading in v1. World access only through Systems, event handlers and the host API outside Ticks.
- **Native AOT games** get data, script and native-code modding; arbitrary managed C# mods require a JIT build (the Game author's choice).
- **Versioning:** two numbers (C# API, C ABI), separate from the lockstep package version, recorded in manifests and saves. No promises during 0.x.
