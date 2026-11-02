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
| `Colony.Persistence` | Save/load container, migrations, replay log and verifier, Input Command ingestion and wire form, join package, transport-free lockstep session. |
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
- `Fields` and `Persistence` depend on `Core`. `Plugins` depends on `Abstractions`, `Core`, `Fields` (Plugins register Fields) and `Persistence` (the C ABI loads and saves Worlds); ADR 0005.
- `Pathfinding`, `Colony`, `ProcGen` depend on `Core` and `Fields` (and `Persistence` only where they save state). `Colony` also depends on `Pathfinding` for its reachability filters (ADR 0004).
- `Diagnostics` plugs into `Core` through hook interfaces and struct callbacks; `Core` never references it.
- Nothing depends on `Diagnostics`, `Testing` or `Plugins`, so a Game can run without the Plugin machinery. Exceptions, decided at implementation: `Native` depends on `Plugins` (it hosts the C ABI, whose host lives in `Plugins`) and on whatever `Plugins` depends on; `Testing` may depend on `Core`, `Fields`, `Persistence` and `Plugins` (it exercises them); the `Colony` meta-package references `Core`, `Fields`, `Persistence`, `Pathfinding`, `Colony` and `ProcGen`, not `Plugins`, `Diagnostics` or `Testing`.
- `Abstractions` depends only on the BCL. `Diagnostics` depends on `Core`, `Fields` and `Persistence` (it reads their counters and the replay log) and on nothing else; `Generators` depends only on Roslyn.

Public vs internal: the public API is safe by default (typed handles, ref structs, Span views, generated typed queries). `unsafe` appears only in `internal` code, plus a small documented `[Experimental]` `Colony.Unsafe` namespace (raw chunk pointers for custom SIMD). `InternalsVisibleTo` is for tests only, with two exceptions recorded in ADR 0005: `Core` grants `Persistence` (exact save and restore of chunk memory, the Entity index, Resources and tables) and `Plugins` (layout-only registration and raw-byte access for the C ABI host). No runtime reflection anywhere; no static state or singletons, so many Worlds can coexist in a process. Three process-wide exceptions hold no simulation state and never decide an outcome (ADR 0005): the C ABI handle table (C code holds plain integers; World-owned handles are released when their World is disposed), the C ABI's per-thread last-error message (`colony_last_error`, C convention), and the `Colony-Diagnostics` EventSource (`EventSource` instances are process-wide by design). Read-only caches of immutable data are not state.

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
- **Halo kernels:** the fast stencil path hands a kernel the whole 18x18x18 halo (`StencilHalo<T>`: `Row(y, z)` in, `OutputRow(y, z)` out, clipped to the World on edge chunks, `Boundary` written to storage-only cells; in place, output aliases input so a kernel sees its own earlier writes) through `StencilHaloKernel<T>` overloads of `Stencil.DoubleBuffered` / `Stencil.InPlace`; the per-cell `Read` kernel stays for reach beyond one cell. Halo scratch is a `StencilScratchPool` owned by the World's Field set (one slot per worker, freed on dispose), never thread-static.
- **Grid Entities** carry a `TilePos` (3 ints) with optional fixed-point sub-tile offset (no floats).
- **Spatial index:** per-chunk Entity buckets updated incrementally; queries by tile, box, radius and nearest-k with predicate; results ordered by distance then Entity ID.
- **Change tracking:** per-chunk per-Field change versions plus a World change feed of changed chunks per Tick in deterministic order. Region upkeep, the work index and renderers subscribe. Host writes between Ticks are stamped too (`World.Get`, `GetBuffer`, `ChunkView.Column`), so those subscribers see them next Tick; `GetReadOnly` and `ReadColumn` never stamp and are the clean read paths.

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
- **World hash:** from chunk hashes (Field chunks in Morton order, cells in storage order; archetype chunks by archetype ID then chunk order), covering Component columns, the Entity index (slot high-water mark, generations, free list in pop order), Fields, Resources and RNG state; excluding padding, change versions and caches. Cheap enough to run every N Ticks.
- Load-then-continue is bit-identical to never saving.

