---
id: TASK-7
title: 'Scheduler, Phases and the Tick; reproduce the ECS prototype hash'
status: Done
assignee:
  - '@jason'
created_date: '2026-10-31 01:56'
updated_date: '2026-10-31 03:51'
labels:
  - ready-for-agent
  - implementation
dependencies:
  - TASK-4
  - TASK-6
documentation:
  - >-
    backlog/docs/spec/doc-1 -
    Colony-Framework-architecture-spec-v1-part-1-of-2.md
  - >-
    backlog/docs/spec/doc-2 -
    Colony-Framework-architecture-spec-v1-part-2-of-2.md
type: task
ordinal: 34000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Spec section 4.3 and the spec acceptance for the Core slice: the host calls World.Tick, Systems with declared read/write access run in waves of non-conflicting Systems on the Framework work-stealing pool, chunks are distributed across workers with affinity and stealing, and command buffers merge at each of the six Phase boundaries (Streaming, Input, Begin, Simulate, Late, End) in System order then record order. Determinism under parallelism rests on Systems writing only their declared columns partitioned by chunk, with reductions combined in chunk order. Wall-clock time never enters the simulation; a TickClock helper and a shared Tick gate live on the host side. The World hash defined in section 6 is the acceptance oracle. Systems are hand-written entry structs here; the source generator ticket produces them later from attributes.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 The colonist scenario from the ECS prototype runs through World.Tick and produces the same World hash on 1, 2, 4 and N worker threads, and on JIT and Native AOT, with zero steady-state allocation
- [x] #2 Systems declare access sets; conflicting Systems never run in the same wave; a stable topological sort by After/Before constraints with System ID ties orders Systems within a Phase, and cycles or ambiguous conflicting writers fail at World build
- [x] #3 All six Phases run in order each Tick with command-buffer merge at each boundary, and a builder can insert a Game Phase between named ones
- [x] #4 The World hash covers Component columns and RNG state, excludes padding and change versions, and is cheap enough to run every N Ticks in a test
- [x] #5 A System exception aborts the Tick and marks the World faulted naming the System; TickClock handles accumulator, pause, speed and catch-up cap; a Tick gate lets the host hold a Tick
- [x] #6 A negative-control test that introduces a thread-order dependency fails the hash comparison
- [x] #7 Conflicting structural ops on one Entity within a Phase resolve first-in-stable-order (System order, job order, record order); stale ops are skipped and counted (the loser failure event moved to TASK-8)
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Scheduler: SystemSchedule with stable topo sort (System ID ties), waves of non-conflicting Systems, Build fails on cycles, cross-Phase constraints and ambiguous conflicting pairs; WorldSchema.Systems exposes Order/Wave/Reads/Writes. Fixed worker pool (WorldOptions.WorkerThreads), chunk jobs with affinity and stealing, per-System command buffers merged per Phase boundary in System->job->record order, World.ResetScratch at End. Game Phases via WorldBuilder.AddPhase(name, after). TickClock and TickGate on the host side. World hash = Hash64 over archetype columns (archetype ID then chunk order), Entity column, non-fragmenting stores, RNG state and Tick; excludes padding, change versions, asleep bits. Fnv1a deleted; schema/layout hashes now Hash64. Bug fixed under load: ScratchArena.Alloc raced across workers renting command-buffer pages (now serialised with Lock). AOT evidence: tests/Colony.Core.AotHost published with dotnet publish -c Release -r win-x64 prints the same three checkpoint hashes as JIT for 1 and 4 workers; the test Assert.Skips when no publish output exists (CI does not publish it yet). Follow-ups: declared Reads/Writes are not enforced at runtime (candidate for a debug ChunkView check or analyzer); bulk playback not implemented; Systems with empty access sets are treated as non-conflicting (spec silent).
<!-- SECTION:NOTES:END -->

## Comments

<!-- COMMENTS:BEGIN -->
author: @jason
created: 2026-10-31 03:00
---
Hand-forward from TASK-6: World.Playback is the sync point to call at each Phase boundary in System order; World.ResetScratch is the end-of-Tick sync point. Bulk playback paths (reserve N rows + memcpy) are not implemented; add them if the colonist scenario's playback shows up in profiles.
---

author: @jason
created: 2026-10-31 03:17
---
Hand-forward from TASK-4: when wiring the World hash use Colony.Hashing.Hash64 and replace the internal Fnv1a placeholder used for Component layout identity (schema hash values will change; no saves exist yet).
---
<!-- COMMENTS:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Scheduler, Phases and Tick merged to integration/v1 (7a17d47). The ECS prototype colonist scenario (100k colonists, 5k food, 1% churn, 60 Ticks) hashes identically on 1/2/4/N threads and on JIT vs a published Native AOT host, with zero allocation on every Tick thread; a thread-order-dependent negative control fails the comparison. 284 Core tests (1 AOT test skipped when the host is not published); solution 411 passed.
<!-- SECTION:FINAL_SUMMARY:END -->
