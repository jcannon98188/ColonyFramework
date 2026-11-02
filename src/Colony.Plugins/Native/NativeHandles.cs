using System.Collections.Concurrent;

namespace Colony.Plugins.Native;

/// <summary>
/// Opaque handles the C ABI hands out for managed objects (hosts, registrations, schemas, Worlds, command
/// buffers, Native Systems): small non-zero integers, never raw object addresses. One object has one handle for
/// as long as it is registered, so a lookup from a Tick path (a System's command buffer) allocates nothing after
/// the first encounter. Handles root their object until <see cref="Release"/>.
/// <para>
/// The one process-wide table of the Framework (spec section 3 exception): C code holds plain integers that any
/// entry may receive, so the table cannot hang off one World. It holds no simulation state and never decides an
/// outcome. Objects that belong to a World (the World itself, its per-job and host command buffers) are
/// registered with that owner and released when the World is disposed; host, registration and schema handles
/// live until their destroy entry; a Native System's handle lives as long as the process (one per System
/// registered, bounded by the schemas built).
/// </para>
/// </summary>
internal static class NativeHandles
{
    private static readonly ConcurrentDictionary<nint, object> ByHandle = new();
    private static readonly ConcurrentDictionary<object, nint> ByObject = new(ReferenceEqualityComparer.Instance);
    private static readonly Dictionary<World, List<object>> Owned = new(ReferenceEqualityComparer.Instance);
    private static long _next;

    /// <summary>
    /// The handle of <paramref name="target"/>, allocating one on first encounter and tying it to
    /// <paramref name="owner"/>: it is released when the owner World is disposed. Allocation-free once registered.
    /// </summary>
    public static nint GetOrAdd(object target, World owner)
    {
        if (ByObject.TryGetValue(target, out nint existing))
        {
            return existing;
        }

        nint handle = GetOrAdd(target);
        bool hook = false;
        lock (Owned)
        {
            if (!Owned.TryGetValue(owner, out var list))
            {
                list = [];
                Owned.Add(owner, list);
                hook = true;
            }

            if (!list.Contains(target))
            {
                list.Add(target);
            }
        }

        if (hook)
        {
            owner.OnDispose(() => ReleaseOwnedBy(owner));
        }

        return handle;
    }

    /// <summary>The handle of <paramref name="target"/> if it has one.</summary>
    public static bool TryGetHandle(object target, out nint handle) => ByObject.TryGetValue(target, out handle);

    /// <summary>How many live handles <paramref name="owner"/> owns (diagnostics and tests).</summary>
    public static int OwnedCount(World owner)
    {
        lock (Owned)
        {
            return Owned.TryGetValue(owner, out var list) ? list.Count : 0;
        }
    }

    private static void ReleaseOwnedBy(World owner)
    {
        List<object>? list;
        lock (Owned)
        {
            if (!Owned.Remove(owner, out list))
            {
                return;
            }
        }

        foreach (var target in list)
        {
            Release(target);
        }
    }

    /// <summary>The handle of <paramref name="target"/>, allocating one on first encounter.</summary>
    public static nint GetOrAdd(object target)
    {
        if (ByObject.TryGetValue(target, out nint existing))
        {
            return existing;
        }

        nint handle = (nint)Interlocked.Increment(ref _next);
        if (!ByObject.TryAdd(target, handle))
        {
            return ByObject[target];
        }

        ByHandle[handle] = target;
        return handle;
    }

    /// <summary>The object behind <paramref name="handle"/> as <typeparamref name="T"/>; throws <see cref="ArgumentException"/> for an unknown handle or another type.</summary>
    public static T Get<T>(nint handle, string what) where T : class
    {
        if (handle != 0 && ByHandle.TryGetValue(handle, out var target))
        {
            return target as T ?? throw new ArgumentException($"Handle {handle} is a {target.GetType().Name}, not a {what}.");
        }

        throw new ArgumentException($"Handle {handle} is not a live {what}.");
    }

    /// <summary>Forgets the handle; the C side must not use it afterwards.</summary>
    public static void Release(object target)
    {
        if (ByObject.TryRemove(target, out nint handle))
        {
            ByHandle.TryRemove(handle, out _);
        }

        if (target is World world)
        {
            ReleaseOwnedBy(world);
        }
    }
}
