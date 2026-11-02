---
id: TASK-41
title: Reachability adapter between Colony and Pathfinding
status: Done
assignee:
  - '@jason'
created_date: '2026-11-01 21:36'
updated_date: '2026-11-02 02:08'
labels:
  - ready-for-agent
  - implementation
dependencies:
  - TASK-19
  - TASK-23
ordinal: 68000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
The work index and hauling matcher take IWorkFilter and IHaulFilter predicates because Colony.Colony may not reference Colony.Pathfinding, so only a test-side adapter over Reachable exists. Every Game would write the same adapter. Decide the dependency (rule change, or a small glue package) and ship a ready-made adapter.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 A decision recorded in an ADR or the dependency rules: either allow Colony.Colony to reference Colony.Pathfinding or add a small glue package, with the reason
- [x] #2 A ready-made filter adapter over Pathfinding.Reachable for the requesting movement class works with the work index and hauling matcher
- [x] #3 Tests replace the test-side adapter with the shipped one and still pass, including doors opening and closing; allocation gates pass
- [x] #4 Dependency check updated and clean
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Merged at 7ec580c. ADR 0004: Colony.Colony may reference Colony.Pathfinding (one way). ReachableWorkFilter and ReachableHaulFilter (readonly record structs, allocation-free, deterministic from parallel jobs). Spec section 3 line ~57 still says Colony depends on Core and Fields only: fold into final spec sync.
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Shipped reachability filters bridging the work index and Pathfinding regions, with ADR 0004 and a dependency-rule update; verified with doors toggling under parallel jobs on 1 and 4 workers.
<!-- SECTION:FINAL_SUMMARY:END -->
