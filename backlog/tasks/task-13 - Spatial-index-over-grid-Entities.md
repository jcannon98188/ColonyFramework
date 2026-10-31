---
id: TASK-13
title: Spatial index over grid Entities
status: To Do
assignee: []
created_date: '2026-10-31 01:56'
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
- [ ] #1 Tile, box, radius and nearest-k queries with a predicate return results ordered by distance then Entity ID
- [ ] #2 The index updates incrementally in the Late Phase from TilePos changes and a model-based test compares every query against a naive grid after random moves
- [ ] #3 Querying from parallel Systems allocates nothing and yields identical results across thread counts
- [ ] #4 The index is excluded from the World hash and rebuilt on demand
<!-- AC:END -->
