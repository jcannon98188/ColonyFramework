---
id: TASK-26
title: 'Streaming: residency manager and evict/reload round trip'
status: Done
assignee:
  - '@jason'
created_date: '2026-10-31 01:56'
updated_date: '2026-11-01 17:04'
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
- [x] #1 Focus points submitted as Input Commands drive residency transitions in the Streaming Phase in canonical chunk order, and the schedule replays deterministically
- [x] #2 Forcing a chunk out and back in produces a World hash identical to never unloading, including its chunk-owned Entities with stable IDs
- [x] #3 Pinned Entities survive their chunk unloading and can sit in unloaded space
- [x] #4 A Hibernating ring always surrounds Active chunks and stencils treat the frozen side as a fixed boundary
- [x] #5 Never-generated chunks are generated on demand through the ChunkProvider and are not stored until changed
- [x] #6 Streaming evict and reload round trips are added to the determinism harness
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Residency in Colony.Fields (focus points, Active/Hibernating/Unloaded, Frozen/Pinned, IChunkSource, ChunkResidencyManager), transitions only in Phase.Streaming; freeze-in-place via enable bits so reload hash equals never unloading. WorldGeneration is a ChunkSource. Streaming scenario in AOT host. Limits: frozen Entity bytes stay resident; IgnoreEnableBits Systems still see frozen rows; chunk source set after first use leaves stale cache; Apply on Unloaded chunk throws; no ProcGen scenario in AOT host.
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Streaming residency manager with evict/reload hash identity, Pinned Entities, on-demand generation and streaming scenario merged to integration/v1.
<!-- SECTION:FINAL_SUMMARY:END -->
