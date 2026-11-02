---
id: TASK-6
title: 'Entity storage tracer bullet: slabs, chunks, archetypes, command buffer'
status: Done
assignee:
  - '@jason'
created_date: '2026-10-31 01:56'
updated_date: '2026-10-31 03:00'
labels:
  - ready-for-agent
  - implementation
dependencies:
  - TASK-3
documentation:
  - >-
    backlog/docs/spec/doc-1 -
    Colony-Framework-architecture-spec-v1-part-1-of-2.md
  - >-
    backlog/docs/spec/doc-2 -
    Colony-Framework-architecture-spec-v1-part-2-of-2.md
type: task
ordinal: 33000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
The heart of Colony.Core (spec section 4.1 and 4.2, ADR 0001): archetype/chunk structure-of-arrays storage with 64 KiB chunks, 64-byte aligned columns padded to a multiple of 16 elements independent of CPU vector width, carved from roughly 4 MiB native slabs. Entity IDs are 64-bit (32-bit index, 32-bit generation) and stable across save/load. Structural change happens only through command buffers at sync points, which is what makes parallel iteration safe later. The ECS prototype (research report prototype-ecs-bench) validated this shape at 100k and 1M colonists with zero allocation; this ticket builds the real thing with a model-based test as the correctness oracle. Component identity is a registered name plus layout hash, never the CLR type.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 Entities with unmanaged Components can be created, destroyed and have Components added or removed through a command buffer played back at a sync point; stale EntityIds fail the generation check
- [x] #2 Chunk-level iteration over an archetype exposes each column as a Span with 64-byte alignment and 16-element padding regardless of host CPU
- [x] #3 A model-based test runs random operation sequences against a dictionary-based reference ECS and compares observable state after every playback
- [x] #4 A 100k-Entity create, iterate and mutate loop performs zero managed allocations in steady state, verified by a test
- [x] #5 Opt-in non-fragmenting mode exists for Components flagged as flipping often, and a per-Tick scratch bump arena backs command buffers and temporaries
- [x] #6 Debug builds validate refs and Spans against the chunk structural version, poison freed memory and report leaks on World dispose
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Public surface: EntityId, ComponentType, ComponentMask (256-bit, so max 256 Components per schema), ComponentOptions.NonFragmenting, WorldBuilder/WorldSchema/WorldOptions, World (CreateCommandBuffer, Playback = sync point, ResetScratch = end of Tick, IsAlive/Has/Get<T>, Query -> ChunkQuery/ChunkView), CommandBuffer with deferred Create handles, StructuralCounters, LeakReport, IDebugListener. Internals: SlabAllocator (4 MiB slabs, 64 KiB chunks), ScratchArena renting from the same allocator, Archetype with 64-byte column offsets and capacity a multiple of 16 (colonist archetype 2720 rows), EntityIndex with LIFO reuse, NonFragmentingStore. Layout hash is FNV-1a(name,size,alignment) as a placeholder for TASK-4's hash and TASK-11's field-level descriptor. Chunks are 4096-aligned, columns 64-byte aligned. DebugValidation defaults to #if DEBUG. Mutation-checked model test: disabling swap-back fails 3/5 seeds. Not done here, handed forward: conflicting-op failure events (TASK-8, needs events), bulk playback paths (TASK-7 or later), archetype row-size overflow is detected at first use rather than Build.
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Entity storage merged to integration/v1 (01a984d): slabs, 64 KiB chunks, archetype SoA with aligned padded columns, generational EntityId, command buffers with deferred handles played back at Playback, chunk iteration via ChunkView Spans, Component identity by name+layout hash, non-fragmenting mode, scratch arena, debug validation (structural version checks, poisoning, leak report). Verified: 65 Core tests pass in Debug and 45 in Release (debug-only tests excluded), including a model-based test against a dictionary ECS over 5 seeds and a zero-allocation test over a 100k Entity loop; whole solution 92/92 Debug.
<!-- SECTION:FINAL_SUMMARY:END -->
