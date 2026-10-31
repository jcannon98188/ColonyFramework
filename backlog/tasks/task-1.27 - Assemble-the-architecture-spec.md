---
id: TASK-1.27
title: Assemble the architecture spec
status: Done
assignee:
  - '@jason'
created_date: '2026-10-31 01:38'
updated_date: '2026-10-31 01:46'
labels:
  - 'wayfinder:task'
dependencies: []
parent_task_id: TASK-1
ordinal: 28000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Write the destination: one architecture spec document (docs/spec/architecture.md) assembled from all closed decision tickets, CPU-only v1. Must: state module/package boundaries and dependency direction; fold the amendments on the GPU-era tickets into a coherent CPU-only text (GPU as a later Colony.Gpu.* package); place the Input and Streaming Phases among the fixed Phases; give the hello-colony API sketch (Component, System, Field, build a World, Tick, save); link prototype evidence in docs/research; list v1 non-goals and the implementation-ticket seams (one per package, with the first vertical slice). Update GLOSSARY.md for any new domain terms (Movement class, Residency, Streaming Phase, Input Command, Pinned Entity). Decide whether any ADRs are warranted (data model, determinism tier, CPU-only v1).
<!-- SECTION:DESCRIPTION:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Also record in the spec's repo-structure section: development-only tooling lives in tools/, is never referenced by src/ and is not a shipped package. In the implementation-ticket seams, include an early optional ticket for the warm-Roslyn dev daemon (see the standalone task 'Dev daemon: warm Roslyn compilations and test feedback for agents'). Fresh implementation only; no code reused from the user's employer.

The spec is also stored in Backlog as doc-1 (Colony Framework architecture spec (v1)); docs/spec/architecture.md is the repo copy. Keep them in sync.

The spec is stored in Backlog as doc-1 (sections 1-8) and doc-2 (sections 9-16), split because a single CLI argument is limited to about 32 KB. docs/spec/architecture.md is the repo copy; keep them in sync.
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Wrote the architecture spec: docs/spec/architecture.md (16 sections). It assembles every closed decision ticket into one CPU-only v1 document: purpose and non-goals, principles, the twelve packages with dependency rules, data model, memory, scheduler and the Phase order (Streaming, Input, Begin, Simulate, Late, End), World/Fields/streaming, determinism, save/load and replay, networking enablers, Plugins and the C ABI, pathfinding, jobs/AI, procgen, diagnostics, inspection, testing, public API shape with an illustrative hello-colony sketch, the GPU deferral and its seams, prototype evidence with caveats, repo structure (including tools/ for dev-only tooling), an ordered implementation hand-off list with a first vertical slice (reproduce the ECS prototype colonist scenario with identical hashes), and open items.

GLOSSARY.md gained Phase, Input Command, Claim, Movement class, Residency and Pinned Entity.

Not done: no ADRs were written; candidates (archetype data model, Tier 1 determinism contract, CPU-only v1) are left for the user to decide. The hello-colony code is a shape sketch, not compile-checked.
<!-- SECTION:FINAL_SUMMARY:END -->