## 7. Persistence, input, replay

### 7.1 Save/load ("Decide save/load and versioning")

- A save is a snapshot of canonical chunk memory (little-endian, CPU-independent layout) in a single sectioned container: header, manifest (schema, Plugin set, Tick, World hash, seed), table of contents, one section per Field chunk and per archetype. Rebuildable state (spatial index, Relation index, change versions, caches, work index, region caches) is not saved. The exception is the flow Field store: which Fields exist, their recency and their pending repairs decide what later Ticks compute (a starved repair budget leaves a Field stale; LRU eviction depends on recency), so it is saved, not hashed, and a load continues with the same dirty Fields and eviction order (clean Fields are rebuilt). For the same reason host reads between Ticks are pure: `PathfindingService.FlowField(goals, movementClass)` there returns the Field as it is, without repair or a recency touch; `Repair` is the explicit, simulation-affecting call.
- **Schema and migration:** the save embeds a schema table. Matching layout hashes bulk-copy; mismatches run registered per-Component migrations N to N+1 (chained). No migration means a clear failure. Framework Components have Framework migrations from 1.0; none promised during 0.x.
- **Resource and extension migrations:** every persisted shape is migratable the same way. Persistent Resources (values and event queues) are keyed by name and layout hash like Components; a value migrates once, a queue record by record, and a Resource's kind (value or queue) cannot change. Each extension (Fields, pathfinding, residency, generation, ...) stamps its sections with a format version recorded in the manifest; an older save is brought forward by registered per-extension migrations N to N+1 (chained, rewriting the sections before the extension loads them), a newer save is refused naming the extension and both versions. An extension's layout hash (what state exists) must still match; a format migration changes only how that state is written, so a state-preserving one keeps the World hash verifiable. A missing step fails naming the Component, Resource or extension and the layout or version it stopped at.
- **Plugin set** is in the manifest; a missing Plugin fails by default, with an opt-in "drop unknown Components" mode.
- **Integrity:** per-section xxHash; World hash verified on load by default. Compression per section (LZ4-class, optional Zstd). Atomic write (temp file, fsync, rename) with backup generations.
- **Timing:** saves at Tick boundaries, synchronous or background via copy-on-write snapshot. Delta saves are optional in v1 (format admits them).
- **Managed Plugin state:** Plugins register serialize/restore callbacks or declare it derivable. Resources are unmanaged structs.
- **API:** host-side `world.Save(destination, options)` and `World.Load(source, options, plugins)`; Load builds a fresh World. `LoadOptions.PrepareWorld` runs after extensions load and before hash verification, so a Game can attach a host-owned chunk source there and a streamed save still verifies.

### 7.2 Replay log ("Decide input and command ingestion, and the replay log")

Header (the lockstep peer identity: format versions, build, Plugin set, schema hash, seed, tick rate; plus extension content versions, checked on playback with the same handshake comparison; a log from a NonDeterministic Plugin is refused), canonical input list per non-empty Tick, periodic checkpoint World hashes, optional full-save checkpoints. Same sectioned container as saves. Modes: record, playback with hash verification, seek via checkpoints, fork to live input. No outputs recorded.

### 7.3 Input Commands

A typed, schema-registered unmanaged struct. `world.Submit(command)` from any thread, stamped with an effective Tick (next boundary by default). At each Tick boundary a canonical list is built, sorted by (effective Tick, source ID, per-source sequence). Handler Systems run in the Input Phase and write through command buffers. Rejection is deterministic and yields a `CommandRejected{source, seq, reason}` event; rejected commands stay in the log. Handlers use counter-based RNG keyed by (source, sequence, use index). Native and script Plugins submit through the same entry.

**Per-source permissions.** Declared at build time by the Game and Plugins: a default access, per-source and per-command rules, and (source, command) grants, resolved pair first, then Deny over Allow, then the default. A contradictory declaration fails the build naming both owners. The table is part of the schema hash (only when restricted, so unrestricted schemas hash as before), is saved in manifests and replay headers and compared by the handshake. A denied command stays in the canonical list and replay frame, is hidden from handlers and yields `CommandRejected` with the PermissionDenied reason at the Tick boundary. Debug commands are denied by default and granted to the debug source.

