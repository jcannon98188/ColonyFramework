namespace Colony.Core.Tests;

// Unmanaged test Components. Names are registered explicitly: identity is the registered name plus
// layout hash, never the CLR type (spec section 4.1).
public struct Position
{
    public int X;
    public int Y;
}

public struct Hunger
{
    public float Value;
}

public struct Health
{
    public int Value;
}

public struct Marked
{
    public int Stamp;
}

internal static class TestSchemas
{
    public static WorldSchema ColonistSchema(bool debug = false)
    {
        return new WorldBuilder()
            .AddComponent<Position>("position")
            .AddComponent<Hunger>("hunger")
            .AddComponent<Health>("health")
            .AddComponent<Marked>("marked", ComponentOptions.NonFragmenting)
            .DebugValidation(debug)
            .Build();
    }
}
