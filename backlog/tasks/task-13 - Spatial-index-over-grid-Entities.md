---
id: TASK-13
title: Spatial index over grid Entities
status: Done
assignee:
  - '@jason'
created_date: '2026-10-31 01:56'
updated_date: '2026-10-31 08:04'
labels:
  - ready-for-agent
  - implementation
dependencies:
  - TASK-12
documentation:
  - >-
    backlog/docs/spec/doc-1 -
    Colony-Framework-architecture-spec-v1-part-1-of-2.md
  - >-
    backlog/docs/spec/doc-2 -
    Colony-Framework-architecture-spec-v1-part-2-of-2.md
type: task
ordinal: 40000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Spec section 5.1: per-chunk Entity buckets updated incrementally in the Late Phase, supporting queries by tile, box, radius and nearest-k with predicate, with results ordered by distance then Entity ID so parallel queries are deterministic. The index is rebuildable state (not saved). It feeds the work index and streaming later. A naive grid is the model-based oracle.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 Tile, box, radius and nearest-k queries with a predicate return results ordered by distance then Entity ID
- [x] #2 The index updates incrementally in the Late Phase from TilePos changes and a model-based test compares every query against a naive grid after random moves
- [x] #3 Querying from parallel Systems allocates nothing and yields identical results across thread counts
- [x] #4 The index is excluded from the World hash and rebuilt on demand
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Merged into integration/v1 at 8d1e000 (2026-10-31). Full solution Release: 488 total, 487 passed, 1 skipped (AOT host). New in Colony.Fields: SpatialIndexBuilder (builder.AddSpatialIndex(fieldsBuilder, tilePosType), world.SpatialIndex()), SpatialIndex, SpatialHit/TileBox/ISpatialPredicate/AnyEntity; extension 'spatial-index' registers Late System 'spatial.index' (Reads+ChangeFilter TilePos). Storage: per-chunk intrusive doubly linked buckets via per-Entity-slot arrays (8 B/chunk + 32 B/Entity slot), O(1) upsert/remove, no per-bucket allocation. Removals detected by a liveness sweep over indexed slots when World.Counters.Destroys/Removes moved (Core has no per-Entity removal feed). Queries fill caller spans ordered by squared whole-tile distance then EntityId; short span keeps nearest; nearest-k walks chunk shells with exact lower-bound stop; struct predicates so parallel queries allocate nothing. Core change (additive): World.Playback bumps ChangeCounter once per sync point (World.BeginWave) so ChangeFilter sees structural changes stamped right after the filtered System's wave and pre-first-Tick host playbacks; new SyncPointChangeTests. Follow-ups: host writes via World.Get<TilePos> are not change-stamped (Core-wide) - move from a System with Writes or call Rebuild(); removal sweep is O(indexed) per destroy Tick, a per-Entity removal feed would make it exact-incremental; nearest-k on a near-empty large grid is O(chunk count); residency-aware behaviour (Hibernating/Unloaded) left to TASK-25/26.
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Spatial index in Colony.Fields: tile/box/radius/nearest-k queries with struct predicates, results ordered by distance then EntityId, incrementally maintained in Late from TilePos changes, verified against a naive-grid oracle under random moves/creates/destroys, zero-alloc and thread-count-invariant, excluded from the World hash and rebuildable on demand. Merged at 8d1e000.
<!-- SECTION:FINAL_SUMMARY:END -->
