using System.Runtime.CompilerServices;

namespace Colony.Ecs;

/// <summary>
/// Deferred structural changes recorded by one System job. Buffers are played back at the Phase boundary,
/// in System-ID order then job (chunk) order, so the result is independent of thread count.
/// Byte-encoded; the backing array is reused across Ticks (it only grows during warm-up).
/// </summary>
public sealed unsafe class CommandBuffer
{
    private enum Kind : byte { Destroy = 1, Create = 2, Add = 3, Remove = 4 }

    private byte[] _data = new byte[256];
    private int _len;
    public int Count { get; private set; }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Write<T>(in T v) where T : unmanaged
    {
        int n = sizeof(T);
        if (_len + n > _data.Length) Grow(n);
        Unsafe.WriteUnaligned(ref _data[_len], v);
        _len += n;
    }

    private void Grow(int n) => Array.Resize(ref _data, Math.Max(_data.Length * 2, _len + n));

    private void WriteComp<T>(in T v) where T : unmanaged
    {
        Write((byte)ComponentId<T>.Value);
        Write(v);
    }

    public void Destroy(Entity e)
    {
        Write((byte)Kind.Destroy);
        Write(e.Value);
        Count++;
    }

    public void Create<T1, T2>(in T1 a, in T2 b) where T1 : unmanaged where T2 : unmanaged
    {
        Write((byte)Kind.Create);
        Write(Mask.Of<T1, T2>());
        Write((byte)2);
        WriteComp(a);
        WriteComp(b);
        Count++;
    }

    public void Add<T>(Entity e, in T value) where T : unmanaged
    {
        Write((byte)Kind.Add);
        Write(e.Value);
        WriteComp(value);
        Count++;
    }

    public void Remove<T>(Entity e) where T : unmanaged
    {
        Write((byte)Kind.Remove);
        Write(e.Value);
        Write((byte)ComponentId<T>.Value);
        Count++;
    }

    public void Clear()
    {
        _len = 0;
        Count = 0;
    }

    internal void Playback(World w)
    {
        if (_len == 0) return;
        fixed (byte* basePtr = _data)
        {
            byte* p = basePtr;
            byte* end = basePtr + _len;
            while (p < end)
            {
                var kind = (Kind)(*p++);
                switch (kind)
                {
                    case Kind.Destroy:
                    {
                        var e = new Entity(Unsafe.ReadUnaligned<ulong>(p)); p += 8;
                        if (w.Destroy(e)) w.Structural.Destroys++; else w.Structural.StaleSkipped++;
                        break;
                    }
                    case Kind.Create:
                    {
                        ulong sig = Unsafe.ReadUnaligned<ulong>(p); p += 8;
                        int n = *p++;
                        var e = w.CreateEntity(w.GetOrCreateArchetype(sig));
                        for (int i = 0; i < n; i++)
                        {
                            int t = *p++;
                            w.SetRaw(e, t, p);
                            p += ComponentRegistry.Sizes[t];
                        }
                        w.Structural.Creates++;
                        break;
                    }
                    case Kind.Add:
                    {
                        var e = new Entity(Unsafe.ReadUnaligned<ulong>(p)); p += 8;
                        int t = *p++;
                        if (w.AddComponentRaw(e, t, p)) w.Structural.Adds++; else w.Structural.StaleSkipped++;
                        p += ComponentRegistry.Sizes[t];
                        break;
                    }
                    case Kind.Remove:
                    {
                        var e = new Entity(Unsafe.ReadUnaligned<ulong>(p)); p += 8;
                        int t = *p++;
                        if (w.RemoveComponentRaw(e, t)) w.Structural.Removes++; else w.Structural.StaleSkipped++;
                        break;
                    }
                    default: throw new InvalidOperationException("corrupt command buffer");
                }
            }
        }
        Clear();
    }
}
