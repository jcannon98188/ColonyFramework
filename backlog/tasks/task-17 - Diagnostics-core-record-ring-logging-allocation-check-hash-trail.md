---
id: TASK-17
title: 'Diagnostics core: record ring, logging, allocation check, hash trail'
status: Done
assignee:
  - '@jason'
created_date: '2026-10-31 01:56'
updated_date: '2026-10-31 08:15'
labels:
  - ready-for-agent
  - implementation
dependencies:
  - TASK-7
documentation:
  - >-
    backlog/docs/spec/doc-1 -
    Colony-Framework-architecture-spec-v1-part-1-of-2.md
  - >-
    backlog/docs/spec/doc-2 -
    Colony-Framework-architecture-spec-v1-part-2-of-2.md
type: task
ordinal: 44000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Spec section 10.1 (decided in the profiling and diagnostics ticket): always-on numeric counters in a preallocated per-Tick record ring with a versioned layout, cost levels Off/Standard/Detailed with Standard under about 1% of Tick time, an allocation-free in-process read API (world.Diagnostics), a first-class all-thread allocation check, an optional Tick budget with slow-Tick reports, structured allocation-free logging (message IDs plus numeric args drained between Ticks, no wall-clock time), flag-controlled validation switches and an optional hash trail per Tick/Phase/System/archetype. Core never references Diagnostics; it exposes hooks through interfaces and struct callbacks. The ECS prototype showed per-System timing finds hotspots immediately while per-job timing is itself measurable overhead, hence the levels.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 world.Diagnostics returns the latest N Tick records as structs or spans with Tick, Phase, per-System time, chunks visited and skipped, worker busy/spin/park and memory versus budget, without allocating
- [ ] #2 Standard level overhead is measured by a benchmark and stays under about 1% of Tick time on the prototype colonist scenario; Off records only Tick time and the allocation check
- [x] #3 Structured logging from Systems allocates nothing, carries Tick and System ID, and resolves text only when drained
- [x] #4 The allocation check reports the System running when a managed allocation occurred, per thread
- [x] #5 With the hash trail enabled, two runs that diverge report the first differing Tick, Phase and System
- [x] #6 Record layout is versioned and Core has no reference to Colony.Diagnostics (verified by the dependency check)
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Merge attempt 1 (2026-10-31) bounced: full-solution Release test run failed 4/4 attempts. LogDrainTests.Entries_are_formatted_on_drain_in_tick_system_job_order_regardless_of_thread_count fails ~1/3 in isolation (drain order follows worker completion, not JobIndex) - violates AC #3. Also flaky under load: Structured_logging_allocates_nothing..., AllocationTests.A_100k_entity..., StandardLevelOverheadTests (5% timing gate). Sent back to implementer; integration/v1 reset to 955b6a4.

Merged into integration/v1 at a0dd0a4 (2026-10-31) after rework; full solution Release green 3/3 (534 total, 1 skipped AOT). Bounce root cause: workers run their own job stripe then steal, so per-worker LogRings were not in job order and the k-way merge assumed sorted input -> completion-order drains. Fix: LogEntry.Sequence per ring, DrainLogs sorts each ring in place (zero-alloc struct comparer) by (Tick, SystemId, JobIndex, Sequence) then merges; new LogDrainTests.Drained_text_is_identical_for_1_and_8_workers_over_repeated_runs. Flaky gates: Colony.Core.Tests runs with TieredCompilation/TieredPGO off (new test projects with zero-alloc gates should do the same); overhead test is report-only (best-of-8 interleaved, asserts equal hashes and Standard < 2x Off) - measured 1.5% / -1.5% on 350 jobs/Tick x 4 workers, i.e. within noise of the 1% target, so AC #2 is left unchecked and the 1% gate goes to TASK-30 benchmarks; allocation-check test derives expected workers from the record instead of assuming job spread. Layout: record types/options/listener/read API in Core (src/Colony.Core/Diagnostics, namespace Colony.Diagnostics) because Core writes them; the Colony.Diagnostics package holds consumers (LogDrain, HashTrailRecorder, HashTrail.Compare); sinks are TASK-18. world.Diagnostics.Latest / Record(ago) return TickRecordView (ref struct over preallocated arrays). Counter seam for TASK-16: WorldBuilder.AddCounter(name) -> CounterId, SystemContext.Count, WorldDiagnostics.AddToCounter; registration closes at Build; plugin-ID tagging left to TASK-16. Hook order: BeginTick diag then extensions; EndPhase extensions then diag (trail hashes extension state); EndTick extensions then diag. Follow-ups: hash trail serialises wave Systems; Off level still fills cheap counters; spot check detects out-of-Tick mutation only; slow-Tick/allocation reports reach IDiagnosticsListener only until TASK-18.
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Diagnostics core: zero-alloc Tick record ring (per-System time, chunks visited/skipped, worker busy/spin/park, memory vs budget) with Off/Standard/Detailed levels, structured logging with Tick+System+Job-ordered thread-independent drain, per-worker allocation check naming the System, deterministic hash trail with first-divergence report, tick budget and rate-limited slow reports, access checking, counters, LayoutVersion stamping, Core free of any Colony.Diagnostics reference. Standard overhead measured ~1.5% (report-only; 1% gate deferred to TASK-30). Merged at a0dd0a4.
<!-- SECTION:FINAL_SUMMARY:END -->
