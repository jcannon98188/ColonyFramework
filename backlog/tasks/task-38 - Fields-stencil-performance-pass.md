---
id: TASK-38
title: Fields stencil performance pass
status: Done
assignee:
  - '@jason'
created_date: '2026-11-01 21:36'
updated_date: '2026-11-01 22:13'
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
- [x] #1 Profile of the colonist-100k and colonist-1m harness runs identifies where Fields time goes (stencil, change feed, chunk iteration, hashing), recorded in a note
- [ ] #2 Measured improvement on colonist-100k with Fields on, single worker and 8 workers, against the committed TASK-30 baseline, with the target set from the profile and stated in the ticket notes
- [x] #3 All determinism goldens, save goldens and world hashes are unchanged; 0 B per Tick is preserved
- [x] #4 Updated baselines committed and the harness gates still pass
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Merged at 7cc103a. StencilInput ctor gathers a 1-cell halo into per-thread scratch; Read is AggressiveInlining with three tiers. colonist-100k 1 worker 18.08 -> 12.96 ms (Fields 16.1 -> 11.0, 1.46x), 8 workers 7.11 -> 4.69; 1m 36.34 -> 31.20 (1w), 14.71 -> 11.70 (8w). Hashes and goldens unchanged, 0 B/Tick. AC#2 (2x) NOT met: 1.46x; remainder needs a halo/row-span kernel API, filed as a follow-up ticket.
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Fields stencil reads inlined via a gathered halo; Fields share of the 100k Tick 1.46x faster, hashes unchanged. The 2x target was not reached; follow-up ticket filed for a halo kernel API.
<!-- SECTION:FINAL_SUMMARY:END -->
