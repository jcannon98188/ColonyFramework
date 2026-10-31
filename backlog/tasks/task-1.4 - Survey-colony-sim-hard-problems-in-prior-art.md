---
id: TASK-1.4
title: Survey colony-sim hard problems in prior art
status: Done
assignee:
  - '@research-agent'
created_date: '2026-10-30 13:46'
updated_date: '2026-10-30 13:53'
labels:
  - 'wayfinder:research'
dependencies: []
parent_task_id: TASK-1
ordinal: 5000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Question: How do Dwarf Fortress, RimWorld and similar games (and public talks/articles by their authors) solve pathfinding at scale, job/task assignment, fluid/temperature simulation, simulating off-screen areas, and save/load of huge worlds? Capture known pitfalls and techniques to inform Framework scope and fog tickets.
<!-- SECTION:DESCRIPTION:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Findings in `docs/research/colony-sim-prior-art.md`.

- Pathfinding: DF and RimWorld both fixed unreachable-target A* freezes with connected-component/region IDs checked before search; hard part is keeping IDs current as terrain changes (RimWorld ~5.5 ms full regen on 200x200, so only local regen); flyers need separate connectivity; Factorio uses a hierarchical 32x32 abstract map as heuristic.
- Fluids: DF water is a 3D cellular automaton (0-7 per cell), nonlocal pressure search, "static" flag to skip settled cells; pitfalls: update-order dependence, waterfall artifacts.
- Temperature: RimWorld per-room equalisation every 120 ticks; DF per-tile/item (wiki-level only).
- Off-screen: best source is Factorio active-set scheduling (idle entities off, bucketed chunk updates).
- Save/load: only a fixed-early-format and endianness remark found.
- Gaps: job assignment, off-screen sim, save architecture lack primary sources; DF wiki rate-limited; verify the ~40-cell region cutoff claim.
<!-- SECTION:FINAL_SUMMARY:END -->
