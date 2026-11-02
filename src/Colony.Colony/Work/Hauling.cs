using Colony.Fields;

namespace Colony.Jobs;

/// <summary>A stock of <see cref="Item"/> that may be hauled away: the holding Entity, where it stands and how much is free.</summary>
public readonly record struct HaulSupply(EntityId Entity, TilePos Position, int Item, int Amount);

/// <summary>A request for <see cref="Item"/>: the requesting Entity, where it wants delivery, how much and how urgently (higher <see cref="Priority"/> first).</summary>
public readonly record struct HaulDemand(EntityId Entity, TilePos Position, int Item, int Amount, long Priority);

/// <summary>
/// One haul: carry <see cref="Amount"/> of <see cref="Item"/> from <see cref="Supply"/> (at <see cref="From"/>) to
/// <see cref="Demand"/> (at <see cref="To"/>). Also the saved Component of a haul work item
/// (<see cref="HaulingRegistration.AddHauling"/>, <see cref="HaulingMatcher.CreateWorkItem"/>).
/// </summary>
public readonly record struct HaulMatch(EntityId Supply, EntityId Demand, TilePos From, TilePos To, int Item, int Amount);

/// <summary>
/// A filter over (supply, demand) pairs the matcher may form, as a struct so matching allocates nothing. The
/// reachability filter of spec section 9.2 ships as <see cref="ReachableHaulFilter"/>; write your own struct to
/// combine it with Game rules (hauler capacity, zones).
/// </summary>
public interface IHaulFilter
{
    public bool CanHaul(in HaulSupply supply, in HaulDemand demand);
}

/// <summary>The filter that allows every pair.</summary>
public readonly struct AnyHaul : IHaulFilter, IEquatable<AnyHaul>
{
    public bool CanHaul(in HaulSupply supply, in HaulDemand demand) => true;

    public bool Equals(AnyHaul other) => true;

    public override bool Equals(object? obj) => obj is AnyHaul;

    public override int GetHashCode() => 0;

    public static bool operator ==(AnyHaul left, AnyHaul right) => true;

    public static bool operator !=(AnyHaul left, AnyHaul right) => false;
}

/// <summary>
/// The optional hauling matcher of spec section 9.2: pairs supply with demand into hauls, deterministically and
/// without allocating. Demands are served by priority (higher first, ties by Entity id); each takes from the
/// nearest supply of its item (ties by Entity id), splitting across supplies until it is met or none is left.
/// The Game gathers supplies and demands from its own Components into the spans (from a serial hook, or per job
/// and concatenated in job order) and turns the matches into work items with <see cref="CreateWorkItem"/>.
/// </summary>
public static class HaulingMatcher
{
    private static readonly Comparison<HaulDemand> DemandOrder = static (a, b) =>
        a.Priority != b.Priority ? b.Priority.CompareTo(a.Priority) : a.Entity.Value.CompareTo(b.Entity.Value);

    /// <summary>
    /// Matches every pair. Both input spans are scratch: demands are sorted in place and supply amounts are
    /// decremented as they are taken, so what remains afterwards is the unmet demand and the untaken supply.
    /// Returns the number of matches written, stopping when <paramref name="matches"/> is full.
    /// </summary>
    public static int Match(Span<HaulSupply> supplies, Span<HaulDemand> demands, Span<HaulMatch> matches)
        => Match(supplies, demands, matches, default(AnyHaul));

    /// <summary>Matches only the pairs <paramref name="filter"/> allows (reachability, hauler capacity, zones). See <see cref="Match(Span{HaulSupply}, Span{HaulDemand}, Span{HaulMatch})"/>.</summary>
    public static int Match<TFilter>(Span<HaulSupply> supplies, Span<HaulDemand> demands, Span<HaulMatch> matches, TFilter filter)
        where TFilter : struct, IHaulFilter
    {
        demands.Sort(DemandOrder);
        int written = 0;
        for (int d = 0; d < demands.Length && written < matches.Length; d++)
        {
            ref readonly var demand = ref demands[d];
            int need = demand.Amount;
            while (need > 0 && written < matches.Length)
            {
                int best = Nearest(supplies, in demand, filter);
                if (best < 0)
                {
                    break;
                }

                ref var supply = ref supplies[best];
                int take = Math.Min(need, supply.Amount);
                matches[written++] = new HaulMatch(supply.Entity, demand.Entity, supply.Position, demand.Position, demand.Item, take);
                supply = supply with { Amount = supply.Amount - take };
                need -= take;
            }
        }

        return written;
    }

    /// <summary>The nearest supply of the demand's item with stock left that the filter allows; ties by Entity id; -1 when none.</summary>
    private static int Nearest<TFilter>(ReadOnlySpan<HaulSupply> supplies, in HaulDemand demand, TFilter filter)
        where TFilter : struct, IHaulFilter
    {
        int best = -1;
        long bestDistance = long.MaxValue;
        for (int i = 0; i < supplies.Length; i++)
        {
            ref readonly var supply = ref supplies[i];
            if (supply.Item != demand.Item || supply.Amount <= 0)
            {
                continue;
            }

            long distance = SpatialIndex.DistanceSquared(supply.Position, demand.Position);
            if (distance > bestDistance || (distance == bestDistance && supply.Entity.Value >= supplies[best].Entity.Value))
            {
                continue;
            }

            if (filter.CanHaul(in supply, in demand))
            {
                best = i;
                bestDistance = distance;
            }
        }

        return best;
    }

    /// <summary>
    /// Records the creation of a haul work item: an Entity with <see cref="WorkIndexBuilder.ItemComponents"/>
    /// (standing at the supply, available, of <paramref name="haulType"/>) plus the <see cref="HaulMatch"/>
    /// Component <paramref name="haulJob"/> from <see cref="HaulingRegistration.AddHauling"/>. Returns the deferred
    /// id; the item is indexed at the next Late Phase.
    /// </summary>
    public static EntityId CreateWorkItem(CommandBuffer commands, in HaulMatch match, WorkIndexBuilder index, ComponentType haulJob, int haulType)
    {
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentNullException.ThrowIfNull(index);
        var id = commands.Create(index.ItemComponents.With(haulJob));
        commands.Add(id, index.TilePos, match.From);
        commands.Add(id, index.WorkItem, WorkItem.Available(haulType));
        commands.Add(id, haulJob, in match);
        return id;
    }
}

/// <summary>Registration of the haul work item Component.</summary>
public static class HaulingRegistration
{
    public const string HaulJobName = "colony.haul.job";

    /// <summary>Registers <see cref="HaulMatch"/> as the saved Component of haul work items; returns its type (also <see cref="Hauling(WorldSchema)"/>).</summary>
    public static ComponentType AddHauling(this WorldBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.AddComponent<HaulMatch>(HaulJobName);
        return builder.Component<HaulMatch>();
    }

    /// <summary>The <see cref="HaulMatch"/> Component type of a schema that called <see cref="AddHauling"/>.</summary>
    public static ComponentType Hauling(this WorldSchema schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        return schema.Component<HaulMatch>();
    }
}
