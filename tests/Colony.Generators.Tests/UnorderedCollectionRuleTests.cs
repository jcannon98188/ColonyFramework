namespace Colony.Generators.Tests;

/// <summary>
/// COL1006: enumerating an unordered managed collection (Dictionary, HashSet, ConcurrentDictionary, ConcurrentBag and
/// their key/value views) inside System code. Keyed lookups stay allowed; spec section 6 ("all orderings end with an
/// Entity ID tiebreak").
/// </summary>
public sealed class UnorderedCollectionRuleTests
{
    [Fact]
    public void Enumerating_unordered_collections_is_reported()
    {
        AnalyzerHarness.Verify("""
            using System.Collections.Concurrent;
            using System.Collections.Generic;
            using System.Linq;
            [Colony.System]
            static class Work
            {
                public static int Sum(Dictionary<long, int> byEntity, HashSet<long> ids, ConcurrentDictionary<long, int> shared, ConcurrentBag<int> bag)
                {
                    int total = 0;
                    foreach (var pair in {|COL1006:byEntity|}) { total += pair.Value; }
                    foreach (var id in {|COL1006:ids|}) { total += (int)id; }
                    foreach (var key in {|COL1006:byEntity.Keys|}) { total += (int)key; }
                    foreach (var value in {|COL1006:byEntity.Values|}) { total += value; }
                    foreach (var pair in {|COL1006:shared|}) { total += pair.Value; }
                    foreach (var item in {|COL1006:bag|}) { total += item; }
                    total += {|COL1006:ids|}.First() == 0 ? 1 : 0;
                    total += {|COL1006:byEntity|}.Select(p => p.Value).Sum();
                    var copy = new List<long>({|COL1006:ids|});
                    using var e = {|COL1006:ids.GetEnumerator|}();
                    return total + copy.Count;
                }
            }
            """);
    }

    [Fact]
    public void Keyed_access_and_ordered_collections_are_allowed()
    {
        AnalyzerHarness.Verify("""
            using System.Collections.Generic;
            using System.Linq;
            [Colony.System]
            static class Work
            {
                public static int Sum(Dictionary<long, int> byEntity, HashSet<long> ids, SortedDictionary<long, int> sorted, List<long> ordered, long id)
                {
                    int total = byEntity[id] + byEntity.Count + ids.Count;
                    if (byEntity.TryGetValue(id, out int v) && ids.Contains(id)) { total += v; }
                    byEntity[id] = total;
                    ids.Add(id);
                    foreach (var pair in sorted) { total += pair.Value; }
                    foreach (var x in ordered.OrderBy(x => x)) { total += (int)x; }
                    return total;
                }
            }
            """);
    }

    [Fact]
    public void Enumerating_unordered_collections_outside_System_code_is_allowed()
    {
        AnalyzerHarness.Verify("""
            using System.Collections.Generic;
            using System.Linq;
            static class Host
            {
                public static int Sum(Dictionary<long, int> byEntity, HashSet<long> ids)
                {
                    int total = ids.Sum(i => (int)i);
                    foreach (var pair in byEntity) { total += pair.Value; }
                    return total;
                }
            }
            """);
    }
}
