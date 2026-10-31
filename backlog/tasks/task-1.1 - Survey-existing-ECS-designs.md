---
id: TASK-1.1
title: Survey existing ECS designs
status: Done
assignee:
  - '@research-agent'
created_date: '2026-10-30 13:46'
updated_date: '2026-10-30 13:53'
labels:
  - 'wayfinder:research'
dependencies: []
parent_task_id: TASK-1
ordinal: 2000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Question: What can we learn from existing ECS implementations (.NET: Arch, Friflo.Engine.ECS, DefaultEcs, Leopotam; others: flecs, Bevy, Unity DOTS) about storage layout (archetype vs sparse-set vs hybrid), query design, structural-change cost, entity relationships and parallel scheduling, with an eye to 100k+ Entities, unmanaged Components and GPU upload? Capture findings and trade-offs, not a recommendation.
<!-- SECTION:DESCRIPTION:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Findings in `docs/research/ecs-designs.md` (Arch, Friflo, DefaultEcs, Leopotam ecslite, flecs, Bevy, Unity DOTS; primary sources, inferences tagged).

- Storage: archetype/table is dominant (Arch, Friflo, flecs, Bevy default, DOTS 16 KiB chunks); DefaultEcs is sparse-set; flecs/Bevy allow opt-in sparse components.
- Queries: archetype designs cache matching tables; flecs makes caching optional; Friflo forbids structural change inside a query loop.
- Structural change: all parallel-capable libraries defer via command buffers (sync points); Friflo has batch ops, notes parallel command buffers can be slower.
- Relationships: flecs pairs / Bevy ChildOf can fragment archetypes; Friflo offers fragmenting links and non-fragmenting relations.
- Parallelism: Bevy and DOTS derive conflicts from declared access; flecs slices tables across threads.
- Gaps: no primary source on GPU upload; no independent 100k+ benchmark; Arch chunk size docs disagree; command-buffer merge order/determinism unsettled (matters for the determinism ticket).
<!-- SECTION:FINAL_SUMMARY:END -->
