---
id: TASK-7
title: 'Scheduler, Phases and the Tick; reproduce the ECS prototype hash'
status: In Progress
assignee:
  - '@jason'
created_date: '2026-10-31 01:56'
updated_date: '2026-10-31 03:17'
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
- [ ] #1 The colonist scenario from the ECS prototype runs through World.Tick and produces the same World hash on 1, 2, 4 and N worker threads, and on JIT and Native AOT, with zero steady-state allocation
- [ ] #2 Systems declare access sets; conflicting Systems never run in the same wave; a stable topological sort by After/Before constraints with System ID ties orders Systems within a Phase, and cycles or ambiguous conflicting writers fail at World build
- [ ] #3 All six Phases run in order each Tick with command-buffer merge at each boundary, and a builder can insert a Game Phase between named ones
- [ ] #4 Conflicting structural ops on one Entity within a Phase resolve first-in-stable-order and the loser receives a deterministic failure event the next Phase
- [ ] #5 The World hash covers Component columns and RNG state, excludes padding and change versions, and is cheap enough to run every N Ticks in a test
- [ ] #6 A System exception aborts the Tick and marks the World faulted naming the System; TickClock handles accumulator, pause, speed and catch-up cap; a Tick gate lets the host hold a Tick
- [ ] #7 A negative-control test that introduces a thread-order dependency fails the hash comparison
<!-- AC:END -->

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
