---
id: TASK-1.21
title: Survey procedural generation approaches for colony sims
status: Done
assignee:
  - '@jason'
created_date: '2026-10-30 23:40'
updated_date: '2026-10-30 23:45'
labels:
  - 'wayfinder:research'
dependencies: []
parent_task_id: TASK-1
ordinal: 22000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Question: How do Dwarf Fortress, RimWorld, Minecraft, Caves of Qud, Wave Function Collapse, Factorio and noise-based/graph-based generators structure world generation (terrain, biomes, rivers, caves, ores, history/factions), staged pipelines (e.g. RimWorld GenSteps, Minecraft noise and region generation), determinism and seeding (counter-based RNG, per-chunk pure functions), streaming/chunked generation and cross-chunk features, performance and GPU suitability, modding/extension of generators, and editing/validation tools? Output: docs/research/procgen-approaches.md with cited primary sources.
<!-- SECTION:DESCRIPTION:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Findings in `docs/research/procgen-approaches.md`. Claims are labelled by source quality ([P] first-party, [W] wiki, [S] interview, [I] inference, [U] unverified).

- Position-pure generation is the cleanest design (Factorio noise expressions of x,y compiled to SIMD-batched programs, seeded per layer; Minecraft's JSON density-function noise router). Fits "generation as a pure function of seed and chunk coordinate" and Exact GPU Kernels.
- Large features are keyed to a coarse region, not a chunk (Minecraft structure sets with spacing/separation/salt; Factorio spot noise per region). Neighbouring chunks hold references and clip their own part.
- Order dependence is the main determinism pitfall: Minecraft features writing into generated neighbours make results route-dependent (MC-55596); RimWorld roads sit outside the world seed and 1.6 fixed non-deterministic generation sources; Minecraft once truncated seeds to 48 bits. Inference: make cross-chunk decoration a pure function of a neighbourhood halo.
- WFC is a poor fit for per-chunk pure functions or GPU Kernels (sequential solver, contradictions yield no output); Caves of Qud uses it only for ruin interiors.
- Extensibility is data-driven and name-based (Factorio property_expression_names; RimWorld GenSteps ordered by one integer, so mods conflict; before/after ordering by name suggested).
- Debug tooling worth copying: RimWorld noise visualisers, Factorio per-program complexity log. Proposals with no source: golden hashes per stage, isolation-vs-context comparison to catch order dependence, seam checker, connectivity validators.

Gaps: no primary source for Dwarf Fortress worldgen; RimWorld GenStep order and RNG isolation unread; talks seen as titles only; Philox paper unreadable; nothing on connectivity guarantees, GPU worldgen, or graph-based generators.

Feeds the procgen decision ticket.
<!-- SECTION:FINAL_SUMMARY:END -->
