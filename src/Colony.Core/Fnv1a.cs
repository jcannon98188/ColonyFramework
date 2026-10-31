namespace Colony;

/// <summary>
/// Minimal FNV-1a (64-bit) used for layout identity and mask hashing until the Framework hash layer
/// (TASK-4) lands. Internal on purpose; it is not the World hash (spec section 6).
/// </summary>
internal static class Fnv1a
{
    public const ulong Offset = 14695981039346656037UL;
    private const ulong Prime = 1099511628211UL;

    public static ulong Hash(ReadOnlySpan<byte> bytes, ulong seed = Offset)
    {
        ulong h = seed;
        foreach (byte b in bytes)
        {
            h = (h ^ b) * Prime;
        }

        return h;
    }

    public static ulong Hash(ulong value, ulong seed = Offset)
    {
        ulong h = seed;
        for (int i = 0; i < 8; i++)
        {
            h = (h ^ (byte)(value >> (i * 8))) * Prime;
        }

        return h;
    }

    public static ulong Hash(string text, ulong seed = Offset)
    {
        ulong h = seed;
        foreach (char c in text)
        {
            h = (h ^ (byte)c) * Prime;
            h = (h ^ (byte)(c >> 8)) * Prime;
        }

        return h;
    }
}
