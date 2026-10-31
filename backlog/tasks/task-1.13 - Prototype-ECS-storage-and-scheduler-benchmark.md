---
id: TASK-1.13
title: Prototype ECS storage and scheduler benchmark
status: Done
assignee:
  - '@jason'
created_date: '2026-10-30 13:46'
updated_date: '2026-10-31 00:38'
labels:
  - 'wayfinder:prototype'
dependencies:
  - TASK-1.5
  - TASK-1.6
  - TASK-1.7
parent_task_id: TASK-1
ordinal: 14000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Question: Does the chosen ECS storage, memory model and scheduler hit the targets with a throwaway headless benchmark (100k colonists wandering and eating), measuring Tick time, allocations and scaling across cores? Link the prototype; it is discarded.

Execution: this prototype must be built by an Opus 5.5 agent (model claude-opus-5-5).
<!-- SECTION:DESCRIPTION:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Report in `docs/research/prototype-ecs-bench.md`; code in `prototypes/ecs-bench/` (throwaway, never imported by production code; kept as seed for the benchmark and determinism harnesses). Reviewed with the user; all eight proposals accepted.

**Results:** the decided storage, memory model and scheduler hit the main targets. 100k colonists: 1.02 ms/Tick on one thread, 0.20 ms on 12; 1M colonists 7.8 ms single-thread, 2.8 ms on 8 to 12 threads; zero managed allocation per Tick on all Tick threads; World hashes bit-identical across 1/2/4/16 threads and JIT/AOT builds, with a negative control failing as intended. Weak spots: scaling (5.1x at 100k, 2.8x at 1M, 1.5x under churn), all 16 logical CPUs slower than 6, serial command playback, no claim primitive (double-eat bug), AOT default ISA about 2x slower. Game logic, not the framework, dominated Tick time.

**Revisions adopted:**
1. **Workers (Q1):** default worker count is physical cores (minus 0 to 1), not logical cores, configurable; park on an allocation-free kernel wait after a short tunable spin. Amends the scheduler decision.
2. **Affinity (Q2):** assign chunk i to worker i mod N first, steal only on imbalance; minimum-entities-per-job knob and fusing of tiny jobs. Determinism is unaffected because job indices key buffers and reductions. An implementation tuning detail validated by benchmark, not a hard contract.
3. **Enable bits (Q3):** the standard way to model frequent state, steered by docs and analyzers; archetype-changing Components stay for rare structural traits (not mandatory).
4. **Playback (Q4):** bulk paths (reserve N rows per archetype and memcpy columns for batched creates; batched destroys) and deferred Entity handles from Create. Parallel archetype-partitioned playback is later work. Document a structural-churn budget (about 30 ns per operation per core).
5. **Contested Entities (Q5):** gameplay arbitration is the Claim Relation and two-phase resolve (jobs decision). Additionally the command-buffer layer defines a rule: conflicting ops on the same Entity in one Phase resolve first-in-stable-order wins, and the loser gets a deterministic failure event next Phase.
6. **SIMD/AOT (Q6):** every hot SIMD path has a Vector128 fallback; AOT packaging guidance sets IlcInstructionSet=x86-64-v3 for desktop; the CI determinism matrix covers ISA settings; no FMA, reassociated reductions or estimate instructions in deterministic paths.
7. **Scheduler shape (Q7):** keep waves; no DAG scheduler in v1; revisit if Plugin ecosystems produce lopsided waves.
8. **Closure (Q8):** this ticket closes with these revisions; the scheduler, memory and data-model map lines are amended rather than reopened.

**Not exercised by the prototype:** enable bits, deferred Entity handles, Relations, Fields, change detection, access enforcement, work-stealing deques, a real component registry. Treat results as evidence for the core loop only.
<!-- SECTION:FINAL_SUMMARY:END -->
