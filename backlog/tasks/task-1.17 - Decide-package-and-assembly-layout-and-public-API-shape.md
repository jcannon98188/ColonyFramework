---
id: TASK-1.17
title: Decide package and assembly layout and public API shape
status: Done
assignee:
  - '@jason'
created_date: '2026-10-30 23:31'
updated_date: '2026-10-31 01:35'
labels:
  - 'wayfinder:grilling'
dependencies: []
parent_task_id: TASK-1
ordinal: 18000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Question: How is the Framework split into packages/assemblies and what is the public API shape? Cover: core vs optional packages (Colony.Abstractions, ECS core, math/fixed-point/RNG, Fields, scheduler, save/load, Definitions, C ABI header and loader, source generators and analyzers, Backend abstraction and any GPU Backend, test harness), dependency direction, what is public vs internal given unsafe confinement, Native AOT/trimming annotations (IsAotCompatible), NuGet/versioning, repo/solution structure. Provisional pieces: the GPU Backend package and some ECS details depend on the review of the three prototypes (ECS, GPU Field, GPU pathfinding); treat them as provisional.
<!-- SECTION:DESCRIPTION:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Decided with the user (all eleven proposals accepted). CPU-only v1; the GPU Backend package is out of v1 and would later be a separate `Colony.Gpu.*` family.

**Packages (Q1):** `Colony.Core` (Entity/Component storage, memory model, scheduler, command buffers, Resources, events, World/Tick API, math layer, fixed-point, RNG; deliberately one package because ECS, memory and scheduler are tightly coupled); `Colony.Fields` (Field chunks, spatial index, change feed, ChunkProvider, streaming residency manager); `Colony.Persistence` (save/load container, migrations, replay log, input commands); `Colony.Pathfinding`; `Colony.Colony` (jobs/claims, work index, hauling matcher, priority/utility helpers); `Colony.ProcGen`; `Colony.Plugins` (manifest/loader, Definition registry, Overridable Systems, C ABI host); `Colony.Diagnostics` (profiling sinks, logging, inspection server, colony-inspect CLI); `Colony.Generators` (source generator plus analyzers); `Colony.Testing` (determinism, allocation, parity and ABI conformance tooling); `Colony.Abstractions` (only contracts Plugins or separately versioned packages compile against); `Colony.Native` (AOT shared library for the C ABI). A `Colony` meta-package references the common set.

**Dependency direction (Q2):** strictly layered. Core depends only on the BCL; Fields, Persistence and Plugins depend on Core; Pathfinding, Colony and ProcGen depend on Core and Fields (Persistence only where they save state); Diagnostics plugs into Core through hook interfaces and struct callbacks so Core never references it; nothing depends on Diagnostics, Testing or Plugins; a CI dependency check enforces this. Plugins is a leaf so a Game can run without the Plugin machinery.

**Public vs internal (Q3):** safe by default (typed handles, ref structs, Span views, generated typed queries); `unsafe` only in internal code, plus a small documented `Colony.Unsafe` namespace marked `[Experimental]` for advanced users (raw chunk pointers for custom SIMD). Everything else internal; InternalsVisibleTo only for tests; generated code in the user's assembly uses only public surface (with `[EditorBrowsable(Never)]` hooks where needed).

**API shape (Q4):** small and data-oriented: `World` (create, Tick, Submit, Save/Load), generated typed queries, attributed Systems, Resources/Events/Fields accessors, Definitions, and a `WorldBuilder` as the single composition root (Components, Systems, Fields, Plugins, budgets, determinism flags, ChunkProvider). No static state or singletons, so many Worlds coexist in one process. WorldBuilder validates at build time (layout hashes, access conflicts, Plugin ordering, Field names), fails with structured errors and produces an immutable `WorldSchema` shared across Worlds; registration closes before the first Tick.

**Generators and analyzers (Q5):** one analyzer package, incremental generators targeting netstandard2.0 with deterministic, snapshot-tested output; no runtime reflection anywhere; analyzer rules cover determinism bans, access-declaration checks and unsafe misuse. Runtime assemblies target net11.0 only.

**AOT and trimming (Q6):** every runtime package sets IsAotCompatible and IsTrimmable with warnings as errors; only Colony.Plugins' managed Plugin loader may load assemblies, behind RequiresUnreferencedCode/RequiresDynamicCode and optional (the C ABI path and statically linked Plugins work under AOT). AOT publish of a sample host is a CI target for every PR.

**C ABI packaging (Q7):** `Colony.Native` produces an AOT shared library plus a checked-in generated C header (colony.h) built from the same registrations and diffed in CI; versioned independently from the C# API.

**Versioning (Q8):** all packages share one lockstep SemVer version (0.x = breaking changes allowed); the Plugin C# API version and the C ABI version are separate numbers recorded in the Plugin manifest and in saves; save container and replay log carry their own format versions; PublicApiAnalyzers track the public surface (PublicAPI.Shipped.txt).

**Repo structure (Q9):** one solution: `src/<Package>/`, `tests/<Package>.Tests/` (six test layers in their own projects), `benchmarks/`, `samples/` (minimal headless host and a C native host), `docs/`, `prototypes/` (throwaway, not built by the solution), central package management, shared Directory.Build.props (analyzers, AOT and determinism flags) and global.json pinning the SDK.

**Deferred (Q10):** license, remote hosting and CI service are chosen when the repo gets a remote; not architectural.

**Hello colony sketch (Q11):** written in the final assembly as a spec artifact (about 30 lines: Component, System, Field, build a World, Tick, save); it becomes the first sample and a compile-checked doc test.

**Handed forward:** final assembly rewrites the GPU-era decisions as CPU-only, states Streaming Phase placement among the Phases, and includes the hello-colony sketch.
<!-- SECTION:FINAL_SUMMARY:END -->
