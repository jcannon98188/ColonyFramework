namespace Colony.Fields;

/// <summary>
/// Typed handle of a registered Field (spec section 5.1): a named grid of one unmanaged cell struct
/// <typeparamref name="T"/> covering the whole World. Obtained from <see cref="FieldsBuilder.AddField{T}"/>
/// or <see cref="FieldSet.Field{T}"/>; read and written through <see cref="FieldSet.Accessor{T}"/>.
/// </summary>
public readonly struct Field<T> : IEquatable<Field<T>>
    where T : unmanaged
{
    internal Field(int id)
    {
        Id = id;
    }

    /// <summary>Dense registration id, also the Field's position in the change feed and the World hash.</summary>
    public int Id { get; }

    public bool Equals(Field<T> other) => Id == other.Id;

    public override bool Equals(object? obj) => obj is Field<T> other && Equals(other);

    public override int GetHashCode() => Id;

    public override string ToString() => $"Field<{typeof(T).Name}>#{Id}";

    public static bool operator ==(Field<T> left, Field<T> right) => left.Equals(right);

    public static bool operator !=(Field<T> left, Field<T> right) => !left.Equals(right);
}

/// <summary>What the schema knows about a Field: its stable name, cell layout and layout hash.</summary>
public readonly record struct FieldDescriptor(int Id, string Name, int CellSize, int Alignment, ulong LayoutHash);