### 7.4 Networking enablers (not networking)

The Framework implements no networking. It is built to support Game authors' lockstep networking: a stable serialisable wire form for per-Tick input frames (the replay format), remote players as source IDs, explicit future effective Ticks for input delay, the shared Tick gate, checkpoint hash exchange with the hash-trail finder for desync, join-in-progress via save plus input log, and build/Plugin identity in the header for peer matching. Not promised: rollback netcode, state replication.

**Join, handshake, test network, hash tree.** `PeerIdentity` (format versions, build, Plugins in load order, schema hash, seed, tick rate, session settings) encodes to bytes and `PeerHandshake.Compare` names every mismatch from each side. `JoinPackage` captures the latest save plus the log frames, checkpoints and pending commands since; it splits into self-verifying chunks that reassemble in any order, and `Join` loads with hash verification and plays forward to the end Tick checking every checkpoint (the host forwards later submissions with their explicit sequence). `Colony.Testing` ships a seeded `TestNetwork` (latency, jitter, reordering, loss, duplication; same sends and seed give the same deliveries), a `LockstepHarness` with fault injection (lost, replaced or wrong frames) and desync reporting. `world.HashTree()` gives a tree whose root equals the World hash (groups: tick, components, resources, tables, one per extension, random; leaves are chunks) so two peers compare only diverging groups and items; those group names are reserved and an extension may not use them. Diagnostics writes `.tree` files, serves a `hashtree` operation and compares live servers (`hashtree` and `desync` CLI commands).

**Lockstep session (TASK-34).** `Colony.Persistence` ships `LockstepSession`, a transport-free state machine over those enablers: it consumes and produces lockstep messages as byte spans (input frames, cumulative acks and resend requests, scheduled checkpoint hashes, the handshake, post-desync diagnostics) and opens no sockets, starts no threads and reads no wall clock. The Game owns the transport and the loop: it hands received bytes to `Receive`, asks `NextMessage(peer)` for what to send each peer, and Ticks when `TryBeginTick` releases the shared Tick gate. Per-peer input delay is fixed or adaptive; a missing frame stalls at the gate; a checkpoint disagreement raises `Desynced` with the checkpoint Tick, then `DesyncLocalised` with the first diverging Tick, Phase and System from the exchanged hash trails and, when both peers kept the tree, the hash-tree localisation. Join-in-progress uses `JoinPackage` plus the frames the session retains. See `docs/guides/lockstep-networking.md` and the `samples/HelloLockstep` loopback sample. The Framework still ships no transport, no NAT traversal, no matchmaking, no encryption and no peer-drop policy.

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

## 9. Colony-sim building blocks

### 9.1 Pathfinding ("Decide pathfinding at scale", evidence in `prototype-gpu-pathfinding.md`)

A CPU service providing:

- **Region IDs** (connected components) maintained incrementally from the change feed: merges are cheap, splits trigger a bounded local relabel with a chunk-set rebuild fallback. They make unreachable requests O(1) (40-110 ns) and are mandatory.
- **Movement classes:** Game-defined Definitions, each with its own passability rule; structures built lazily per class on first use. Per-World limit (default 32, hard ceiling 256).
- **Two-level chunk-graph HPA*** for unique goals (paths approximate, within a few percent of optimal), refined near the unit by local A*.
- **Bounded flow Fields** for shared goals: goal chunks plus a radius, built lazily, with an LRU cap under the Field memory budget; units outside the bound use HPA* to reach the edge.
- **Layered repair:** per-chunk dirty marking, lazy repair on read, a per-Tick work budget with a staleness bound, and an up-front rebuild-vs-incremental threshold (prototype: CPU incremental repair can cost more than a rebuild).
- **Queries:** `Reachable(from, to, movement class)` in constant time; approximate path cost; budgeted deferred path request/result; and a "next step toward goal" query.
- **Request queue** processed in canonical order (priority class, then request ID) under a budget counted in work units (nodes expanded), results delivered N Ticks later; a missed deadline fails with a deterministic event. A request through an Unloaded chunk fails with "needs loading" carrying the chunk coordinate.
- Flow Fields never cross an Unloaded chunk: such a Field reports `NeedsLoading` with the first missing chunk, and a step into or toward one returns `FlowStepStatus.NeedsLoading`.
- Tier 1 deterministic (integer costs, ties by neighbour index). Caches are not saved, except the flow Field store (section 7.1).

