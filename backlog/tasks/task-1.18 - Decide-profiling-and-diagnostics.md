---
id: TASK-1.18
title: Decide profiling and diagnostics
status: Done
assignee:
  - '@jason'
created_date: '2026-10-30 23:39'
updated_date: '2026-10-31 01:38'
labels:
  - 'wayfinder:grilling'
dependencies: []
parent_task_id: TASK-1
ordinal: 19000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Question: How does the Framework expose runtime performance and health data? Cover: per-System and per-Phase timing, per-thread and total allocation counters, chunk/archetype/memory stats and World budget use, worker utilisation and scheduling traces (which the test harnesses and benchmarks consume), Field/Kernel/Backend timing and fence waits, tracing format and sinks (EventSource/ETW/EventPipe, Chrome-trace or Tracy-style), cost when disabled (zero-allocation, near-zero overhead), debug-build validation switches and diagnostics levels, logging API and determinism-safe logging inside Systems. Inputs: scheduler, memory, determinism, testing decisions; docs/research/prototype-ecs-bench.md (per-System time and the all-thread allocation finding).
<!-- SECTION:DESCRIPTION:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Decided with the user (all ten proposals accepted). Informed by the ECS prototype (docs/research/prototype-ecs-bench.md): per-System timing found a 22 ms game-logic hotspot immediately, while per-job Stopwatch calls around ~0.2 us jobs are themselves measurable overhead.

**Measurements (Q1):** fixed always-on counters stored in a preallocated ring of per-Tick records (numeric only; no allocation or strings on the hot path): per Tick wall time and Phase/wave timings; per System wall time and summed CPU time, chunks visited/skipped (asleep bit or rate), entities touched; per worker busy/spinning/parked time; memory (slabs, chunks in use vs budget, scratch-arena high-water mark, Buffer Component overflows); command-buffer sizes and playback time; Field/Backend Kernel time, dirty-chunk counts, transfer bytes, fence waits.

**Cost levels (Q2):** per-World setting: Off (per-Tick wall time and the allocation check only), Standard (default; per-System timing at System/wave granularity with one timestamp per System per worker accumulated in thread-local slots, target under about 1% of Tick time, verified by a perf-suite benchmark), Detailed (per-job timing and chunk-level traces for investigations).

**Exposure (Q3):** an allocation-free in-process read API on the host side (world.Diagnostics) returning the latest N Tick records as plain structs/spans, with opt-in sinks built on it: an EventSource/Meter bridge (dotnet-counters, OpenTelemetry), Chrome-trace/Perfetto JSON trace export, and the inspection protocol (next ticket) streaming the same records.

**Allocation checking (Q4):** first-class diagnostic. Debug and test builds record managed allocations per Framework thread per Tick (per-thread counters, process total, GC count) and report violations with the System running at the time; usable in release for soak runs. The testing strategy's allocation harness consumes this API.

**Slow-Tick detection (Q5):** the host sets an optional Tick budget; exceeding it raises a rate-limited slow-Tick report event (that Tick's record, top N Systems by time, GPU fence waits). Nothing is logged by default; the Game author decides the response.

**Logging (Q6):** Systems log through a structured allocation-free API: message IDs registered at startup plus a small fixed set of numeric arguments, written to per-thread ring buffers drained by the host between Ticks; text resolved only when drained. Logs carry Tick number and System ID, never wall-clock time; logging never changes simulation state. Free-form string logging only outside Ticks.

**Validation switches (Q7):** separate from profiling level, flag-controlled: structural-version checks on refs/Spans, freed-memory poisoning, leak checks at World dispose, access-declaration checking (a System touching an undeclared Component), determinism spot-checks (periodic World hash recomputation and cross-check). Debug builds default on; release compiles cheap ones out via a constant and keeps the rest behind a runtime flag.

**Determinism diagnostics (Q8):** optional hash trail: per-Tick hashes broken down by Phase, System and archetype/Field, so comparing two runs shows the first diverging Tick, Phase and System. Cost is paid only when enabled; comparison tools belong to the inspection tooling ticket.

**GPU and Backend diagnostics (Q9):** the Backend interface exposes timing hooks (Kernel time via timestamp queries where supported, otherwise fence timing; upload/readback bytes; queue depth; fence waits; device-loss events) feeding the same records. Provisional on the GPU-in-v1 decision, but the hooks belong to the Backend abstraction either way.

**Extension (Q10):** Plugins (and Native Plugins via the C ABI) register named counters and timed sections in Register; they appear in the same records tagged with the Plugin ID. Registration closes before the first Tick so the record layout is fixed and allocation-free. The record layout is versioned because the trace and protocol sinks depend on it.

**Handed forward:** the inspection ticket builds the protocol, hash-trail comparison and desync finder on these records; package layout must place the diagnostics API, sinks (EventSource bridge, trace export) and logging; the testing harnesses depend on the allocation API and Standard-level overhead benchmark; the scheduler implementation must keep per-System timing per worker in thread-local slots.

**Amendment (CPU-only v1):** Q9 (GPU and Backend diagnostics: Kernel timing, transfer bytes, fence waits, device loss) is out of v1 and returns with a future GPU package; the slow-Tick report drops GPU fence waits. Added from later decisions: streaming counters (residency counts, load queue depth, stalls), pathfinding counters (request queue, work units spent, repair backlog).
<!-- SECTION:FINAL_SUMMARY:END -->
