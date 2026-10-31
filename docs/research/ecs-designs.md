# Survey of existing ECS designs

Research for TASK-1.1. Question: what can we learn from existing ECS implementations about storage layout, query design, structural-change cost, entity relationships and parallel scheduling, with an eye to 100k+ Entities, unmanaged Components and GPU upload?

This document records findings and trade-offs. It does not recommend a design; that is TASK-1.5 and TASK-1.7. Terms (Entity, Component, System, Tick) follow `GLOSSARY.md`.

## How to read this

- Every claim cites a primary source (official docs, README, or source file) in square brackets, keyed to the Sources list at the end.
- **[Inference]** marks my own reasoning from the sources, not something a source states.
- **[Gap]** marks something I could not confirm from a primary source. Treat it as unknown, not as false.
- Several pages were read through a summarising fetch tool, not raw. Short quotes are therefore paraphrase-level; re-check the exact wording before quoting in an ADR.

## 1. Libraries surveyed

| Library | Language | Storage model | Source of the classification |
|---|---|---|---|
| Arch | C# | Archetype + chunks, structure-of-arrays | [A1][A2][A3] |
| Friflo.Engine.ECS | C# | Archetype + chunks | [F1][F4] |
| DefaultEcs | C# | Sparse-set per component type | [D1] (source of `ComponentPool<T>`) |
| Leopotam ecslite | C# | Per-component pool plus cached filters | [L1]; sparse-set internals **[Gap]** (source file not retrievable) |
| flecs | C | Archetype ("table") by default; opt-in sparse and toggle storage | [FL1][FL2][FL3][FL5] |
| Bevy ECS | Rust | Table (archetype) by default; per-component opt-in sparse set | [B1][B2] |
| Unity DOTS (Entities 1.3) | C# | Archetype + fixed 16 KiB chunks | [U1] |

Note: ecslite's README says the project is no longer supported and recommends EcsProto instead [L1].

## 2. Storage layout

### 2.1 Archetype (table) storage

All entities with the same set of Components share an archetype. Components sit in per-archetype arrays (columns), so iteration over a query is a linear walk over contiguous memory.

- **Unity DOTS**: each chunk is 16 KiB; entities per chunk depends on the size of the archetype's components. Chunks are created when existing ones are full and destroyed when their last entity is removed. Removing an entity moves the chunk's last entity into the gap [U1].
- **Arch**: `Chunk` holds an `Entity[]` plus one `Array` per component type, all the same length (`Capacity`); a lookup array maps component id to array index. Removal is swap-with-last across every array, keeping the chunk dense [A3]. **Chunk size is inconsistent in Arch's own documents**: the README says 16 KB [A1] and the concepts page says 64 KB [A2]. I did not locate the constant in source (the constants file returned 404).
- **Friflo**: components are stored "in continuous memory" [F1] and exposed as chunk arrays that queries read directly [F2]. Chunk spans can be padded so SIMD loops need no scalar remainder [F3].
- **flecs**: entities with the same components share a table; a query can often match a whole table instead of checking entities individually [FL1].
- **Bevy**: "Table" storage is the default and is described as "fast and cache-friendly iteration, but slower addition and removal of components" [B2].

### 2.2 Sparse-set storage

Each Component type has its own packed array plus a sparse index keyed by entity id. Adding or removing a Component touches only that one pool; entities are never moved.

- **DefaultEcs** `ComponentPool<T>`: a sparse `int[] _mapping` indexed by entity id (`-1` = absent) pointing into a dense `T[] _components`. Remove swap-removes with the last dense slot and patches the moved entity's mapping. `Get` returns a `ref` and does no presence check. The class has no internal locking [D1]. It also supports several entities sharing one dense slot via reference counts [D1].
- **Bevy** (opt-in per component): "fast addition and removal of components, but slower iteration" [B2].
- **ecs-faq** (Sander Mertens, flecs author): sparse-set implementations "allow for fast add/remove operations"; a query iterates the entities of the smallest queried component and checks each other component for membership [S1].

### 2.3 Hybrids

All three of the following keep archetypes as the base and add an opt-out for specific components.

