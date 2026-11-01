---
id: TASK-38
title: Fields stencil performance pass
status: To Do
assignee: []
created_date: '2026-11-01 21:36'
labels:
  - ready-for-agent
  - implementation
dependencies:
  - TASK-30
ordinal: 65000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
TASK-30 baselines show Fields cost dominates the Tick: colonist-100k single worker takes 16.7 ms with Fields on versus 1.65 ms off (the ECS prototype without Fields took 1.02 ms), and colonist-1m takes 34.9 ms versus 19.6 ms (prototype 7.8 ms, about 2.5x). Profile and reduce it without changing any World hash.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Profile of the colonist-100k and colonist-1m harness runs identifies where Fields time goes (stencil, change feed, chunk iteration, hashing), recorded in a note
- [ ] #2 Measured improvement on colonist-100k with Fields on, single worker and 8 workers, against the committed TASK-30 baseline, with the target set from the profile and stated in the ticket notes
- [ ] #3 All determinism goldens, save goldens and world hashes are unchanged; 0 B per Tick is preserved
- [ ] #4 Updated baselines committed and the harness gates still pass
<!-- AC:END -->
