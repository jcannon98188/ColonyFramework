---
id: TASK-1.12
title: Decide Plugin and extension architecture
status: Done
assignee:
  - '@jason'
created_date: '2026-10-30 13:46'
updated_date: '2026-10-31 01:38'
labels:
  - 'wayfinder:grilling'
dependencies:
  - TASK-1.5
  - TASK-1.7
parent_task_id: TASK-1
ordinal: 13000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Question: What extension points make modding and scripting first-class without mandating a language or mod format: Plugin loading and isolation (AssemblyLoadContext, AOT implications), registering Components/Systems/Fields, data-driven definitions, versioned API surface, ordering and conflicts between Plugins.
<!-- SECTION:DESCRIPTION:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Decided with the user. Prior art raised by the user: Dwarf Fortress supports data-driven mods only, with scripting via external native tooling (DFHack) injected into the process. That led to dropping the three-mode proposal in favour of two extension surfaces.

**Surfaces (Q1, revised twice):** the Framework provides exactly two extension surfaces.
1. **C# API**, used by Game authors and by any library they reference (including reusable third-party NuGet packages with a Register method; that is a convention, not a separate mode). Covers registering Components, Resources, Relations, Fields, Systems, Events, Kernels and Definitions, plus the host-side Tick/Save/Load API. Runtime loading of C# assemblies (AssemblyLoadContext) is the Game author's concern: they can load assemblies and call the same API; at most a documented recipe or optional helper package outside the core.
2. **C ABI**, for Modders and non-C# or script-runtime extensions: a narrow, versioned function table, loaded in-process (NativeLibrary) from a mods folder when the host opts in. Works with both Native AOT and JIT hosts. Exposes schema introspection (names, layout hashes, field offsets), read-only chunk views valid within a Tick and checked against the structural version, command-buffer submission as the only write path, RNG streams, event queues and Definition lookups. Native Plugin Systems run under the same scheduler with access declared in data. Caveat: a Plugin built as a Native AOT .NET library carries its own runtime and GC; two AOT runtimes in one process is untested.

**Plugin contract (Q2):** a Plugin has a manifest (stable ID, semantic version, required API version, Plugin dependencies, Deterministic/NonDeterministic declaration) and one entry point, Register, called once at World setup. Nothing registers outside it; registration closes before the first Tick. Native Plugins register the same things through ABI calls, describing Components through schema registration (a C header can be generated from the same metadata).

**Source generation (Q3):** applies to the C# side only; Framework generators and analyzers ship as one NuGet package; no runtime reflection scanning, so trimming and AOT stay safe.

**Isolation (Q4):** no sandbox claimed for any Plugin type (.NET has none, and native code has none). Trust, signing and allow-lists are the Game author's problem. No runtime unloading in v1; Plugin sets are fixed per World load.

**Ordering and conflicts (Q5):** Plugin load order from the declared dependency graph, ties by Plugin ID; Systems ordered by Phase plus After/Before constraints (which may name another Plugin's System). Duplicate Component/Field names, cyclic constraints and unorderable writers fail at World build with a clear report. A Plugin may replace or disable another Plugin's System only if marked Overridable; replacement is recorded in the save manifest.

**Data-driven definitions (Q6):** a Definition registry: typed, schema-bound unmanaged-struct definitions with interned names, loaded through a pluggable IDefinitionSource (we ship the interface, a reference JSON source, and ID/reference resolution). Definitions have stable IDs, reference each other by name, are patchable by later Plugins (add/override/remove by ID in load order), and are baked into immutable blob data at load. File format and modding workflow remain the Game author's call.

**Scripting (Q7, revised):** no language shipped or mandated. A script runtime is a Plugin of either surface (a compiled-in C# library or a Native Plugin can host Lua, WASM or anything). The bindings surface is one narrow, handle-based, versioned API (queries over Components, command buffers, RNG streams, event queues, Definition lookups), defined once and surfaced in C# and in C. Script Systems are ordinary Systems under the scheduler and determinism rules; no raw memory pointers for writes.

**Native AOT (Q8, revised):** Native AOT games get data modding, script modding and native-code modding; only arbitrary managed C# mods need the game to be a JIT build, which is the Game author's choice (as in DF: data mods plus external native tooling).

**Versioning (Q9, revised):** semantic versioning with two version numbers (C# API, C ABI). A small separate Colony.Abstractions assembly holds the Plugin contract and registration API. Plugins declare the minimum version needed; the host refuses newer requirements. No promises during 0.x.

**Obligations (Q10):** save/determinism obligations ride on the manifest and Register: managed-state serialize/restore callbacks (or declare derivable), migrations, Deterministic/NonDeterministic declaration; Plugin identity and version go in save manifests and replay logs; the 1-vs-N-thread hash harness ships as a package any Plugin can run.

**World access (Q11):** only through Systems, registered event handlers (typed event queues as Resources) and the host-side API outside Ticks. No global World access.

**Out-of-process attach (Q12):** v1 Native Plugins load in-process with host opt-in. An out-of-process inspection protocol (shared memory or socket, read-mostly) belongs to the profiling/debug tooling work in the fog.

**Timing (Q13):** the spec defines the C ABI shape; implementation ships after the C# path; no earlier decision needs to change.

**Handed forward:** package layout must place Colony.Abstractions, the C ABI header and loader, generators/analyzers and the test harness; the ECS prototype's AOT check should note the two-AOT-runtimes caveat; tooling ticket owns the out-of-process protocol; testing strategy covers Plugin ordering/conflict reports and ABI conformance tests.

**Amendment (CPU-only v1):** Plugins do not register GPU Kernels in v1; the Kernel registration point is removed from the v1 C# API and returns with a later GPU package.
<!-- SECTION:FINAL_SUMMARY:END -->
