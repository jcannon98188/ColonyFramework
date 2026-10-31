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
COL2001 | Colony.Generator | Error | [System] method is not a static void method of a partial type (spec section 11)
COL2002 | Colony.Generator | Error | [System] parameter cannot be bound (by-value Component, unknown type, mixed shapes) (spec section 4.3)
COL2003 | Colony.Generator | Error | [Component] is not a non-generic partial struct (spec section 11)
COL2004 | Colony.Generator | Error | [Component] field is not unmanaged or is a fixed-size buffer (spec section 2)
COL2005 | Colony.Generator | Error | System body accesses a Component outside its declared access set (spec section 4.3)
COL2006 | Colony.Generator | Error | [Reads]/[Writes]/[With]/[Without]/[ChangeFilter] names a non-struct type (spec section 4.3)
COL2007 | Colony.Generator | Error | [System] Every/Offset rate is invalid (spec section 4.3)
