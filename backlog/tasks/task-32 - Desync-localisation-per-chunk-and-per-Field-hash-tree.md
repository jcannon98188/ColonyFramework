---
id: TASK-32
title: 'Desync localisation: per-chunk and per-Field hash tree'
status: Done
assignee:
  - '@jason'
created_date: '2026-11-01 21:28'
updated_date: '2026-11-01 21:56'
labels:
  - ready-for-agent
  - implementation
dependencies:
  - TASK-17
  - TASK-15
ordinal: 59000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
The hash trail names the first diverging Tick, Phase and System but not where in the World the states differ. Peers should be able to find the diverging chunks, Fields and Components by exchanging a few hashes, without transferring state.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 World exposes a hierarchical hash (World, then Components and Fields, then chunk) computed on demand at a Tick boundary, consistent with the existing World hash
- [x] #2 Two hash trees compare level by level and report the diverging chunk coordinates and Field or Component names, exchanging only hashes
- [x] #3 Building the tree allocates nothing in Ticks that do not ask for it and is identical on 1, 2, 4 and N workers
- [x] #4 colony-inspect desync reports the diverging chunk and Field or Component when given two trees or two live hosts
- [x] #5 A negative control with a deliberately perturbed chunk is localised exactly
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Merged at 2719a79. World.HashTree() (root == World.Hash(), hash bytes unchanged), IWorldExtension.HashTree default, FieldSet per-chunk leaves, Diagnostics .tree files and 'hashtree' server op, colony-inspect hashtree/desync over files or live hosts. Gaps: extension group name collision with core group names (tick, components, resources, tables, random) not rejected; spec does not describe .tree format or hashtree op (fold into spec sync).
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Desync localisation by hash tree: two peers exchange hashes level by level and name the diverging component/archetype/chunk or Field chunk. No allocation on Ticks that do not ask for the tree.
<!-- SECTION:FINAL_SUMMARY:END -->
