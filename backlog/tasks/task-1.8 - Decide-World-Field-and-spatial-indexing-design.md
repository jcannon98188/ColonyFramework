---
id: TASK-1.8
title: 'Decide World, Field and spatial indexing design'
status: Done
assignee:
  - '@jason'
created_date: '2026-10-30 13:46'
updated_date: '2026-10-31 01:38'
labels:
  - 'wayfinder:grilling'
dependencies:
  - TASK-1.6
parent_task_id: TASK-1
ordinal: 9000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Question: How are Fields represented (chunked grids, configurable depth/z-levels, chunk size, sparse vs dense, double buffering), how do Entities map to tile positions (spatial index/hash, queries like nearest-X), and how do Fields share memory with GPU Backends?
<!-- SECTION:DESCRIPTION:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Decided with the user (all proposals accepted; Q1 and Q2 refined in discussion):

1. World: bounded dimensions fixed at creation (configurable, documented maximum such as 4096x4096x512), int tile coordinates, depth 1 = 2D. Streaming is designed for but not shipped in v1: chunk table keyed by chunk coordinate with a dense fast path for bounded worlds; chunk lifecycle (Load/Unload/Generate) behind a ChunkProvider interface; procedural generation as a pure function of seed and chunk coordinate; per-chunk persistence in the save format; GPU residency tracked per loaded chunk. Intended meaning of streaming: a large world with an active simulated region where Entities in unloaded chunks hibernate (a Game author may supply a catch-up rule). Streaming itself is added as fog ("Streaming worlds").
2. Field chunks: 16x16x16 tiles, chunk edge a build-time constant shared by all Fields; row-major layout inside the chunk (SIMD along x, trivial neighbour offsets, simple GPU indexing) behind a Field accessor so it can be revisited after prototypes; chunks are iterated and saved in Morton order of chunk coordinate for deterministic cache-friendly traversal; depth-1 Worlds use one layer of 16x16 slabs.
3. Uniform chunks: a chunk can hold a single value with no column allocated, promoted on first differing write, demoted by explicit compaction; saved as a single value.
4. Fields: named grid of one unmanaged cell struct, registered by the source generator with a stable name and layout hash; multi-property tiles are several Fields.
5. Neighbour access: double-buffered stencil by default (read/write buffers swapped at Phase boundary, framework-provided cross-chunk neighbour access); in-place ordered mode for cellular automata using fixed chunk colouring (e.g. 2x2x2 checkerboard) with deterministic order inside; both deterministic regardless of thread count.
6. Tiles vs Entities: things with identity/own state/movement are Entities (creatures, items, buildings, plants); per-tile terrain-like data is Fields. Grid Entities carry a TilePos Component (3 ints) with optional fixed-point sub-tile offset (no floats for deterministic movement).
7. Spatial index: grid of per-chunk Entity buckets updated incrementally when TilePos changes, at Phase-boundary command-buffer merge; queries by tile, box, radius and nearest-k with predicate; results in deterministic order (distance, then Entity ID); bucket size configurable per World; separate opt-in indexes per kind.
8. Change tracking: per-chunk per-Field change versions plus a World change feed of changed chunks per Tick in deterministic order; derived-data Systems (path regions, lighting, renderer) subscribe. Path-region design left to pathfinding.
9. Authority: each Field declares CPU-authoritative (default; dirty chunks uploaded) or GPU-authoritative (readback at defined Ticks or on demand); change versions drive partial transfers. Transfer timing and deferred-completion details go to the GPU Backend ticket.

**Amendment (CPU-only v1):** item 9 (Field authority) is reduced to CPU-authoritative Fields only; GPU-authoritative Fields and dirty-chunk upload/readback are deferred to a later optional GPU package. Row-major layout, Morton chunk order and the Field accessor are unchanged. Streaming is no longer only designed-for: see Decide streaming worlds for the v1 residency manager.
<!-- SECTION:FINAL_SUMMARY:END -->