- **Bevy**: per-component `StorageType::SparseSet`. A sparse component is still part of the archetype, but its data lives outside the table [B2]. **[Inference]** The archetype-membership part is my reading of Bevy's archetype docs, which say archetypes are collections of entities with the same component set [B1].
- **flecs `Sparse`**: stores the component outside tables so it does not move when the entity changes tables; pointers stay stable; "queries get slower, but adding and removing the component gets faster"; adding or removing still causes an archetype change [FL5].
- **flecs `DontFragment`**: same sparse storage, but does not create new tables. Intended for very sparse components or relationships that would otherwise produce many tiny tables. Stated limitations at the time of the docs I read: not in entity types, monitors do not trigger, `target_for` unsupported, Or/Optional/AndFrom/NotFrom unsupported, no inheritance or transitivity [FL5].
- **flecs `CanToggle`**: a bitset; toggling does not move the entity between tables. Queries must set the TOGGLE flag, and toggle-capable components add "a small query-evaluation overhead, even for entities that never toggled" [FL5].
- **Unity enableable components**: documented as creating "less structural changes than adding or removing components" and useful for states that "change frequently and unpredictably" [U4]. The page gives no costs. **[Gap]**
- **Friflo link relations** (see section 5) avoid fragmentation by indexing outside archetypes [F5].

### 2.4 Trade-off summary

| Concern | Archetype/chunk | Sparse set | Hybrid |
|---|---|---|---|
| Iteration over a multi-component query | Linear over matched tables; matching cost amortised because tables stabilise [S1] | Walk smallest pool, probe the rest [S1] | Fast for table components, slower when sparse components are in the query [FL5][B2] |
| Add/remove a Component | Moves entity between archetypes [U1][B2] | Local to one pool [S1][D1] | Still an archetype change for flecs `Sparse`, none for `DontFragment`/`CanToggle` [FL5] |
| Fragmentation risk | Grows with distinct component combinations [FL3] | None | Controlled per component |
| Pointer/reference stability | Moves on structural change | Moves on swap-remove within pool [D1] | flecs `Sparse` gives stable pointers [FL5] |
| Feature gaps | None noted | n/a | flecs `DontFragment` has unsupported query operators [FL5] |

## 3. Query design

- **Archetype queries keep a list of matching tables** and so "do not actually have to search for entities" [FL2]. flecs makes caching a choice: cached queries "iterate fast but take longer to create, use more RAM, and add overhead to archetype creation and deletion"; uncached queries are stateless. Rule of thumb in its docs: per-frame queries (systems) benefit from caching, ad-hoc queries usually should not [FL2].
- flecs cached queries support change detection, grouping and Cascade/Desc ordering; those features require caching [FL2]. Relationship traversal is "generally faster when cached" but can degrade with many cached queries and deep hierarchies [FL2]. Sorted queries suit rarely-changing data because re-sorting can be expensive; grouping is the cheapest ordering mechanism [FL2].
- **Friflo** `ArchetypeQuery` is meant to be stored and reused to avoid setup allocation; only matching archetypes are touched, so non-matching ones cost nothing [F2]. Iterating via `query.Entities` with `GetComponent<T>()` is "much slower" than chunk access; the docs reserve it for under 10 results [F3].
- **Arch** offers lambda, interface, manual-enumeration and source-generated query styles [A2]. Queries are built with `QueryDescription().WithAll<...>()` [A1].
- **Bevy** expresses queries as system parameters (`Query<(Entity, &Position), With<Player>>`) and supports `Changed<T>`/`Added<T>` filters [B1]. The parameter types are what the scheduler uses to detect conflicts (section 6).
- **DefaultEcs** caches results in an `EntitySet` that updates automatically as compositions change; with `When...` rules the caller must call `Complete()` afterward [D2].
- **ecslite** filters store only matching entities (component data stays in pools), are built once with `Inc<>()`/`Exc<>()`, and "Lite" filters skip per-filter component caches to make entity moves cheaper [L1].
- Change detection granularity: flecs tracks per table, per component [FL2]. Bevy exposes per-component change flags [B1]. Unity chunk iteration skips chunks with no matching enabled entities [U6]. **[Gap]** Unity's per-chunk change-version mechanism (`DidChange`) was not confirmed in the pages I read.
- **[Inference]** The sparse-set designs (DefaultEcs `EntitySet`, ecslite filters) pay for query membership at add/remove time; the archetype designs pay at archetype-creation time (cached) or at iteration time (uncached).