Games own unit movement; a path-follow helper is provided.

### 9.2 Jobs and colony AI ("Decide job assignment and colony AI building blocks")

- **Claim** is a built-in Relation owned by the target (claimant, target, slot) with capacity, optional amount and release on job end, claimant death or target destruction. Two phases: agents propose in parallel; a deterministic resolve step (sorted by priority key then Entity ID, per target chunk bucket) grants or denies. Denied agents rescan next decision.
- **Work index:** per-work-type, per-chunk lists of available work items with deterministic ordering and change tracking, queried by radius-bounded bucket search ("nearest N available of types").
- **Optional hauling matcher** (supply/demand, producing haul work items).
- **Decision scheduling:** agents are woken (hashed cadence or trigger), with a per-System decision budget carrying remainders forward.
- **Anti-thrash defaults:** commitment window, per-agent cooldowns on failed targets, job-start circuit breaker (all replaceable).
- **Optional helpers:** a lexicographic priority-table chooser and utility scoring. No behavior trees or GOAP/HTN in v1.
- Needs, mood, work types, skills and job content are Game author design. Claims, work items and cadence are ordinary saved Components/Relations; work-index lists and reachability caches are rebuilt on load.

### 9.3 Procedural generation ("Decide procedural generation architecture")

- Generation is a pure function of (seed, chunk coordinate) through the ChunkProvider contract: any chunk alone, in any order, on any thread, same bytes.
- **Pipeline:** named stages, each declaring the Fields it reads and writes, ordered by before/after name; Plugins register and (if Overridable) replace stages.
- **Randomness:** counter-based RNG keyed by (seed, stage name hash, chunk or region coordinate, use index); adding a stage never shifts another's numbers.
- **Cross-chunk features:** region-keyed placement for large features; halo reads over neighbours' base data only; a deterministic world-level pre-pass for global solves (for example drainage graphs), cached as a generated Resource.
- **Entities** come from placement stages with IDs derived from (seed, stage, coordinate, index).
- **Validation:** golden hashes per stage and seed, order-independence and seam checks, extensible connectivity validators, per-stage timing.
- **Ships:** integer/fixed-point noise utilities (value, gradient, simplex, cellular, fBm, domain warping, region-keyed spot sampling), pipeline machinery and test tooling. **Optional packages:** a graph/network toolkit and a WFC helper for small bounded content. Terrain rules and content are Game author territory.
- The manifest records a generation version; regenerating a chunk is valid only if it matches. Float noise is excluded from the deterministic path.

## 10. Tooling

### 10.1 Profiling and diagnostics ("Decide profiling and diagnostics")

- Fixed always-on numeric counters in a preallocated per-Tick record ring: Tick and Phase timings, per-System time and chunks visited/skipped, worker busy/spin/park, memory vs budget, command-buffer sizes, plus streaming counters (residency counts, load queue depth, stalls) and pathfinding counters (request queue, work units, repair backlog). Versioned layout.
- Cost levels: Off, Standard (default, under about 1% of Tick time), Detailed.
- Allocation-free in-process read API (`world.Diagnostics`) with opt-in sinks: EventSource/Meter bridge, Perfetto trace export, and the inspection protocol.
- First-class all-thread allocation check; optional Tick budget with slow-Tick reports; structured allocation-free logging (message IDs plus numeric args, drained between Ticks; no wall-clock time).
- Flag-controlled validation switches (ref checks, poisoning, leak checks, access-declaration checking, determinism spot-checks); optional hash trail per Tick/Phase/System/archetype for localising divergence.
- Plugins register named counters and timed sections in `Register`.

