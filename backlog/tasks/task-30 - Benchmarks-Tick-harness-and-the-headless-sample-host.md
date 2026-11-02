---
id: TASK-30
title: 'Benchmarks, Tick harness and the headless sample host'
status: Done
assignee:
  - '@jason'
created_date: '2026-10-31 01:56'
updated_date: '2026-10-31 20:43'
labels:
  - ready-for-agent
  - implementation
dependencies:
  - TASK-14
documentation:
  - >-
    backlog/docs/spec/doc-1 -
    Colony-Framework-architecture-spec-v1-part-1-of-2.md
  - >-
    backlog/docs/spec/doc-2 -
    Colony-Framework-architecture-spec-v1-part-2-of-2.md
type: task
ordinal: 57000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Spec sections 10.3, 11.1 and 14: performance is tracked, not gated, so the Framework needs BenchmarkDotNet micro-benchmarks plus a custom Tick harness (100k and 1M colonists, churn, Fields, save and load) with JSON output that a dedicated machine can compare nightly; hard gates apply only to deterministic counts. The hello-colony sketch becomes the minimal headless host sample and a compile-checked doc test, giving later tickets a host to run sinks and inspection against. Prototype numbers (1.02 ms per Tick for 100k colonists single-threaded, 7.8 ms for 1M) are the baseline to re-measure.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 BenchmarkDotNet micro-benchmarks cover storage, scheduler, Fields, hashing and save/load and run from one command
- [x] #2 The Tick harness runs 100k and 1M colonist scenarios with churn, Fields and save/load and writes JSON results comparable across runs
- [x] #3 The headless sample host builds from the hello-colony sketch, runs on JIT and Native AOT, and the sketch in the spec compiles as a doc test
- [x] #4 Deterministic-count gates (allocations, chunks visited) fail the suite when exceeded while timing is reported only
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Baselines (i9-9900KF, .NET 11 RC2): colonist-100k 1 worker Fields on mean 16.75 ms, 0 B/tick, save 56 ms/load 41 ms; no-fields 1.65 ms; 8 workers p50 6.49 ms. colonist-1m 1 worker Fields on 34.9 ms, no-fields 19.6 ms; save 134 ms/load 106 ms. Diagnostics overhead +0.2% (TickOff 1.695 vs TickStandard 1.699 ms). Spec 11.1 sketch deviations: plain struct + AddFields/AddField, NamedPhase.Simulate, WorldOptions.Seed, ColonySystems.AddSystems(builder), create via command buffer. Follow-ups: Fields stencil perf (~15 ms/Tick at 100k single-threaded), linux-x64 AOT unverified.
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Benchmarks, tick harness with baselines/gates, HelloColony AOT sample and CI publish step merged to integration/v1.
<!-- SECTION:FINAL_SUMMARY:END -->
