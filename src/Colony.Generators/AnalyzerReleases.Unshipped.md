; Unshipped analyzer release
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
COL1001 | Colony.Determinism | Error | Math/MathF transcendentals in System code (spec section 6)
COL1002 | Colony.Determinism | Error | System.Numerics.Vector<T> in System code (spec section 6)
COL1003 | Colony.Determinism | Error | DateTime/DateTimeOffset/Stopwatch/Environment.TickCount in System code (spec section 6)
COL1004 | Colony.Determinism | Error | Guid.NewGuid in System code (spec section 6)
COL1005 | Colony.Determinism | Error | GetHashCode on a reference type in System code (spec section 6)
COL1006 | Colony.Determinism | Error | Enumeration of an unordered managed collection in System code (spec section 6)
COL1007 | Colony.Determinism | Error | System.Random in System code (spec section 6)
