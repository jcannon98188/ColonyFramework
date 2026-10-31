---
id: TASK-26
title: 'Streaming: residency manager and evict/reload round trip'
status: To Do
assignee: []
created_date: '2026-10-31 01:56'
labels:
  - ready-for-agent
  - implementation
dependencies:
  - TASK-15
  - TASK-25
documentation:
  - >-
    backlog/docs/spec/doc-1 -
    Colony-Framework-architecture-spec-v1-part-1-of-2.md
  - >-
    backlog/docs/spec/doc-2 -
    Colony-Framework-architecture-spec-v1-part-2-of-2.md
type: task
ordinal: 53000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Spec section 5.3 (decided in the streaming worlds ticket): v1 ships the seams plus a basic residency manager, with the bounded fully-resident World as the never-unload policy. Chunks are Active, Hibernating or Unloaded; transitions happen only in the Streaming Phase in canonical chunk order, driven by Game focus points and radii that arrive as Input Commands so the schedule is replayable. Entities are chunk-owned by position and hibernate or unload with their chunk unless pinned (global store). A ring of Hibernating chunks always surrounds Active ones so halo reads are valid. An Unloaded chunk is exactly its save section; never-generated chunks are produced on demand from (seed, coordinate). Chunk coordinates stay 64-bit-safe.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Focus points submitted as Input Commands drive residency transitions in the Streaming Phase in canonical chunk order, and the schedule replays deterministically
- [ ] #2 Forcing a chunk out and back in produces a World hash identical to never unloading, including its chunk-owned Entities with stable IDs
- [ ] #3 Pinned Entities survive their chunk unloading and can sit in unloaded space
- [ ] #4 A Hibernating ring always surrounds Active chunks and stencils treat the frozen side as a fixed boundary
- [ ] #5 Never-generated chunks are generated on demand through the ChunkProvider and are not stored until changed
- [ ] #6 Streaming evict and reload round trips are added to the determinism harness
<!-- AC:END -->
