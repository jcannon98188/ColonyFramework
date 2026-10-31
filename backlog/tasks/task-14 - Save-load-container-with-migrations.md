---
id: TASK-14
title: Save/load container with migrations
status: To Do
assignee: []
created_date: '2026-10-31 01:56'
labels:
  - ready-for-agent
  - implementation
dependencies:
  - TASK-9
  - TASK-12
documentation:
  - >-
    backlog/docs/spec/doc-1 -
    Colony-Framework-architecture-spec-v1-part-1-of-2.md
  - >-
    backlog/docs/spec/doc-2 -
    Colony-Framework-architecture-spec-v1-part-2-of-2.md
type: task
ordinal: 41000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Spec section 7.1: a save is a snapshot of canonical chunk memory (little-endian, CPU-independent) in one sectioned container with header, manifest (schema, Plugin set, Tick, World hash, seed), table of contents and one section per Field chunk and per archetype, with per-section xxHash, LZ4-class compression, atomic write with backup generations. Rebuildable state is not saved. A schema table embedded in the save lets matching layout hashes bulk-copy while mismatches run registered chained per-Component migrations; no migration means a clear failure. The acceptance that matters most is from section 6: load-then-continue is bit-identical to never saving.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 world.Save writes the sectioned container atomically; World.Load builds a fresh World from it; a scenario saved at Tick T and continued to T+K hashes identically to an unsaved run at T+K
- [ ] #2 Per-section hashes are verified on load and corruption of any section fails with a clear error naming the section
- [ ] #3 A Component whose layout hash changed is migrated through registered N to N+1 migrations chained across versions; a missing migration fails loading with a clear message
- [ ] #4 Golden saves per container version are committed and loaded in tests; compression is per section and optional
- [ ] #5 Background save via copy-on-write snapshot produces a save identical to a synchronous save at the same Tick
<!-- AC:END -->
