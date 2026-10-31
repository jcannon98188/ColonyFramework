---
status: accepted
---
# Archetype/chunk structure-of-arrays storage for Entities

The ECS stores Entities in archetype tables of fixed 64 KiB chunks with 64-byte aligned columns, rather than a sparse-set or per-Entity object model. Chunks are the unit of parallel work, hashing, saving and streaming, and the layout never depends on the CPU, which is what makes saves portable and the World hash identical across machines. The prototype hit 100k Entities in 1.02 ms/Tick single-threaded with zero allocation.

## Considered options

- **Sparse-set storage:** cheap add/remove, but no contiguous columns for SIMD or chunk-level work partitioning, and no canonical chunk memory to snapshot.
- **Archetype tables (chosen):** structural changes are costly, so frequent state is modelled with enable bits or data fields, with opt-in non-fragmenting Components for the rest.

## Consequences

Structural changes are deferred through command buffers merged at Phase boundaries; a ref is valid only until the next structural change. Changing the storage model later would invalidate the save format, the hash and the scheduler's chunk partitioning.
