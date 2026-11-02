using System.Numerics;

namespace Colony.Jobs;

/// <summary>
/// The Begin-Phase resolve System behind <see cref="ClaimSet"/> (spec section 9.2, two-phase assignment).
/// Gather (chunk jobs, parallel over agent chunks): copy each agent's pending proposal into this job's
/// buffer and clear it; detect claims whose target vanished. Resolve (serial End hook): concatenate the
/// buffers in job order, counting-sort by target bucket, resolve every bucket in parallel (sort by target,
/// priority key, claimant id; grant within capacity, deny the rest), then apply the decisions in bucket
/// order through the System's command buffer and events. Every step is keyed by chunk, bucket or Entity,
/// never by thread, so the outcome is identical for any worker count. Scratch arrays grow only.
/// </summary>
internal sealed class ClaimResolver : IIndexedJob
{
    private readonly ClaimSet _set;
    private readonly ClaimPolicy _policy;
    private readonly int _bucketShift;
    private Pending[][] _perJob = [];
    private int[] _perJobCount = [];
    private int[] _perJobLost = [];
    private Pending[] _sorted;
    private int[] _bucketStart = new int[2];
    private int[] _bucketCursor = new int[2];
    private int[] _buckets = new int[16];
    private int _total;
    private int _bucketCount;
    private World? _world;
    private long _tick;

    public ClaimResolver(ClaimSet set, ClaimOptions options)
    {
        _set = set;
        _policy = options.Policy;
        _bucketShift = options.BucketShift;
        _sorted = new Pending[Math.Max(16, options.InitialProposalCapacity)];
    }

    public void Begin(in SystemContext context)
    {
        int jobs = context.JobCount;
        if (_perJob.Length < jobs)
        {
            int old = _perJob.Length;
            Array.Resize(ref _perJob, Math.Max(jobs, old * 2));
            Array.Resize(ref _perJobCount, _perJob.Length);
            Array.Resize(ref _perJobLost, _perJob.Length);
            for (int j = old; j < _perJob.Length; j++)
            {
                _perJob[j] = new Pending[64];
            }
        }
    }

    public void Gather(in SystemContext context, in ChunkView chunk)
    {
        var world = context.World;
        var relation = _set.Relation;
        var entities = chunk.Entities;
        var proposals = chunk.Column<ClaimProposal>(_set.Proposal);
        var grants = chunk.Column<ClaimGrant>(_set.Grant);
        var states = chunk.Column<ClaimAgentState>(_set.Agent);
        var buffer = _perJob[context.JobIndex];
        int count = 0;
        int lost = 0;
        for (int i = 0; i < entities.Length; i++)
        {
            var grant = grants[i];
            if (grant.IsHeld && world.TargetOf(entities[i], relation) != grant.Target)
            {
                // Release on target destruction: Core already dropped the link; clear the mirror and tell the agent.
                _policy.OnLost(ref states[i], grant.Target, context.Tick);
                context.Emit(_set.Lost, new ClaimLost(entities[i], grant.Target));
                grants[i] = default;
                lost++;
            }

            var proposal = proposals[i];
            if (proposal.IsEmpty)
            {
                continue;
            }

            if (count == buffer.Length)
            {
                Array.Resize(ref buffer, buffer.Length * 2);
                _perJob[context.JobIndex] = buffer;
            }

            buffer[count++] = new Pending { Claimant = entities[i], Target = proposal.Target, Priority = proposal.Priority, Amount = proposal.Amount };
            proposals[i] = default;
        }

        _perJobCount[context.JobIndex] = count;
        _perJobLost[context.JobIndex] = lost;
        if (lost > 0)
        {
            context.Count(_set.LostCounter, lost);
        }
    }

    public void Resolve(in SystemContext context)
    {
        var world = context.World;
        int jobs = context.JobCount;
        int total = 0;
        int lost = 0;
        int maxBucket = -1;
        for (int j = 0; j < jobs; j++)
        {
            total += _perJobCount[j];
            lost += _perJobLost[j];
            var buffer = _perJob[j];
            for (int i = 0; i < _perJobCount[j]; i++)
            {
                maxBucket = Math.Max(maxBucket, buffer[i].Target.Index >> _bucketShift);
            }
        }

        _total = total;
        if (total > 0)
        {
            Bucket(total, maxBucket, jobs);
            _world = world;
            _tick = context.Tick;
            world.RunParallel(_bucketCount, this);
            _world = null;
        }

        Apply(in context, lost);
    }

    /// <summary>Counting sort of every job's proposals by target bucket (stable, so job then record order survives inside a bucket).</summary>
    private void Bucket(int total, int maxBucket, int jobs)
    {
        if (_sorted.Length < total)
        {
            Array.Resize(ref _sorted, Math.Max(total, _sorted.Length * 2));
        }

        int bucketSlots = maxBucket + 2;
        if (_bucketStart.Length < bucketSlots)
        {
            Array.Resize(ref _bucketStart, Math.Max(bucketSlots, _bucketStart.Length * 2));
            Array.Resize(ref _bucketCursor, _bucketStart.Length);
        }

        Array.Clear(_bucketStart, 0, bucketSlots);
        for (int j = 0; j < jobs; j++)
        {
            var buffer = _perJob[j];
            for (int i = 0; i < _perJobCount[j]; i++)
            {
                _bucketStart[(buffer[i].Target.Index >> _bucketShift) + 1]++;
            }
        }

        _bucketCount = 0;
        for (int b = 0; b <= maxBucket; b++)
        {
            int n = _bucketStart[b + 1];
            _bucketStart[b + 1] = _bucketStart[b] + n;
            _bucketCursor[b] = _bucketStart[b];
            if (n > 0)
            {
                if (_bucketCount == _buckets.Length)
                {
                    Array.Resize(ref _buckets, _buckets.Length * 2);
                }

                _buckets[_bucketCount++] = b;
            }
        }

        for (int j = 0; j < jobs; j++)
        {
            var buffer = _perJob[j];
            for (int i = 0; i < _perJobCount[j]; i++)
            {
                _sorted[_bucketCursor[buffer[i].Target.Index >> _bucketShift]++] = buffer[i];
            }
        }
    }

