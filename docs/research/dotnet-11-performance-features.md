# .NET 11 (RC2) performance features relevant to the Framework

Research for TASK-1.3. Installed SDK: `11.0.100-rc.2.26504.105`; runtime `Microsoft.NETCore.App 11.0.0-rc.2.26504.105` (verified with `dotnet --list-sdks/--list-runtimes`). Research date: 2026-10-30. GA is expected November 2026, and RC3 release notes already exist upstream.

## How to read this

Each claim cites its owning primary source (Microsoft Learn first-party docs and dotnet/core release notes). Status labels:

- **Stable**: shipped before .NET 11, or in .NET 11 RC2 with no preview or experimental flag.
- **RC2, no flag**: new in 11 and on by default, but still pre-GA, so a late change is possible.
- **Preview/opt-in**: needs `LangVersion=preview`, a feature flag, or is documented as preview or experimental.
- **Unverified**: I could not confirm it against a primary source in this pass.

## Summary for the Framework

| Area | Verdict for Framework | Status in RC2 |
| --- | --- | --- |
| `Vector128/256/512<T>`, `Vector<T>` | Core of any SoA/Field kernel work. Use fixed-width types, not `Vector<T>`. | Stable |
| New lane ops (`Zip`, `Unzip`, `Concat*`, `CreateGeometricSequence`) | Useful for interleaving SoA and AoS data portably. | RC2, no flag |
| `TensorPrimitives` | Free vectorized span math for Fields (add, multiply, sum, min/max, etc.). | Stable package; 11 build is `11.0.0-rc.1` |
| `NativeMemory` aligned alloc | Right tool for 64-byte aligned Field and column storage. | Stable |
| Ref structs, `allows ref struct`, `Span<T>` | Use for zero-copy views over columns. | Stable |
| `[InlineArray]` | Fixed-size per-entity buffers without heap allocation. | Stable (with an `Equals`/`GetHashCode` caveat) |
| GC settings, `TryStartNoGCRegion` | Useful as a safety net inside a Tick, not as a primary strategy. | Stable |
| Native AOT | Possible, but constrains Plugin loading and reflection. | Stable (with limits) |
| Source generators | Preferred replacement for reflection-based Component registration. | Stable |
| Runtime Async | Probably irrelevant to the simulation hot path. | Preview (opt-in) |
| C# 15 unions, closed hierarchies | Optional modelling aids. Not performance features. | Language features; `union` types need the 11 SDK |
| C# 15 memory-safety rules | Affects `unsafe` code style; the full rule set is opt-in. | Preview/opt-in |

## 1. SIMD: Vector<T>, Vector128/256/512, hardware intrinsics

### Stable surface

