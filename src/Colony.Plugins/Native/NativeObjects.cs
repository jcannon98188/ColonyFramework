using System.Runtime.InteropServices;
using System.Text;

namespace Colony.Plugins.Native;

/// <summary>UTF-8 copies of managed strings with stable addresses, owned by one schema or host; freed with it.</summary>
internal sealed class Utf8Strings : IDisposable
{
    private readonly Dictionary<string, nint> _pointers = new(StringComparer.Ordinal);
    private readonly Lock _lock = new();

    public unsafe byte* Pin(string text)
    {
        lock (_lock)
        {
            if (!_pointers.TryGetValue(text, out nint pointer))
            {
                pointer = Marshal.StringToCoTaskMemUTF8(text);
                _pointers.Add(text, pointer);
            }

            return (byte*)pointer;
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            foreach (var pointer in _pointers.Values)
            {
                Marshal.FreeCoTaskMem(pointer);
            }

            _pointers.Clear();
        }
    }

    /// <summary>Reads a NUL-terminated UTF-8 string the C side owns; null yields null.</summary>
    public static unsafe string? Read(byte* text) => text == null ? null : Marshal.PtrToStringUTF8((nint)text);

    public static unsafe string ReadRequired(byte* text, string what)
        => Read(text) ?? throw new ArgumentNullException(what, $"{what} is null.");

    /// <summary>Copies <paramref name="text"/> as UTF-8 into a C buffer (NUL-terminated when it fits); returns the full UTF-8 length.</summary>
    public static unsafe int CopyTo(ReadOnlySpan<char> text, byte* buffer, int capacity)
    {
        int length = Encoding.UTF8.GetByteCount(text);
        if (buffer != null && capacity > 0)
        {
            var target = new Span<byte>(buffer, capacity);
            int written = length < capacity ? Encoding.UTF8.GetBytes(text, target) : Encoding.UTF8.GetBytes(text[..Math.Min(text.Length, capacity - 1)], target[..(capacity - 1)]);
            target[Math.Min(written, capacity - 1)] = 0;
        }

        return length;
    }
}

/// <summary>A <c>colony_host</c>: the <see cref="WorldBuilder"/> and <see cref="PluginHost"/> a C host builds a schema with.</summary>
internal sealed class NativeHost
{
    public WorldBuilder Builder { get; } = new();

    public PluginHost Plugins { get; } = new();
}

/// <summary>A <c>colony_schema</c>: a built <see cref="WorldSchema"/> with its Definition registry and the UTF-8 names handed to C.</summary>
internal sealed class NativeSchema : IDisposable
{
    public NativeSchema(WorldSchema schema, DefinitionRegistry definitions)
    {
        Schema = schema;
        Definitions = definitions;
    }

    public WorldSchema Schema { get; }

    public DefinitionRegistry Definitions { get; }

    public Utf8Strings Strings { get; } = new();

    public void Dispose() => Strings.Dispose();
}

/// <summary>A <c>colony_registration</c>: one Native Plugin's <see cref="PluginRegistration"/> while its <c>register</c> callback runs.</summary>
internal sealed class NativeRegistration
{
    public NativeRegistration(PluginRegistration registration, NativePlugin plugin)
    {
        Registration = registration;
        Plugin = plugin;
    }

    public PluginRegistration Registration { get; }

    public NativePlugin Plugin { get; }
}