    /// <summary>Resolves one bucket: reads the World only, writes its own slice of the decisions.</summary>
    public void Execute(int index)
    {
        var world = _world!;
        int bucket = _buckets[index];
        var slice = _sorted.AsSpan(_bucketStart[bucket], _bucketStart[bucket + 1] - _bucketStart[bucket]);
        slice.Sort(default(PendingComparer));

        var relation = _set.Relation;
        int i = 0;
        while (i < slice.Length)
        {
            var target = slice[i].Target;
            bool alive = world.IsAlive(target);
            var capacity = alive && world.Has(target, _set.Capacity) ? world.Get<ClaimCapacity>(target, _set.Capacity) : ClaimCapacity.OneSlot;
            int slots = Math.Clamp(capacity.Slots, 1, ClaimCapacity.MaxSlots);
            ulong usedMask = 0;
            int held = 0;
            long usedAmount = 0;
            if (alive)
            {
                foreach (var holder in world.SourcesOf(target, relation))
                {
                    held++;
                    if (world.Has(holder, _set.Grant))
                    {
                        var grant = world.Get<ClaimGrant>(holder, _set.Grant);
                        if (grant.Target == target)
                        {
                            usedMask |= 1UL << Math.Clamp(grant.Slot, 0, ClaimCapacity.MaxSlots - 1);
                            usedAmount += grant.Amount;
                        }
                    }
                }
            }

            for (; i < slice.Length && slice[i].Target == target; i++)
            {
                ref var p = ref slice[i];
                if (!alive)
                {
                    p.Reason = ClaimDenialReason.TargetGone;
                }
                else if (!world.TargetOf(p.Claimant, relation).IsNull)
                {
                    p.Reason = ClaimDenialReason.Holding;
                }
                else if (held >= slots)
                {
                    p.Reason = ClaimDenialReason.Capacity;
                }
                else if (capacity.Amount > 0 && usedAmount + p.Amount > capacity.Amount)
                {
                    p.Reason = ClaimDenialReason.Amount;
                }
                else
                {
                    ulong free = ~usedMask & (slots == ClaimCapacity.MaxSlots ? ulong.MaxValue : (1UL << slots) - 1);
                    int slot = free == 0 ? 0 : BitOperations.TrailingZeroCount(free);
                    usedMask |= 1UL << slot;
                    usedAmount += p.Amount;
                    held++;
                    p.Granted = true;
                    p.Slot = slot;
                }
            }
        }
    }

    /// <summary>Applies the decisions in bucket order: Relations and events through the command buffer, Grant and policy state in place.</summary>
    private void Apply(in SystemContext context, int lost)
    {
        var world = context.World;
        long tick = context.Tick;
        int granted = 0;
        int denied = 0;
        for (int i = 0; i < _total; i++)
        {
            ref readonly var p = ref _sorted[i];
            ref var state = ref world.Get<ClaimAgentState>(p.Claimant, _set.Agent);
            if (p.Granted)
            {
                context.Commands.Relate(p.Claimant, _set.Relation, p.Target);
                world.Get<ClaimGrant>(p.Claimant, _set.Grant) = new ClaimGrant(p.Target, p.Slot, p.Amount, tick);
                _policy.OnGranted(ref state, p.Target, tick);
                context.Emit(_set.Granted, new ClaimGranted(p.Claimant, p.Target, p.Slot, p.Amount));
                granted++;
            }
            else
            {
                _policy.OnDenied(ref state, p.Target, tick);
                context.Emit(_set.Denied, new ClaimDenied(p.Claimant, p.Target, p.Reason));
                denied++;
            }
        }

        if (granted > 0)
        {
            context.Count(_set.GrantedCounter, granted);
        }

        if (denied > 0)
        {
            context.Count(_set.DeniedCounter, denied);
        }

        ref var stats = ref context.Resource<ClaimStats>(_set.Stats);
        stats = new ClaimStats(stats.Proposals + _total, stats.Granted + granted, stats.Denied + denied, stats.Lost + lost);
    }

    private struct Pending
    {
        public EntityId Claimant;
        public EntityId Target;
        public long Priority;
        public int Amount;
        public int Slot;
        public ClaimDenialReason Reason;
        public bool Granted;
    }

    /// <summary>
    /// Total order inside a bucket: target slot, then target generation (a slot reused within the round keeps
    /// one contiguous group per Entity, which <see cref="Execute"/> relies on), then priority key (ascending),
    /// then claimant id.
    /// </summary>
    private readonly struct PendingComparer : IComparer<Pending>
    {
        public int Compare(Pending x, Pending y)
        {
            int c = x.Target.Index.CompareTo(y.Target.Index);
            if (c != 0)
            {
                return c;
            }

            c = x.Target.Generation.CompareTo(y.Target.Generation);
            if (c != 0)
            {
                return c;
            }

            c = x.Priority.CompareTo(y.Priority);
            return c != 0 ? c : x.Claimant.Value.CompareTo(y.Claimant.Value);
        }
    }
}