### 10.2 Inspection ("Decide debug and inspection tooling")

One versioned inspection protocol (compact binary with a JSON mapping), served in-process or over a localhost socket/pipe; host-started, off by default, compiled out under `COLONY_NO_INSPECT`; token plus read-only by default, `AllowMutation` opt-in. Reads at Tick boundaries (schema, Entities, Fields, Resources, events, spatial queries, diagnostics). Mutation only as debug commands through the command buffer, recorded in the replay log. Pause/step, replay-based rewind, hash-trail desync finder, World/save diff, data-not-pixels visualisation, Plugin-registered inspectors, and a `colony-inspect` CLI as the reference client. No GUI.

### 10.3 Testing and benchmarks ("Decide testing and benchmark strategy")

- **Six layers, each its own project:** unit/property; simulation determinism; contract and conformance (Plugin ordering, C ABI); compile-time (generator snapshots, analyzer diagnostics); soak/stress; benchmarks. Test framework: the current Microsoft.Testing.Platform default (xUnit v3 or TUnit), chosen at implementation time.
- **Determinism harness** (shipped as a package Plugins can run): seeded scenarios across 1/2/4/N threads, ISA settings and Native AOT; a negative control that must fail; committed golden hashes compared across OSes; save-load-continue equals never-saved. Added scope: replay equivalence, a simulated two-peer lockstep test, streaming evict/reload round trips and load-deadline stalls, pathfinding scenarios (unreachable targets, toggles, budget exhaustion).
- **Allocation harness** over every thread; debug memory validations; optional nightly use-after-free stress.
- **ABI conformance:** a C host loads the Framework, registers a Native Plugin and exercises every entry on JIT and AOT; `colony.h` is checked in and diffed in CI.
- **Benchmarks:** BenchmarkDotNet micro-benchmarks plus a custom Tick harness (100k/1M colonists, churn, Fields, save/load) with JSON output. Performance is tracked, not gated, on shared runners; hard gates only on deterministic counts. A dedicated machine runs nightly comparisons.
- **Model-based tests** against slow reference implementations (dictionary ECS, naive grid). Hashes prove sameness, not correctness.
- **CI:** PR matrix Windows x64 and Linux x64 on JIT; nightly adds Native AOT, ISA variants, ARM64 Linux, soak. CI runs on GitHub Actions; the dedicated benchmark machine is a self-hosted runner.
- Fixtures: a non-shipped test content package, scenarios defined by seed plus parameters, committed golden saves per container version.

## 11. Public API shape

- `WorldBuilder` is the single composition root: it registers Components, Systems, Fields, Plugins, budgets, determinism flags and the ChunkProvider, validates at build time (layout hashes, access conflicts, Plugin ordering, Field names) and produces an immutable `WorldSchema` shared across Worlds. Registration closes before the first Tick.
- `World`: create, `Tick`, `Submit`, `Save`/`Load`, `Diagnostics`, accessors for Resources, Events, Fields and Definitions.
- Systems and Components are attributed partial structs/methods processed by the generator. Everything not needed by Games, Plugins or generated code is `internal`.
- Generators and analyzers ship as one package; generated code lives in the user's assembly and uses only public surface.

### 11.1 Hello colony (illustrative sketch)

This shows intended shape, not final signatures. It becomes the first sample and a compile-checked doc test.

```csharp
[Component] public partial struct Hunger { public Fixed32 Value; }
[Component] public partial struct Food   { public int Units; }

[Field("heat")] public partial struct Heat { public Fixed32 Value; }

public static partial class ColonySystems
{
    [System(Phase.Simulate, Every = 1)]
    public static void Starve(ref Hunger hunger)
        => hunger.Value += Fixed32.FromRatio(1, 600);
}

var schema = new WorldBuilder()
    .Seed(12345)
    .AddComponent<Hunger>().AddComponent<Food>()
    .AddField<Heat>()
    .AddSystems<ColonySystems>()
    .Build();                                  // validates, freezes the schema

using var world = schema.CreateWorld();
for (int i = 0; i < 100; i++) world.Create(new Hunger());

for (int tick = 0; tick < 1000; tick++) world.Tick();

world.Save("colony.save", SaveOptions.Default);
Console.WriteLine(world.Hash());               // identical on every machine
```

