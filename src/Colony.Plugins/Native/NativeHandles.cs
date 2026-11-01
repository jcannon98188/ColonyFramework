using System.Collections.Concurrent;

namespace Colony.Plugins.Native;

/// <summary>
/// Opaque handles the C ABI hands out for managed objects (hosts, registrations, schemas, Worlds, command
/// buffers, Native Systems): small non-zero integers, never raw object addresses. One object has one handle for
/// as long as it is registered, so a lookup from a Tick path (a System's command buffer) allocates nothing after
/// the first encounter. Handles root their object until <see cref="Release"/>.
/// </summary>
internal static class NativeHandles
{
    private static readonly ConcurrentDictionary<nint, object> ByHandle = new();
    private static readonly ConcurrentDictionary<object, nint> ByObject = new(ReferenceEqualityComparer.Instance);
    private static long _next;

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
    }
}
