---
id: TASK-32
title: 'Desync localisation: per-chunk and per-Field hash tree'
status: To Do
assignee: []
created_date: '2026-11-01 21:28'
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
- [ ] #1 World exposes a hierarchical hash (World, then Components and Fields, then chunk) computed on demand at a Tick boundary, consistent with the existing World hash
- [ ] #2 Two hash trees compare level by level and report the diverging chunk coordinates and Field or Component names, exchanging only hashes
- [ ] #3 Building the tree allocates nothing in Ticks that do not ask for it and is identical on 1, 2, 4 and N workers
- [ ] #4 colony-inspect desync reports the diverging chunk and Field or Component when given two trees or two live hosts
- [ ] #5 A negative control with a deliberately perturbed chunk is localised exactly
<!-- AC:END -->