The sketch as it compiles is `samples/HelloColony/Sketch.cs`; `SpecSketchDocTests` diffs the two line by line and fails on any drift beyond these exact deviations:

- **Fields:** there is no `[Field]` attribute; `Heat` is a plain `public struct`, registered by name on the Fields builder: `builder.AddFields(new WorldSize(64, 64, 1)).AddField<Heat>("heat")` replaces `.AddField<Heat>()`.
- **Phase in attributes:** `[System(NamedPhase.Simulate, Every = 1)]`, since an attribute argument must be a constant and `Phase` is not one.
- **Systems:** the generator emits `ColonySystems.AddSystems(builder)`, a statement, so the chain breaks into `var builder = ...;`, `ColonySystems.AddSystems(builder);` and `var schema = builder.Build();` in place of `.AddSystems<ColonySystems>()`.
- **Seed:** a World option, `schema.CreateWorld(new WorldOptions { Seed = 12345 })`, not `.Seed(12345)` on the builder (the seed is per World, the schema is shared).
- **Creating Entities:** through a command buffer: `var commands = world.CreateCommandBuffer();`, `commands.Create(ComponentMask.Of(schema.Component<Hunger>()))` in the loop, then `world.Playback(commands);`. There is no `world.Create(new Hunger())`.
- The compiled sketch also ends with `return world.Hash();` so the doc test can check the run.

## 12. GPU: deferred, with a seam

The GPU work done during mapping (Backend abstraction, wgpu-native, Exact/Approximate Kernels, GPU Field and pathfinding prototypes) is retained as research. Findings: dense GPU-authoritative Fields won about 15x (2.1 ms vs about 32 ms); a CPU-authoritative mirror never wins (PCIe-bound); shared-goal flow Fields won only about 2x; per-unit GPU queries lost 17-20x to CPU HPA*; float Kernels differed up to 5 ULP between Vulkan and DX12 while integer Kernels were bit-identical.

Decision: v1 is CPU-only. The gain does not justify bindings, a second Kernel implementation, an authority model, device-loss handling and a test matrix before a real workload needs it. Kept seams: Fields behind a Field interface with the CPU implementation as oracle; the Kernel/Backend vocabulary in the glossary; integer-distance and fixed-point formulations; the Exact/Approximate rule (only Exact Kernels run in deterministic Worlds). A later optional `Colony.Gpu.*` package starts from `docs/research/prototype-gpu-field.md`, `prototype-gpu-pathfinding.md` and the superseded GPU Backend ticket (wgpu-native with own generated bindings, CPU default with explicit per-Field GPU opt-in, transfer budgets, staging ring plus scatter Kernel, Vulkan default).

## 13. Prototype evidence

| Prototype | Result | Report |
|---|---|---|
| ECS storage and scheduler | 100k colonists 1.02 ms/Tick single-thread, 0.20 ms on 12 threads; 1M colonists 7.8 ms single-thread; zero allocation; bit-identical across 1/2/4/16 threads and JIT/AOT. Weak spots: scaling, serial playback, AOT default ISA (fixed by x86-64-v3). | `prototype-ecs-bench.md` |
| GPU Field | See section 12. | `prototype-gpu-field.md` |
| GPU pathfinding | See sections 9.1 and 12. Region IDs mandatory; plain A* infeasible at scale; full-World Fields break at 512x512x64. Big-World hybrid and thread rows were not run. | `prototype-gpu-pathfinding.md` |

