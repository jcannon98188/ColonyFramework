using Colony.Fields;

namespace Colony.Pathfinding;

/// <summary>
/// Handle of a registered movement class (spec section 9.1, glossary "Movement class"): a Game-defined kind of
/// mover with its own rule for which tiles it can cross. Ids are registration order within the schema.
/// </summary>
public readonly record struct MovementClass(int Id)
{
    public override string ToString() => $"MovementClass({Id})";
}

/// <summary>
/// The passability rule of a movement class over one Field chunk: sets bit <c>FieldChunk.CellIndex(x, y, z)</c>
/// of <paramref name="passable"/> (64 words, 4096 bits, cleared on entry) for every tile of the chunk the class
/// can stand on. It runs on worker threads, keyed by chunk, so it must read Fields only and allocate nothing.
/// Tiles outside the World are masked out afterwards whatever the rule says.
/// </summary>
public delegate void PassabilityRule(in PassabilityInput input, Span<ulong> passable);

/// <summary>What a <see cref="PassabilityRule"/> sees: the World's Fields and the chunk being labelled.</summary>
public readonly ref struct PassabilityInput
{
    internal PassabilityInput(FieldSet fields, int ordinal, ChunkCoord chunk)
    {
        Fields = fields;
        Ordinal = ordinal;
        Chunk = chunk;
    }

    public FieldSet Fields { get; }

    /// <summary>The chunk's ordinal (Morton rank), as the Field accessors take it.</summary>
    public int Ordinal { get; }

    public ChunkCoord Chunk { get; }

    /// <summary>The tile at the chunk's minimum corner.</summary>
    public TilePos Origin => Chunk.Origin;
}

/// <summary>Ready-made passability rules.</summary>
public static class Passability
{
    /// <summary>
    /// Passable where <paramref name="passable"/> holds for the cell of <paramref name="field"/>; a uniform chunk
    /// is evaluated once. The movement class should <see cref="MovementClassDefinition.DependsOn{T}"/> the Field.
    /// </summary>
    public static PassabilityRule OfField<T>(Field<T> field, Func<T, bool> passable)
        where T : unmanaged
    {
        ArgumentNullException.ThrowIfNull(passable);
        return (in PassabilityInput input, Span<ulong> bits) =>
        {
            var accessor = input.Fields.Accessor(field);
            if (accessor.IsUniform(input.Ordinal))
            {
                if (passable(accessor.UniformValue(input.Ordinal)))
                {
                    bits.Fill(ulong.MaxValue);
                }

                return;
            }

            var cells = accessor.ReadChunk(input.Ordinal);
            for (int i = 0; i < cells.Length; i++)
            {
                if (passable(cells[i]))
                {
                    bits[i >> 6] |= 1UL << (i & 63);
                }
            }
        };
    }
}

/// <summary>
/// The Definition of a movement class: a stable name, its <see cref="PassabilityRule"/> and the Fields the rule
/// reads (<see cref="DependsOn{T}"/>), so only changes to those Fields trigger region upkeep; a class that
/// names no Field is refreshed on any Field change. Register it with <see cref="PathfindingBuilder.AddMovementClass"/>.
/// </summary>
public sealed class MovementClassDefinition
{
    private readonly List<int> _fields = [];

    public MovementClassDefinition(string name, PassabilityRule rule)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(rule);
        Name = name;
        Rule = rule;
    }

    public string Name { get; }

    public PassabilityRule Rule { get; }

    /// <summary>
    /// Optional vertical rule: sets the bit of every passable tile from which the class can move to the tile
    /// directly above (z + 1) and back down; the upper tile must be passable too. It should look at the tile
    /// itself only (stairs, ladders). <c>null</c> (the default) connects any two vertically adjacent passable
    /// tiles, so the class moves on the plain six-neighbour grid. Horizontal moves are always the four face
    /// neighbours in the plane; there are no diagonal moves.
    /// </summary>
    public PassabilityRule? Vertical { get; init; }

    /// <summary>Field ids the rule reads, in declaration order; empty means every Field.</summary>
    public IReadOnlyList<int> FieldIds => _fields;

    /// <summary>Declares a Field the rule reads.</summary>
    public MovementClassDefinition DependsOn<T>(Field<T> field)
        where T : unmanaged
    {
        if (!_fields.Contains(field.Id))
        {
            _fields.Add(field.Id);
        }

        return this;
    }
}

/// <summary>A registered movement class as the schema describes it.</summary>
public readonly record struct MovementClassDescriptor(MovementClass Class, string Name, ulong LayoutHash);

/// <summary>
/// A region id (spec section 9.1): every tile a movement class can stand on belongs to exactly one region, and two
/// tiles are mutually reachable iff their regions are equal. Ids are canonical (the smallest <c>(chunk ordinal &lt;&lt; 12) | cellIndex</c>
/// over its tiles, see <see cref="RegionMap"/>), so an incremental update and a from-scratch rebuild agree exactly.
/// </summary>
public readonly record struct RegionId(long Value)
{
    /// <summary>No region: the tile is impassable for the class or lies outside the World.</summary>
    public static RegionId None => new(-1);

    public bool IsNone => Value < 0;

    public override string ToString() => IsNone ? "Region(none)" : $"Region({Value})";
}
