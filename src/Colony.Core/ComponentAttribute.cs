namespace Colony;

/// <summary>
/// Marks a partial unmanaged struct as a Component (spec sections 4.1 and 11). The source generator emits
/// <see cref="IComponent{TSelf}"/> with a field-level layout, plus equality members, so the struct registers
/// with <see cref="WorldBuilder.AddComponent{T}()"/> under a stable name and layout hash.
/// </summary>
[AttributeUsage(AttributeTargets.Struct, Inherited = false, AllowMultiple = false)]
public sealed class ComponentAttribute : Attribute
{
    public ComponentAttribute()
    {
    }

    /// <param name="name">The stable registered name; defaults to the struct's simple name.</param>
    public ComponentAttribute(string name)
    {
        Name = name;
    }

    /// <summary>The stable registered name (spec section 4.1); defaults to the struct's simple name.</summary>
    public string? Name { get; }

    /// <summary>Store the Component in the non-fragmenting World column instead of the archetype.</summary>
    public bool NonFragmenting { get; set; }
}

/// <summary>Declares Components a System reads beyond its <c>in</c> parameters (spec section 4.3).</summary>
[AttributeUsage(AttributeTargets.Method, Inherited = false, AllowMultiple = false)]
public sealed class ReadsAttribute : Attribute
{
    public ReadsAttribute(params Type[] components)
    {
        Components = components;
    }

    public Type[] Components { get; }
}

/// <summary>Declares Components a System writes beyond its <c>ref</c> parameters (spec section 4.3).</summary>
[AttributeUsage(AttributeTargets.Method, Inherited = false, AllowMultiple = false)]
public sealed class WritesAttribute : Attribute
{
    public WritesAttribute(params Type[] components)
    {
        Components = components;
    }

    public Type[] Components { get; }
}

/// <summary>Components a chunk must have for the System to visit it, beyond those it reads or writes (<see cref="SystemDefinition.All"/>).</summary>
[AttributeUsage(AttributeTargets.Method, Inherited = false, AllowMultiple = false)]
public sealed class WithAttribute : Attribute
{
    public WithAttribute(params Type[] components)
    {
        Components = components;
    }

    public Type[] Components { get; }
}

/// <summary>Components a chunk must not have for the System to visit it (<see cref="SystemDefinition.None"/>).</summary>
[AttributeUsage(AttributeTargets.Method, Inherited = false, AllowMultiple = false)]
public sealed class WithoutAttribute : Attribute
{
    public WithoutAttribute(params Type[] components)
    {
        Components = components;
    }

    public Type[] Components { get; }
}

/// <summary>Visit a chunk only when one of these Components changed since the System last ran (<see cref="SystemDefinition.ChangeFilter"/>).</summary>
[AttributeUsage(AttributeTargets.Method, Inherited = false, AllowMultiple = false)]
public sealed class ChangeFilterAttribute : Attribute
{
    public ChangeFilterAttribute(params Type[] components)
    {
        Components = components;
    }

    public Type[] Components { get; }
}