Surveys: `ecs-designs.md`, `gpu-compute-options.md`, `dotnet-11-performance-features.md`, `colony-sim-prior-art.md`, `procgen-approaches.md`, `colony-ai-and-jobs.md`. Caveats: prototypes were single-machine (i9-9900KF, RTX 2060 SUPER, Windows 11); the ECS prototype did not exercise enable bits, Relations, Fields, change detection or access enforcement; read numbers as orders of magnitude.

## 14. Repo structure and tooling

One solution: `src/<Package>/`, `tests/<Package>.Tests/`, `benchmarks/`, `samples/` (a minimal headless host and a C native host), `docs/`, `prototypes/` (throwaway, never referenced or built by the solution), `tools/` (development-only tooling, never referenced by `src/`, not shipped), central package management, shared `Directory.Build.props` (analyzers, AOT and determinism flags) and `global.json` pinning the SDK. Licensed MIT (`LICENSE` at the repo root); hosted at `github.com/jcannon98188/ColonyFramework`; CI on GitHub Actions.

## 15. Implementation hand-off

Ticket seams in dependency order. Each item is tracked in Backlog as the listed tasks (all labelled `ready-for-agent`, with native dependency edges); the task bodies carry the acceptance criteria.

1. **Repo skeleton:** solution, build props, analyzers, CI dependency check, `Colony.Generators` scaffold. — TASK-3.
2. **Core vertical slice:** math layer, fixed-point and RNG (TASK-4); entity storage with slabs, chunks, archetypes and command buffers (TASK-6); scheduler, Phases and Tick, reproducing the ECS prototype's colonist hash across 1/N threads and JIT/AOT (TASK-7); Resources, events, enable bits and rates (TASK-8); Relations, buffer Components, string table and blobs (TASK-9); determinism and allocation harnesses in `Colony.Testing` (TASK-10).
3. **Generators and analyzers:** determinism analyzers (TASK-5, can start right after the skeleton); source generator for Components, Systems and typed queries (TASK-11).
4. **Fields and spatial index:** Field chunks, stencils, uniform chunks and change feed (TASK-12); spatial index (TASK-13).
5. **Persistence:** save container and migrations (TASK-14); Input Commands and replay log (TASK-15).
6. **Plugins and Definitions:** manifest, Register, Overridable Systems, Definition registry, `Colony.Abstractions` (TASK-16).
7. **Diagnostics:** record ring, logging, allocation check, validation switches, hash trail (TASK-17); EventSource/Meter and Perfetto sinks (TASK-18).
8. **Pathfinding:** movement classes, region IDs and Reachable (TASK-19); chunk graph, HPA* and request queue (TASK-20); flow Fields and layered repair (TASK-21).
9. **Colony building blocks:** claims and decision scheduling (TASK-22); work index, hauling matcher and chooser helpers (TASK-23).
10. **ProcGen:** noise utilities (TASK-24, depends only on the math layer); pipeline, ChunkProvider and validation tooling (TASK-25).
11. **Streaming:** residency manager and evict/reload round trip (TASK-26); background loaders, eviction budget and catch-up (TASK-27).
12. **C ABI:** `Colony.Native`, header generation, conformance suite (TASK-28).
13. **Inspection:** protocol, server, `colony-inspect` (TASK-29).
14. **Benchmarks and sample host:** BenchmarkDotNet suite, Tick harness, hello-colony headless host (TASK-30).
15. **Optional early tooling:** the warm-Roslyn dev daemon (TASK-2; standalone, fresh implementation, `tools/`), blocked on the harnesses and generator so there is a build/test loop to measure.

## 16. Open items and risks

- Prototype gaps listed in section 13; re-measure on the real implementation.
- Background-save copy-on-write cost was not measured (the ECS prototype did not cover it).
- Two Native AOT runtimes in one process (AOT-built Plugin libraries) is untested.
- Possible later work, not specified: time/calendar helpers, event/notification conventions, snapshot ring and rollback, state-replication networking, a GPU package, GOAP/BT libraries.
- ADRs: `docs/adr/0001-archetype-chunk-storage.md`, `0002-tier-1-bit-exact-determinism.md`, `0003-cpu-only-v1.md`.
