---
status: accepted
---
# Colony.Colony references Colony.Pathfinding for its reachability filters

Spec section 9.2 has reachability filter work-index and hauling candidates, but section 3 let `Colony.Colony` depend only on Core, Fields and Persistence. So the work index and hauling matcher took `IWorkFilter` / `IHaulFilter` struct predicates, and every Game had to write the same small adapter over `PathfindingService.Reachable`. We now allow the one-way edge `Colony.Colony -> Colony.Pathfinding` and ship the adapters (`ReachableWorkFilter`, `ReachableHaulFilter`) in `Colony.Colony` (TASK-41).

## Considered options

- **Allow the reference (chosen):** Pathfinding already depends only on Core and Fields, so there is no cycle. The `Colony` meta-package already brings both packages, and a colony sim that uses the work index almost always uses pathfinding too. It costs one line in the dependency table.
- **A glue package (for example `Colony.Colony.Pathfinding`):** keeps `Colony.Colony` free of pathfinding, but adds a package (and its versioning, packing and meta-package entry) for two small structs. Rejected as cost with no user: we know of no Game that wants Claims and the work index without Pathfinding.
- **Leave the adapter to Games:** the status quo the ticket was raised to fix.

## Consequences

- `tools/Colony.DependencyCheck` lists `Colony.Colony -> [Core, Fields, Pathfinding, Persistence]`. `Colony.Pathfinding` must never reference `Colony.Colony` (tested), so the edge stays one way.
- The `IWorkFilter` / `IHaulFilter` seams stay, so Games can still combine reachability with their own rules (zones, capacity) in a struct of their own.
- Spec section 3's rule for `Colony` should list this exception next to the others "decided at implementation"; the spec text itself is unchanged by this ADR.
