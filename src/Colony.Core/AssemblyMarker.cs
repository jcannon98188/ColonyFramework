namespace Colony;

/// <summary>
/// Anchor type for locating the <c>Colony.Core</c> assembly without reflection over names.
/// Internal on purpose: it is not public API (spec section 11), only tests reach it via <c>InternalsVisibleTo</c>.
/// </summary>
internal static class AssemblyMarker
{
}