## 4. Structural-change cost

A structural change is anything that moves an entity between archetypes or chunks, or creates/destroys one.

- **Unity**: creating/destroying an entity, adding/removing components, and setting a shared-component value are structural; setting an ordinary component value is not. They must run on the main thread, create a sync point (the main thread waits on scheduled jobs), and "you should aim to avoid sync points" [U2]. Moving entities frequently "is resource-intensive" [U1].
- **Deferral is universal.** Every library surveyed that supports parallelism routes structural changes through a command buffer:
  - Unity `EntityCommandBuffer` (parallel writer with sort keys; use a separate ECB per job because sort keys such as `ChunkIndexInQuery` can overlap) [U3].
  - flecs: during `progress()` the world is read-only; operations are queued per-thread with no locks and merged at sync points or end of frame [FL4].
  - Bevy `Commands`: queued, applied "in sequence when the `ApplyDeferred` system runs" because each command needs exclusive `World` access [B3][B4].
  - Friflo `CommandBuffer` (`Playback()` on the main thread after the query finishes; `CommandBuffer.Synced` is the thread-safe variant) [F3].
  - Arch `CommandBuffer` [A2]. DefaultEcs `EntityCommandRecorder`; structural changes are not thread-safe [D2].
- **Friflo throws on structural change inside a query loop** (since v3.1.0, `StructuralChangeException`), because it invalidates the iteration [F2].
- **Batching and bulk operations cut the cost:**
  - Friflo: each single add/remove is a structural change and moves the entity at each step; creating with all components at once, `entity.Add(a, b)` overloads, `EntityBatch`, and bulk `archetype.CreateEntities(count)` avoid intermediate moves. A code comment in the docs gives roughly 0.5 ms for 100,000 entities (author's figure, hardware not stated) [F6].
  - Arch lists bulk/batch creation and modification as more efficient than per-entity operations [A2].
- **Fragmentation also costs memory.** Friflo's docs say flecs allocates roughly 1000 bytes per archetype for link relationships, as motivation for its non-fragmenting relations [F5]. **[Gap]** This is the Friflo author's characterisation of flecs, not a flecs figure.
- **Avoiding the move**: flecs `CanToggle`, Unity enableable components, sparse and `DontFragment` storage (section 2.3).
- **Parallel structural work can be slower.** Friflo's docs state that for massive entity changes through a `CommandBuffer`, the single-threaded version is typically faster because of contention on memory and CPU caches [F3].
- **[Inference]** For a colony sim where Entities frequently gain and lose transient states (hungry, injured, assigned a job), the choice of how to model those states (archetype-changing component, toggle bit, or data field) decides how often the expensive path is hit.

## 5. Entity relationships

| Library | Mechanism | Effect on storage | Source |
|---|---|---|---|
| flecs | Pairs of (relationship, target): two entity ids packed into one 64-bit id, stored like a component | Pair add/remove costs about the same as a component add/remove; pair ids use a hashmap for archetype lookup, "roughly 5-10% overhead". A table with `(Likes, Apples)` registers under four wildcard indices, so table creation is costlier. Relationships can fragment tables. `DontFragment` is the escape hatch | [FL3][FL5] |
| flecs hierarchy | `ChildOf` | Deleting a parent deletes children. ecs-faq warns that archetype splitting of subtrees can create many small tables | [FL3][S1] |
| Bevy | `Relationship` component is the source of truth; `RelationshipTarget` is maintained by component hooks | One-to-many only (many-to-many "planned"). Constant-time add. Currently fragments archetypes by relationship type, not by target value; "fragmenting relationships" planned | [B5] |
| Friflo | Link components (`ILinkComponent`, one per type per entity) and link relations (`ILinkRelation`, many per entity) | Link components fragment archetypes; link relations do not. `GetIncomingLinks<T>()` is O(1); `GetRelation<T>(target)` is O(N) in that entity's relation count. Deleting a target removes links pointing at it. Index is only updated via `AddComponent`; writing through a `ref` bypasses the index | [F5] |
| DefaultEcs | None in core; `DefaultEcs.Extension` offers Children (dispose cascade) and Hierarchy (parents processed before children) | n/a | [D2] |
| Arch | None in core; `Arch.Extended` adds "simple relationships" | n/a | [A4] |
| Unity DOTS | Not covered by the pages I read | **[Gap]** | n/a |
| ecslite | Not covered | **[Gap]** | n/a |

Trade-off theme: the ability to query by relationship target (e.g. "all colonists assigned to workshop X") versus fragmenting storage per target. flecs, Bevy and Friflo each document that tension and pick different default/escape-hatch combinations.

**[Inference]** Hierarchies and cross-Entity references (a colonist carrying an item, a job targeting a stockpile) are likely common in this domain. The Friflo non-fragmenting relation design and flecs `DontFragment` both treat "very many distinct targets" as the case to avoid fragmenting.

## 6. Parallel scheduling

Two distinct problems appear in the sources: parallelising inside one query, and running different Systems concurrently.

### 6.1 Within a query

- **flecs**: a multithreaded system runs on all worker threads; each matched table is sliced into N parts, so an entity stays on one thread until the next sync point [FL4].
- **Friflo**: `QueryJob.RunParallel()` with a shared, thread-safe `ParallelJobRunner`; gains are largest for arithmetic-only loops and shrink with random memory access [F3]. Friflo also stresses that SIMD speeds up a single thread; combined with threads, the speedup is multiplicative only if memory bandwidth is not the limit [F3].
- **Arch**: its own `JobScheduler`; the docs claim no garbage is created [A2].
- **DefaultEcs**: `IParallelRunner`; the part with `index == maxIndex` runs on the calling thread; a runner must not be used in parallel with itself [D2].
- **Unity**: `IJobChunk.ScheduleParallel()` runs one `Execute` per matching chunk [U6][U7].
- **Bevy**: Bevy's `Query` has parallel iteration, but I could not retrieve the `Query` docs page (404), so batching details are **[Gap]**.

### 6.2 Across Systems

- **Bevy**: the multi-threaded executor runs non-conflicting systems in parallel; conflicts are derived from the declared access of each system's parameters. `ApplyDeferred` flushes buffered commands. Ordering is expressed with explicit dependencies and system sets, in a DAG. The docs warn of ambiguity between unordered conflicting systems and give a warning type for it; they state no determinism guarantee [B1][B6].
- **Unity**: ECS tracks which systems read and write which components. When sets overlap, a later system's `Dependency` includes the earlier systems' job handles [U7].
- **flecs**: the pipeline inserts sync points automatically only when a system reads a component that earlier systems could have modified through commands. By default it assumes systems insert no commands, so commands merge only at the end of the frame; the author must declare reads/writes (`in`/`out`, `read<T>()`/`write<T>()`) to inform it [FL4]. flecs can also hand work to an external job system through OS API callbacks (`set_task_threads`) [FL4].
- **ecslite** is explicitly not thread-safe and "will never be"; threading is the user's job, with an external `ecslite-threads` extension listed [L1].
- **DefaultEcs**: pools have no internal locking [D1]; the README names parallel-capable system types (`ParallelSystem`, `AEntitySetSystem`, `AComponentSystem`) and advises deferring structural changes [D2].

### 6.3 Determinism

- Bevy's page states no ordering guarantee among non-conflicting systems [B6].
- Unity's ECB docs show that parallel recording uses sort keys, and say that sort keys from consecutive jobs sharing one ECB can overlap and interleave [U3]. They describe no further determinism guarantee. **[Gap]**
- flecs: the same entity stays on the same thread until a sync point [FL4], and per-thread command queues are merged at sync points. **[Gap]** I found no statement of merge order.
- **[Inference]** Command buffers merged in an unspecified order are a plausible source of nondeterminism. This matters for TASK-1.10; none of the sources settle it.

## 7. Scale: 100k+ Entities

Only a few sources give numbers, and none are independent benchmarks.

- Friflo claims 65,536 entities running in Godot at 202 FPS and in Unity at 100 FPS on desktop, and publishes a "Performance Ratio Overview" with itself at 1.00 and Flecs.NET 2.55, TinyEcs 3.42, Arch 6.96, fennecs 19.02, Leopotam 2.57, DefaultEcs 3.81, Morpeh 21.09. The README does not say what the ratio measures; treat as the author's own marketing, not evidence [F1]. It also reports a subset of the Doraku `Ecs.CSharp.Benchmark` suite on a Mac Mini M2, whose results file I did not read [F1].
- Bevy's 0.16 release notes report transform propagation on a 127,515-object scene dropping from about 1.1 ms to about 0.1 ms on an M4 Max, attributed to parallelism changes and dirty-bit skipping [B5].
- Friflo's batch docs give about 0.5 ms for bulk-creating 100,000 entities [F6].
- **[Gap]** I found no primary-source, independent, reproducible benchmark at 100k+ for the C# libraries. This is what the planned benchmark prototype (TASK-1.13) would produce.
- **[Inference]** At 100k Entities per-structural-change overhead and archetype fragmentation matter more than raw iteration, because the sources consistently describe iteration as linear over contiguous memory and structural change as the expensive path.

## 8. Unmanaged Components and GPU upload

### 8.1 Unmanaged Components

- **Unity**: unmanaged components are the default; fields must be blittable, `bool`, `char`, `BlobAssetReference<T>`, `FixedString`, `FixedList`, fixed arrays (unsafe context only), or nested structs obeying the same rules [U5]. Managed components are a separate, slower category. **[Gap]** The pages I read do not state the cost of managed components.
- **DefaultEcs** allows struct, class or interface components; structs are recommended so they stay contiguous [D2].
- **Friflo** README describes the library as fully managed with no `unsafe` code [F1]. Its query-optimisation page says unsafe code is only needed for bounds-check elision at maximum performance [F3].
- **Arch** is "AOT friendly" [A1]. Its chunk arrays are .NET `Array` instances (`ArrayRegistry.GetArray(type, Capacity)`) [A3]. **[Gap]** Whether Arch constrains `T` to unmanaged was not confirmed.

### 8.2 GPU upload

I found no primary source in these libraries that documents GPU upload. The following is what the sources do establish about the memory layout that an upload path would depend on.

- Archetype/chunk libraries expose component data as contiguous arrays per chunk (Friflo `Chunk` spans [F3]; Arch component arrays [A3]; Unity chunk components [U1]).
- DefaultEcs exposes the dense array of a pool as a span (`AsSpan`) [D1].
- Chunks are bounded blocks (Unity 16 KiB [U1]), not one array per component type across the whole World.
- **[Inference]** A single linear copy of one component type across 100k Entities is possible directly only where storage is a single dense array (sparse-set pool). Archetype storage yields one contiguous block per chunk, so uploads become per-chunk copies or a gather into a staging buffer. Row order in both models changes on swap-remove [A3][D1], so a GPU-side index back to an Entity would need to be maintained or rewritten.
- **[Inference]** Component structs destined for the GPU need a fixed, blittable layout. Unity's blittable-field rule [U5] is the documented example of the constraint.
- **[Inference]** Friflo's padding for SIMD spans [F3] shows chunk storage can be shaped for a consumer's alignment needs; I did not find the same for GPU buffers.

## 9. Open questions for later tasks

These are not answered by the sources:

1. Independent, reproducible 100k+ Entity benchmarks across the C# libraries (TASK-1.13).
2. Command-buffer merge order and determinism under parallel recording (TASK-1.10).
3. How well each design supports saving and loading a stable Entity identity (TASK-1.11). Arch.Extended lists JSON/binary World persistence [A4]; nothing else was checked.
4. Where chunk size is defined in Arch, and whether 16 KB or 64 KB is current.
5. Bevy `Query` parallel iteration details and Unity per-chunk change versions.
6. Whether GC pressure and `Array`-backed storage (Arch, Friflo) versus pinned or native memory affects GPU upload paths on the .NET versions Colony Framework targets.

## Sources

Retrieved 2026-10-30. Numbers in brackets are the keys used above.

**Arch**
- [A1] Arch README: https://github.com/genaray/Arch
- [A2] Arch concepts documentation: https://arch-ecs.gitbook.io/arch/documentation/concepts.md
- [A3] Arch `Chunk.cs`: https://raw.githubusercontent.com/genaray/Arch/master/src/Arch/Core/Chunk.cs
- [A4] Arch.Extended README: https://github.com/genaray/Arch.Extended

**Friflo.Engine.ECS**
- [F1] README: https://github.com/friflo/Friflo.Engine.ECS
- [F2] Query docs: https://friflo.gitbook.io/friflo.engine.ecs/documentation/query
- [F3] Query optimization docs: https://friflo.gitbook.io/friflo.engine.ecs/documentation/query-optimization.md
- [F4] Docs index (checked for layout detail, none found): https://friflo.gitbook.io/friflo.engine.ecs
- [F5] Relationships docs: https://friflo.gitbook.io/friflo.engine.ecs/documentation/relationships.md
- [F6] Batch docs: https://friflo.gitbook.io/friflo.engine.ecs/documentation/batch.md

**DefaultEcs**
- [D1] `ComponentPool.cs`: https://raw.githubusercontent.com/Doraku/DefaultEcs/master/source/DefaultEcs/Internal/ComponentPool.cs
- [D2] README: https://github.com/Doraku/DefaultEcs

**Leopotam ecslite**
- [L1] README: https://github.com/Leopotam/ecslite

**flecs** (docs at flecs.dev)
- [FL1] Quickstart: https://github.com/SanderMertens/flecs/blob/master/docs/Quickstart.md
- [FL2] Queries: https://www.flecs.dev/flecs/Queries.html
- [FL3] Relationships: https://www.flecs.dev/flecs/Relationships.html
- [FL4] Systems: https://www.flecs.dev/flecs/Systems.html
- [FL5] Component traits: https://www.flecs.dev/flecs/ComponentTraits.html
- [S1] ecs-faq by Sander Mertens (flecs author): https://github.com/SanderMertens/ecs-faq

**Bevy**
- [B1] `bevy::ecs` module docs: https://docs.rs/bevy/latest/bevy/ecs/index.html
- [B2] `StorageType`: https://docs.rs/bevy/latest/bevy/ecs/component/enum.StorageType.html
- [B3] `Commands`: https://docs.rs/bevy/latest/bevy/ecs/system/struct.Commands.html
- [B4] (same as B3, for `ApplyDeferred` wording)
- [B5] Bevy 0.16 release notes: https://bevy.org/news/bevy-0-16/
- [B6] `schedule` module docs: https://docs.rs/bevy/latest/bevy/ecs/schedule/index.html

**Unity DOTS (Entities 1.3 manual)**
- [U1] Archetypes: https://docs.unity3d.com/Packages/com.unity.entities@1.3/manual/concepts-archetypes.html
- [U2] Structural changes: https://docs.unity3d.com/Packages/com.unity.entities@1.3/manual/concepts-structural-changes.html
- [U3] Entity command buffers: https://docs.unity3d.com/Packages/com.unity.entities@1.3/manual/systems-entity-command-buffers.html
- [U4] Enableable components: https://docs.unity3d.com/Packages/com.unity.entities@1.3/manual/components-enableable.html
- [U5] Unmanaged components: https://docs.unity3d.com/Packages/com.unity.entities@1.3/manual/components-unmanaged.html
- [U6] IJobChunk: https://docs.unity3d.com/Packages/com.unity.entities@1.3/manual/iterating-data-ijobchunk.html
- [U7] Scheduling jobs: https://docs.unity3d.com/Packages/com.unity.entities@1.3/manual/systems-scheduling-jobs.html

**Not retrievable (404 or blocked)**: Sander Mertens' "Building an ECS" Medium articles (403); Bevy `Query` docs (404); Arch `Constants.cs` (404); ecslite source files (404); Friflo `documentation.md` (404). The ecslite internals and Arch chunk-size constant therefore rest on READMEs and one source file only.
