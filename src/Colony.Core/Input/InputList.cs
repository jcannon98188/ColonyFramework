using System.Runtime.CompilerServices;
using Colony.Numerics;

namespace Colony;

/// <summary>
/// One Input Command of type <typeparamref name="T"/> as a handler System sees it (spec section 7.3): the
/// source, the per-source sequence, the command value and the counter-based random stream keyed by
/// (source, sequence); address it by use index (<c>entry.Random.Int32At(useIndex, ...)</c>), never by
/// anything that depends on a thread.
/// </summary>
public readonly struct InputEntry<T> : IEquatable<InputEntry<T>> where T : unmanaged
{
    internal InputEntry(InputSource source, uint sequence, InputCommandType type, in T command, RandomSource random)
    {
        Source = source;
        Sequence = sequence;
        Type = type;
        Command = command;
        Random = random;
    }

    public InputSource Source { get; }

    public uint Sequence { get; }

    public InputCommandType Type { get; }

    public T Command { get; }

    /// <summary>Random stream keyed by (World seed, "input", source, sequence); counter = use index.</summary>
    public RandomSource Random { get; }

    public bool Equals(InputEntry<T> other) => Source == other.Source && Sequence == other.Sequence && Type == other.Type && Random == other.Random && Command.Equals(other.Command);

    public override bool Equals(object? obj) => obj is InputEntry<T> other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Source, Sequence, Type);

    public static bool operator ==(InputEntry<T> left, InputEntry<T> right) => left.Equals(right);

    public static bool operator !=(InputEntry<T> left, InputEntry<T> right) => !left.Equals(right);
}

/// <summary>
/// The commands of one type in this Tick's canonical input list, in canonical order (effective Tick,
/// source, sequence); <c>foreach (var entry in context.Inputs&lt;T&gt;(type))</c>. Allocation free.
/// </summary>
public readonly ref struct InputList<T> where T : unmanaged
{
    private readonly WorldInputs _inputs;
    private readonly int _typeId;
    private readonly RandomSource _root;

    internal InputList(WorldInputs inputs, int typeId, RandomSource root)
    {
        _inputs = inputs;
        _typeId = typeId;
        _root = root;
    }

    /// <summary>Entries of this type in the current canonical list (a linear count).</summary>
    public int Count
    {
        get
        {
            int count = 0;
            var e = GetEnumerator();
            while (e.MoveNext())
            {
                count++;
            }

            return count;
        }
    }

    public Enumerator GetEnumerator() => new(_inputs, _typeId, _root);

    public ref struct Enumerator
    {
        private readonly WorldInputs _inputs;
        private readonly int _typeId;
        private readonly RandomSource _root;
        private int _index;

        internal Enumerator(WorldInputs inputs, int typeId, RandomSource root)
        {
            _inputs = inputs;
            _typeId = typeId;
            _root = root;
            _index = -1;
        }

        public InputEntry<T> Current
        {
            get
            {
                ref readonly var record = ref _inputs.RecordAt(_index);
                return new InputEntry<T>(
                    new InputSource(record.Source),
                    record.Sequence,
                    new InputCommandType(record.Type),
                    Unsafe.ReadUnaligned<T>(in _inputs.PayloadAt(_index)[0]),
                    WorldInputs.RandomFor(_root, record.Source, record.Sequence));
            }
        }

        public bool MoveNext()
        {
            int count = _inputs.CurrentCount;
            while (++_index < count)
            {
                if (_inputs.RecordAt(_index).Type == _typeId)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
