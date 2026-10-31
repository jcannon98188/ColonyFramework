---
id: TASK-1.16
title: Decide testing and benchmark strategy
status: Done
assignee:
  - '@jason'
created_date: '2026-10-30 23:31'
updated_date: '2026-10-31 01:38'
labels:
  - 'wayfinder:grilling'
dependencies: []
parent_task_id: TASK-1
ordinal: 17000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Question: How is the Framework itself tested and benchmarked? Cover: determinism tests (1-vs-N-thread World hash, cross-CPU-width and instruction-set settings, AOT vs JIT builds, save/load round-trip and migrations), Exact-Kernel CPU/GPU parity, zero-allocation-per-Tick harness (all threads), Plugin test harness package, ABI conformance tests, benchmark suite and regression thresholds (BenchmarkDotNet vs custom Tick harness), CI matrix (Windows/Linux, x86-64/ARM64, GPU-less runners), fuzz/property tests for structural changes and command buffers, and what the prototypes already built that can be reused. Inputs: determinism, save/load, scheduler and Plugin decisions; docs/research/prototype-ecs-bench.md and prototype-gpu-field.md.
<!-- SECTION:DESCRIPTION:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Decided with the user (all eleven proposals accepted). Informed by the ECS and GPU Field prototypes (docs/research/prototype-ecs-bench.md, prototype-gpu-field.md), whose hash test, negative control and CPU/GPU parity check become the seeds of the real harnesses.

**Layers (Q1):** six test layers, each in its own project: unit and property tests (storage, allocators, command buffers, math layer, fixed-point); simulation determinism tests; contract and conformance tests (Plugin ordering/conflict reports, C ABI, Backend parity); compile-time tests (source-generator snapshots, analyzer diagnostics); soak and stress tests (long runs, structural churn, mid-run save/load round trips); benchmarks. Test framework: the current default on Microsoft.Testing.Platform (xUnit v3 or TUnit), chosen at implementation time.

**Determinism (Q2):** a reusable determinism harness shipped as a package Plugins can also run. A seeded scenario runs N Ticks with World-hash checkpoints across a matrix: 1/2/4/N threads; JIT with default, DOTNET_EnableAVX2=0 and EnableAVX512=0; Native AOT. A negative control (for example RNG keyed by thread) must fail so the harness proves it can detect breakage. Golden hashes per scenario are committed and compared on every CI platform for cross-OS equality. Save, load and continue must equal a run that never saved.

**Allocation and memory checks (Q3):** an allocation harness measures every thread (per-thread counters, process total, GC count) and fails above a tiny tolerance (the prototype found a parked worker allocating). Debug builds run the memory-model validations (structural-version checks on refs/Spans, freed-memory poisoning, leak check at World dispose). Optional nightly sanitizer-style use-after-free stress.

**Generators, analyzers, ABI (Q4):** generator snapshot tests (Verify or equivalent); one positive and one negative test per analyzer rule; a C-language (or minimal native host) ABI conformance suite that loads the Framework, registers a Native Plugin and exercises every entry, run on JIT and Native AOT builds; the generated C header is checked in and diffed in CI to catch accidental ABI breaks.

**Benchmarks (Q5):** BenchmarkDotNet for micro-benchmarks (SIMD kernels, allocators, hash, command playback) plus a custom Tick benchmark harness derived from the ECS prototype for end-to-end scenarios (100k/1M colonists, churn, Fields, save/load), reporting mean, p50, p99, per-System time, thread scaling, allocation and peak memory. Results emitted as JSON for CI diffing.

**Performance regression policy (Q6):** performance is tracked, not gated, on shared runners: results stored per commit with a trend report flagging regressions. Hard gates only on deterministic quantities (allocations per Tick, counted operations such as chunk visits). A dedicated benchmark machine (dev box or self-hosted runner) runs nightly comparisons with thresholds that warn first and later fail.

**CI matrix (Q7):** PR matrix is Windows x64 and Linux x64 on JIT. Nightly adds Native AOT, instruction-set variants, ARM64 Linux (and macOS if available) and soak tests. GPU tests run on a self-hosted or dev machine; elsewhere use a software adapter if wgpu supports one, or skip with a clear marker. CI service is chosen when the repo gets a remote.

**Model-based tests (Q8):** a simple reference implementation (a dictionary-based "slow ECS") runs beside the real one on random operation sequences (create, destroy, add/remove Components, command buffers) with observable state compared; the same approach for Fields (naive grid), the spatial index and Relations. Failing seeds are saved as regression cases. Hashes prove sameness, not correctness (the prototype's deterministic double-eat bug is the example).

**GPU Backend tests (Q9, provisional on the pending GPU-in-v1 decision):** the CPU Backend is the oracle; Exact Kernels must be bit-identical on every shipped Backend through a shared parity suite any Backend package, including third-party, can run; Approximate Kernels are tested to an ULP tolerance. If GPU ships later as an optional package, the suite ships with it.

**Fixtures (Q10):** a non-shipped test content package with standard Components, Systems and Fields (the prototype's colonist scenario, a heat Field, a pathing grid); scenarios defined by seed plus parameters so failures reproduce from a seed; committed golden save files, one per container version, loaded by the current build to test migrations.

**Docs (Q11):** a short docs/testing.md (layers, local commands, how Plugin authors use the harness, CI matrix) is written when implementation starts, not as part of the spec.

**Handed forward:** package layout must place the determinism harness, parity suite, allocation harness, test content package and ABI conformance project; tooling work should expose per-System timing and all-thread allocation counters the harnesses use; the GPU-in-v1 decision settles Q9's scope.

**Amendment (CPU-only v1):** Q7's GPU test-machine line and Q9 (Backend parity suite, Approximate-Kernel ULP tests) are out of v1; the CPU implementation is the oracle for any future Backend and the parity suite ships with a future GPU package. Added scope from later decisions: pathfinding scenarios (unreachable targets, toggles, budget exhaustion), streaming forced evict/reload round trips, replay of streaming schedules and load-deadline stalls.
<!-- SECTION:FINAL_SUMMARY:END -->
