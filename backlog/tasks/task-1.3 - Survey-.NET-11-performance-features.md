---
id: TASK-1.3
title: Survey .NET 11 performance features
status: Done
assignee:
  - '@research-agent'
created_date: '2026-10-30 13:46'
updated_date: '2026-10-30 13:50'
labels:
  - 'wayfinder:research'
dependencies: []
parent_task_id: TASK-1
ordinal: 4000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Question: Which .NET 11 RC2 runtime/language features matter for the Framework: Vector<T>/Vector128/256/512 and hardware intrinsics, TensorPrimitives, NativeMemory and alignment, ref structs / allows ref struct generics, Span, inline arrays, GC settings and NoGC regions, Native AOT constraints, source generators, anything new in 11? Note what is stable in RC2 versus preview-only.
<!-- SECTION:DESCRIPTION:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Findings in `docs/research/dotnet-11-performance-features.md` (cited to Microsoft Learn / dotnet/core; RC2 installed, RC3 notes exist, GA expected Nov 2026).

Stable: Vector128/256/512<T>, Vector<T> (256-bit unless DOTNET_MaxVectorTBitWidth=512), NativeMemory incl. AlignedAlloc, Span, allows ref struct, InlineArray, NoGC regions, source generators, Native AOT.
New in 11 (on by default in RC2): x86-64-v2 baseline, new lane ops (Zip/Unzip/Concat*), AVX-VNNI-512, more SVE2, FP16 Half, better JIT bounds-check/devirtualization, C# 15 default.
Preview/opt-in only: Runtime Async, C# 15 memory-safety rules, union scaffolding, AVX10.2.
Hazards for determinism: unchecked float-to-small-int conversions now saturate; width-dependent lane-crossing reductions.
Implications (inferred): prefer fixed-width VectorNNN<T> behind IsHardwareAccelerated; 64-byte aligned columns via AlignedAlloc; NoGC regions only as guard rail; runtime assembly loading conflicts with Native AOT (decide in plugin ticket).
Unconfirmed: AlignedAlloc/AlignedFree pairing, TensorPrimitives determinism, Vector512 downclocking, RC2 source generator changes. A small benchmark on RC2 would settle the hardware questions.
<!-- SECTION:FINAL_SUMMARY:END -->