The docs describe three layers: `Vector<T>` (portable, variable width), `Vector64/128/256/512<T>` (cross-platform, fixed width, "the recommended starting point for new" code), and platform-specific intrinsics. `TensorPrimitives` is a fourth, ready-made layer. [SIMD and hardware intrinsics in .NET](https://learn.microsoft.com/en-us/dotnet/standard/simd)

Facts that matter for design:

- `Vector<T>.Count` is fixed for the process lifetime but varies by CPU. The JIT treats it as a constant. [SIMD doc](https://learn.microsoft.com/en-us/dotnet/standard/simd)
- `Vector<T>` does not automatically grow to the widest hardware width. `Vector512<T>.IsHardwareAccelerated` can be `true` while `Vector<T>` stays 256-bit. `DOTNET_MaxVectorTBitWidth=512` opts `Vector<T>` into 512 bits. [SIMD doc](https://learn.microsoft.com/en-us/dotnet/standard/simd)
- `IsHardwareAccelerated` and `Count` should be checked directly, not cached, because the JIT folds them into constants. Guard each width with both `VectorNNN.IsHardwareAccelerated` and `VectorNNN<T>.IsSupported`. [SIMD doc](https://learn.microsoft.com/en-us/dotnet/standard/simd)
- Lane-crossing operations (pairwise reductions and similar) do not scale simply with width, so the result can depend on the width. This matters for determinism (see Implications). [SIMD doc](https://learn.microsoft.com/en-us/dotnet/standard/simd)
- Load and store helpers: `Load`, `LoadUnsafe`, `LoadAligned`, `LoadAlignedNonTemporal`, `Store*`, `CopyTo`, `TryCopyTo`. [SIMD doc](https://learn.microsoft.com/en-us/dotnet/standard/simd)
- Test knobs: `DOTNET_EnableAVX2=0`, `DOTNET_EnableHWIntrinsic=0`, `DOTNET_PreferredVectorBitWidth`, plus per-ISA switches (`EnableAVX512`, `EnableAVX10v1`, ...). These are diagnostic only, not a stability contract, and they do not affect ReadyToRun or Native AOT code compiled ahead of time. [SIMD doc](https://learn.microsoft.com/en-us/dotnet/standard/simd)
- In that doc's knob table, `EnableAVX10v2` defaults to `0` (opt-in). [SIMD doc](https://learn.microsoft.com/en-us/dotnet/standard/simd)

### New in .NET 11 (RC2, no flag)

- **Minimum hardware raised.** Baseline for x86/x64 goes from `x86-64-v1` to `x86-64-v2` (adds POPCNT, SSE3 through SSE4.2, CX16). .NET 11 fails to start on CPUs missing it. The ReadyToRun target rises to `x86-64-v3` (AVX, AVX2, BMI1/2, FMA, F16C, LZCNT, MOVBE) on Windows and Linux. On Arm64 Windows the baseline requires LSE. [What's new in the .NET 11 runtime](https://learn.microsoft.com/en-us/dotnet/core/whats-new/dotnet-11/runtime)
  - Implication: SSE4.2 and POPCNT can be assumed everywhere. AVX2 is not guaranteed for JIT or AOT unless the app sets a target ISA. Still gate it with `IsHardwareAccelerated`.
- **Lane construction and composition APIs** on `Vector64/128/256/512<T>` and `Vector<T>`: `CreateGeometricSequence`, `CreateAlternatingSequence`, `CreateHarmonicSequence`, `Zip`/`ZipLower`/`ZipUpper`, `Unzip`/`UnzipEven`/`UnzipOdd`, the `Concat*` family and `Reverse`. [runtime](https://learn.microsoft.com/en-us/dotnet/core/whats-new/dotnet-11/runtime); [libraries](https://learn.microsoft.com/en-us/dotnet/core/whats-new/dotnet-11/libraries)
- **AVX-VNNI-512** (`AvxVnni.V512`) and several SVE/SVE2 additions on Arm64. Also, with SVE-enabled runtime builds, `Vector<T>` is passed by reference, and Preview 7 notes say `Vector<T>` reports its real SVE width instead of being capped at 128 bits. [runtime](https://learn.microsoft.com/en-us/dotnet/core/whats-new/dotnet-11/runtime); [Preview 7 runtime notes](https://github.com/dotnet/core/blob/main/release-notes/11.0/preview/preview7/runtime.md)
- **Codegen improvements:** `Vector128.Dot` lowering on AVX now uses `mul + permute + add`; hardware FP16 for `Half` arithmetic (AVX10.1 on x64); better x86/x64 SIMD cost modeling; faster Arm64 `Count`/`IndexOf`/`LastIndexOf`. [runtime](https://learn.microsoft.com/en-us/dotnet/core/whats-new/dotnet-11/runtime)
- **JIT scalar and bounds-check improvements** that help span loops: elimination for `i + cns < len`, for index-from-end (`values[^1]`), and after `!span.IsEmpty`. Also unchecked `float`/`double` to small-integer conversions now saturate instead of wrapping. [runtime](https://learn.microsoft.com/en-us/dotnet/core/whats-new/dotnet-11/runtime)
  - Implication: this is a behavior change. Game code that relied on wrapping conversions will differ from .NET 10.

### Preview-only or caveats

- AVX10.2 is opt-in (`EnableAVX10v2` default `0`). [SIMD doc](https://learn.microsoft.com/en-us/dotnet/standard/simd)
- Unverified: whether Vector512 downclocking is still a concern on current Intel hardware. Not covered by the docs I read. Benchmark on target hardware.

## 2. TensorPrimitives

- `System.Numerics.Tensors.TensorPrimitives`: static vectorized operations over spans (element-wise arithmetic, exponentials, reductions, `CosineSimilarity`). The SIMD guide says to prefer it over hand-written loops: it "often delivers most of the benefit of hand-written SIMD." [SIMD doc](https://learn.microsoft.com/en-us/dotnet/standard/simd)
- Shipped as the NuGet package `System.Numerics.Tensors`; the API page lists `v11.0.0-rc.1.26425.128` for .NET 11. It also supports net10.0 and .NET Framework/netstandard2.0 targets (page monikers). [TensorPrimitives API](https://learn.microsoft.com/en-us/dotnet/api/system.numerics.tensors.tensorprimitives)
- Preview 7 breaking change: `TensorPrimitives.Clamp` no longer throws when `min > max`; the scalar path now matches the vectorized path. [Preview 7 libraries notes](https://github.com/dotnet/core/blob/main/release-notes/11.0/preview/preview7/libraries.md)
- `ReadOnlySpan<T>.Min/Max` (no enumerator allocation) were added in Preview 7. [Preview 7 libraries notes](https://github.com/dotnet/core/blob/main/release-notes/11.0/preview/preview7/libraries.md)
- Unverified in this pass: the exact list of operations that are in-place safe when `x` and `y` overlap, and per-operation determinism guarantees. Check the per-method API docs before building on them.

## 3. NativeMemory and alignment

- `System.Runtime.InteropServices.NativeMemory` provides `Alloc`, `AllocZeroed`, `Realloc`, `Free`, `AlignedAlloc`, `AlignedRealloc`, `AlignedFree`, `Clear`, `Copy`, `Fill`. Available since .NET 6. [NativeMemory](https://learn.microsoft.com/en-us/dotnet/api/system.runtime.interopservices.nativememory)
- The benchmarking section of the SIMD guide recommends `AlignedAlloc` for stable benchmark results, because randomized allocation alignment adds noise between runs. [SIMD doc](https://learn.microsoft.com/en-us/dotnet/standard/simd)
- Pairing rule (not stated on the page I fetched; standard C-heap semantics, unverified here): memory from `AlignedAlloc` must be released with `AlignedFree`, not `Free`. Confirm on the method page before relying on it.
- No new `NativeMemory` APIs appear in the .NET 11 libraries notes I read (RC2 and Preview 7 notes list none). [Preview 7 libraries notes](https://github.com/dotnet/core/blob/main/release-notes/11.0/preview/preview7/libraries.md)
- Alternative for managed arrays: `GC.AllocateArray<T>(n, pinned: true)` places data on the Pinned Object Heap. Unverified in this pass (not fetched).

## 4. Ref structs, allows ref struct, Span, inline arrays

- **`allows ref struct`** is a generic anti-constraint (C# 13). A type argument may be a `ref struct`, and the generic code must obey ref-safety rules. It cannot be combined with `class`/`class?` and must be the last constraint. [where constraint](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/keywords/where-generic-type-constraint)
  - The ref-struct-interfaces proposal page was a 404 at the URL I tried, so interface-implementation rules are unverified here.
- **`[InlineArray(N)]`** on a struct with one field replicates that field N times in the layout. Stable since .NET 8. In .NET 9+, the default `Equals`/`GetHashCode` of such types throw `NotSupportedException`; override both if the type is used as a key or compared. [InlineArrayAttribute](https://learn.microsoft.com/en-us/dotnet/api/system.runtime.compilerservices.inlinearrayattribute)
- **.NET 11 Span-related additions:** span-based compression encoders and decoders with `Reset()`; span Base64 APIs; `Utf8/Utf16.IndexOfInvalidSubsequence`; `BitArray` span constructors and `PopCount()`; `StringBuilder.MoveChunks`; four zero-copy `Stream` types over memory. [libraries](https://learn.microsoft.com/en-us/dotnet/core/whats-new/dotnet-11/libraries)
- **C# 15 relevant items** (all from the C# 15 page): [What's new in C# 15](https://learn.microsoft.com/en-us/dotnet/csharp/whats-new/csharp-15)
  - Collection expression arguments `[with(capacity: n), .. xs]`: lets collection expressions pre-size a `List<T>`. RC2, no flag (need the 11 SDK).
  - Union types (`union Pet(Cat, Dog, Bird)`), closed hierarchies, extension indexers, labeled `break`/`continue`: no `preview` LangVersion needed. The libraries page separately calls union scaffolding (`UnionAttribute`, `IUnion`) a preview feature in .NET 11. [libraries](https://learn.microsoft.com/en-us/dotnet/core/whats-new/dotnet-11/libraries)
  - Memory safety rules: **preview**. Needs `LangVersion=preview`; the updated rules additionally need the `updated-memory-safety-rules` feature flag. The first step relaxes `unsafe` for taking addresses, `fixed`, `stackalloc`-to-pointer and `sizeof`, but dereference and indexing still need `unsafe`. Adds `unsafe(expr)` and a `safe` keyword. Treat as unstable.
  - The RC2 C# release note says there are no additional C# language features in RC2 beyond those listed for RC1. [RC2 C# notes](https://github.com/dotnet/core/blob/main/release-notes/11.0/preview/rc2/csharp.md)

## 5. GC settings and NoGC regions

### Stable APIs and settings

- `GC.TryStartNoGCRegion(totalSize[, lohSize][, disallowFullBlockingGC])` / `GC.EndNoGCRegion()`. Calls cannot nest. `totalSize` must cover all allocations in the region, including ones the runtime makes on your behalf. With the single-size overloads the runtime actually attempts to reserve `2 * totalSize` (SOH plus LOH). If memory is not initially available and `disallowFullBlockingGC` is false, the GC performs a full blocking GC to try to free it. Throws `ArgumentOutOfRangeException` if the size exceeds the ephemeral segment size and `InvalidOperationException` if already in a region. [GC.TryStartNoGCRegion](https://learn.microsoft.com/en-us/dotnet/api/system.gc.trystartnogcregion)
- Settings documented for the GC: `System.GC.Server`, `System.GC.Concurrent`, `System.GC.ConserveMemory` (0-9), `System.GC.RetainVM`, `System.GC.HeapHardLimit` and per-heap (SOH/LOH/POH) variants, `System.GC.LOHThreshold` (default 85,000 bytes), `System.GC.HeapCount`, `System.GC.NoAffinitize`, `System.GC.HeapAffinitizeMask/Ranges`. [GC config settings](https://learn.microsoft.com/en-us/dotnet/core/runtime-config/garbage-collector)
- **Regions** replaced segments in .NET 7 for 64-bit Windows and Linux. Default SOH region 4 MB; UOH region is 8x that. `System.GC.RegionRange` and `System.GC.RegionSize` became runtimeconfig.json settings in .NET 10. [GC config settings](https://learn.microsoft.com/en-us/dotnet/core/runtime-config/garbage-collector)
- **DATAS** (Dynamic Adaptation To Application Sizes) is enabled by default since .NET 9 and applies to Server GC (target TCP default 2%, `System.GC.DTargetTCP`). The gen0 growth knobs `DGen0GrowthPercent/MaxFactor/MinFactor` arrived in .NET 10. A simulation with strict frame budgets should decide explicitly whether DATAS is wanted (`System.GC.DynamicAdaptationMode=0` disables it). [GC config settings](https://learn.microsoft.com/en-us/dotnet/core/runtime-config/garbage-collector)

### .NET 11 changes seen in the release notes

- Preview 5: GC trimming and compaction improvements, with `DOTNET_GCLatencyLevel=0` as another way to balance trimming and startup cost (secondary summary from search; the primary note is the Preview 5 runtime.md, which I did not open in full).
- RC1: `GC.GetTotalMemory` no longer returns negative values with regions; `GC.GetTotalAllocatedBytes` stays monotonic with DATAS under Server GC (same caveat).
- RC2: GC now works correctly on Windows machines with multiple CPU groups. The runtime can initialize on more than 1024 CPUs, though the GC keeps a 1024-heap limit. [runtime](https://learn.microsoft.com/en-us/dotnet/core/whats-new/dotnet-11/runtime)
- No new NoGC-region API in .NET 11 appears in the release notes I read.

## 6. Native AOT constraints

Limits listed in the official doc: [Native AOT deployment overview](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/)

- No dynamic loading (`Assembly.LoadFile`) and no runtime codegen (`System.Reflection.Emit`).
- `System.Linq.Expressions` always uses the interpreter.
- Trimming is required, with its incompatibilities, and the output is single-file.
- Generic instantiations over struct type arguments are pre-generated, so binary size can grow; generic virtual methods get an instantiation per override.
- Platform support (.NET 9+ table): Windows x64/Arm64/x86, Linux x64/Arm64/Arm, macOS x64/Arm64; iOS-like platforms supported; Android experimental.
- Libraries should set `IsAotCompatible` (which enables trim, single-file and AOT analyzers). The `IsAotCompatible` assembly metadata was introduced in .NET 10, and `VerifyReferenceAotCompatibility` is opt-in. Needs `net8.0` or later.

.NET 11 changes: NativeAOT uses a shared interface dispatch helper (smaller call sites, faster dispatch), GVM dispatch uses dispatch cells (Preview 7 measured monomorphic 2.45 ns to 1.74 ns), `Assembly.GetCallingAssembly()` now works (but throws with `StackTraceSupport=false`), and Runtime Async supports NativeAOT. [runtime](https://learn.microsoft.com/en-us/dotnet/core/whats-new/dotnet-11/runtime); [Preview 7 runtime notes](https://github.com/dotnet/core/blob/main/release-notes/11.0/preview/preview7/runtime.md)

Framework implication: a Plugin model that loads assemblies at runtime (`Assembly.LoadFrom`, `AssemblyLoadContext`) is incompatible with a Native AOT host. Decide early whether Native AOT is a goal or whether plugins are compiled in. The hardware-intrinsic knobs also do not apply to AOT-compiled code (see section 1).

## 7. Source generators

- Roslyn source generators add code at compile time by inspecting the compilation and additional files; Microsoft points to incremental generators and the source generator cookbook. [Roslyn SDK overview](https://learn.microsoft.com/en-us/dotnet/csharp/roslyn-sdk/source-generators-overview); [incremental generators cookbook](https://github.com/dotnet/roslyn/blob/main/docs/features/incremental-generators.cookbook.md)
- Relevance: generating Component registration, archetype layouts, and query/System dispatch at compile time avoids reflection, which is the main Native AOT and trimming blocker. This is a design inference, not a documented recommendation.
- Unverified: RC2-specific generator API changes. Nothing was found in the .NET 11 notes I read. The C# 15 breaking-changes page was not reviewed.

## 8. Other .NET 11 items worth knowing

- **Runtime Async** is a **preview feature**, opt-in via `<Features>runtime-async=on</Features>`. The runtime libraries themselves are compiled with it. A `net11.0` project no longer needs `EnablePreviewFeatures`. The `DOTNET_RuntimeAsync` env var was removed; opt out with `<UseRuntimeAsync>false</UseRuntimeAsync>`. Async tiering, `ExecutionContext`-free continuations and cheaper `Task.Yield` also landed. [runtime](https://learn.microsoft.com/en-us/dotnet/core/whats-new/dotnet-11/runtime)
  - Relevance: low for a tick-driven simulation core; possible for tooling and I/O.
- **JIT:** generic virtual method devirtualization; `Activator.CreateInstance<T>()` treated as exact type; constant-folded `SequenceEqual`; branch and bounds-check redundancy elimination; faster `Math.BigMul`. [runtime](https://learn.microsoft.com/en-us/dotnet/core/whats-new/dotnet-11/runtime)
- **ReadyToRun:** `Comparer<T>.Default` and `EqualityComparer<T>.Default` are specialized in R2R images (up to 20x faster for collections using default comparers). [runtime](https://learn.microsoft.com/en-us/dotnet/core/whats-new/dotnet-11/runtime)
- **Numerics:** `Random.NextInteger<T>/NextBinaryFloat<T>`, `Decimal32/64/128`, generic `Complex<T>`, `BFloat16` `BitConverter` methods. Decimal and BigInteger conversions are now correctly rounded, a breaking change with no compatibility switch. `Matrix4x4.GetDeterminant` is about 15% faster. [libraries](https://learn.microsoft.com/en-us/dotnet/core/whats-new/dotnet-11/libraries)
- **Experimental in libraries:** `CompositeMLKem` (SYSLIB5006), `Hpke` (SYSLIB5009), TLS session types (SYSLIB5007). Irrelevant to the Framework; noted so they are not adopted by accident.

## 9. Stable versus preview, quick reference

| Item | Status |
| --- | --- |
| Vector128/256/512, Vector<T>, hardware intrinsics, NativeMemory, Span, `allows ref struct`, InlineArray, TryStartNoGCRegion, GC settings, DATAS, regions | Stable (pre-11) |
| Lane ops (`Zip`/`Unzip`/`Concat`/...), AVX-VNNI-512, SVE2 additions, FP16 `Half`, new BCE and devirtualization, raised hardware baseline | New in 11, no flag |
| TensorPrimitives | Stable API; package version tracks 11 RC |
| C# 15: collection expression args, unions, closed hierarchies, extension indexers, labeled break/continue | Default for `net11.0`, but SDK is still prerelease |
| `UnionAttribute`/`IUnion` scaffolding | Preview in .NET 11 (libraries doc) |
| C# 15 memory safety rules, `unsafe(expr)`, `safe` | Preview, opt-in |
| Runtime Async | Preview, opt-in via `Features=runtime-async=on` |
| AVX10.2 | Opt-in (default knob off) |
| MemoryCache metric renames, Decimal transcendental changes | Changed between RC1 and RC2 |

## Implications for Framework design (inference, not claims from sources)

1. Prefer `VectorNNN<T>` with an `IsHardwareAccelerated` ladder over `Vector<T>`, whose width is a process-level choice.
2. If simulation results must be bit-identical across machines (save/load, replays, networking), SIMD width-dependent lane-crossing reductions and the changed float-to-int saturation are determinism hazards. Specify float semantics in the memory-model decision (TASK-1.6).
3. 64-byte aligned column and Field storage via `NativeMemory.AlignedAlloc` aligns with `LoadAligned`/`StoreAligned` use. Wrap in a `SafeHandle`-like owner to avoid leaks.
4. Treat NoGC regions as a guard rail around a Tick, since the size must cover every allocation including runtime-made ones and regions cannot nest. The primary strategy should be zero allocations in the Tick.
5. Decide Native AOT versus runtime Plugin loading before the Plugin design is fixed.
6. Wait for GA before depending on anything in the preview rows above. RC3 notes already exist in dotnet/core.

## Gaps and follow-ups

- Not opened in full: dotnet/core notes for Preview 1 to 6 and RC1, and dotnet/runtime issues. Searching those for GC and NoGC changes would tighten section 5.
- Ref-struct-interfaces proposal page returned 404 at the URL tried.
- `NativeMemory.AlignedAlloc` pairing and alignment-validity rules, `TensorPrimitives` overlap semantics, and Vector512 frequency behavior need confirming on method pages or by benchmark.
- Nothing was run locally. Recommended next step: a small benchmark project on the installed RC2 SDK to confirm `Vector<T>.Count`, `Vector512.IsHardwareAccelerated`, and `TensorPrimitives` speed on the target machines.
