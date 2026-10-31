---
id: TASK-1.23
title: Decide procedural generation architecture
status: Done
assignee:
  - '@jason'
created_date: '2026-10-30 23:40'
updated_date: '2026-10-31 01:38'
labels:
  - 'wayfinder:grilling'
dependencies:
  - TASK-1.21
parent_task_id: TASK-1
ordinal: 24000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Question: What does the Framework provide for procedural generation and how does it fit the Plugin, determinism and streaming designs? Cover: staged generation pipeline (extensible steps registered by Plugins, ordering, conflicts), pure functions of seed and chunk coordinate, counter-based RNG streams, cross-chunk features (rivers, structures, caves) and overlap/border handling, generation of Fields and Entities, GPU suitability (Exact kernels), validation/determinism tests, and which generators ship (noise utilities, WFC, graph tools) vs Game author territory. Inputs: research on procgen approaches, World/Field design, determinism contract, Plugin architecture.
<!-- SECTION:DESCRIPTION:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Decided with the user (all ten proposals accepted). Informed by docs/research/procgen-approaches.md.

**Core model (Q1):** generation is a pure function of (seed, chunk coordinate), delivered through the ChunkProvider contract. Any chunk can be generated alone, in any order, on any thread, with identical bytes. Generation never reads mutable World state.

**Pipeline (Q2):** named stages (e.g. base terrain, climate, biomes, caves, resources, surface features, structures, Entity placement), each declaring the Fields it reads and writes and running per chunk. Plugins register stages and order them with before/after by name (not a single integer). Cycles, duplicate names and unorderable writers fail at World build; replacing a stage requires Overridable.

**Randomness (Q3):** counter-based RNG keyed by (world seed, stage name hash, chunk or region coordinate, per-use index), so adding or removing a stage never shifts another stage's numbers. No shared sequential generator. Seeds are full 64-bit throughout.

**Cross-chunk features (Q4):** (1) region-keyed placement: large features decided per coarse region from seed and region alone (spacing, separation, salt); each chunk generates only its slice from the recomputed descriptor. (2) Halo reads: a stage declares a read halo evaluated over neighbours' base data only; it never reads same-stage neighbour output and never writes outside its chunk. Features needing a global solve (e.g. drainage-graph rivers) run in a deterministic world-level pre-pass on a coarse grid, cached as a generated Resource.

**What is generated (Q5):** Fields as pure per-chunk functions. Entities via placement stages emitting spawn lists per chunk or region with Entity IDs derived from (seed, stage, coordinate, index), so regenerating a chunk yields the same Entities and IDs. Bounded v1 generates once at World creation and saves the result; later streaming regenerates unmodified chunks when the generation version matches.

**GPU (Q6):** Field stages written as Exact Kernels (integer or fixed-point noise) may run on a Backend and must be bit-identical to the CPU path. The CPU path is the baseline; GPU generation is optional acceleration pending the GPU-in-v1 review. Float noise is Approximate and not allowed in the deterministic generation path; the math layer supplies noise primitives.

**Validation (Q7):** extend the determinism harness with golden hashes per stage and seed, an order-independence check (isolation vs neighbour context, random chunk orders and thread counts), a seam checker, extensible connectivity validators, and per-stage per-chunk timing in the profiling records.

**Debugging (Q8):** reuse the inspection protocol: stage outputs readable as Field slices with suggested palettes, a preview call that generates a region without creating a World, and a run-to-stage-N mode. No renderer.

**What ships (Q9):** in the Framework: seeded integer/fixed-point noise utilities (value, gradient, simplex, cellular, fBm, domain warping, region-keyed spot sampling), pipeline/region/halo machinery, the world-level pre-pass hook and test tooling. Optional packages: a graph/network toolkit (Poisson-disc, Voronoi, spanning/path networks) and a WFC helper for small bounded content only (deterministic, restart on contradiction, own seeded stream). Biomes, resources, terrain rules, structures and content are Game author territory.

**Versioning and Plugins (Q10):** the manifest records a generation version (hash of stage list, Plugin versions and parameters). Saves store generated state, so loading never regenerates; regenerating a chunk is valid only if the generation version is unchanged. Generation parameters are a Resource and Definitions drive tuning. Plugins register stages, noise functions and spawn tables through Register; C ABI Plugins get the same stage API with chunk views and RNG streams.

**Handed forward:** package layout places noise utilities, pipeline, graph toolkit and WFC helper; the streaming-worlds fog relies on pure generation and the generation version; the GPU-in-v1 decision settles Q6's scope.

**Amendment (CPU-only v1):** Q6 (optional Exact GPU generation stages) is out of v1; generation is CPU-only. Float noise remains excluded from the deterministic path.
<!-- SECTION:FINAL_SUMMARY:END -->
