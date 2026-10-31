namespace Colony.Diagnostics;

/// <summary>
/// How much a World measures per Tick (spec section 10.1). <see cref="Off"/> records only the Tick wall
/// time and the allocation check; <see cref="Standard"/> (the default) adds per-System and per-Phase
/// timing accumulated in per-worker slots at a cost of under about 1% of Tick time; <see cref="Detailed"/>
/// adds a per-job trace (System, worker, chunk, start, end) for investigations.
/// </summary>
public enum DiagnosticsLevel
{
    Off = 0,
    Standard = 1,
    Detailed = 2,
}
