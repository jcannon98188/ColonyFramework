---
id: TASK-30
title: 'Benchmarks, Tick harness and the headless sample host'
status: In Progress
assignee:
  - '@jason'
created_date: '2026-10-31 01:56'
updated_date: '2026-10-31 13:49'
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
- [ ] #1 BenchmarkDotNet micro-benchmarks cover storage, scheduler, Fields, hashing and save/load and run from one command
- [ ] #2 The Tick harness runs 100k and 1M colonist scenarios with churn, Fields and save/load and writes JSON results comparable across runs
- [ ] #3 The headless sample host builds from the hello-colony sketch, runs on JIT and Native AOT, and the sketch in the spec compiles as a doc test
- [ ] #4 Deterministic-count gates (allocations, chunks visited) fail the suite when exceeded while timing is reported only
<!-- AC:END -->
