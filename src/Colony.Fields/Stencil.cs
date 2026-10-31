using System.Runtime.InteropServices;

namespace Colony.Fields;

/// <summary>
/// Stencil Systems over a Field (spec section 5.1). Both modes run the kernel once per chunk on the
/// World's worker pool through <see cref="World.RunParallel"/> from the System's serial hook, with jobs
/// keyed by chunk ordinal, so results are bit-identical for any thread count.
/// <list type="bullet">
/// <item><see cref="DoubleBuffered{T}"/>: every chunk is computed from the previous state into a back buffer,
/// then chunks whose cells actually changed are copied back and stamped; unchanged chunks keep their storage
/// (a uniform chunk stays uniform).</item>
/// <item><see cref="InPlace{T}"/>: ordered mode for cellular automata. Chunks are coloured by coordinate parity
/// (8 colours, no two adjacent chunks alike) and processed colour by colour, in place: a kernel sees its own
/// earlier cells and lower-coloured neighbours already updated, higher-coloured ones not yet. Every visited
/// chunk is stamped as written.</item>
/// </list>
/// Stencil Systems have no Component access set; order them against other Systems with After/Before.
/// </summary>
public static class Stencil
{
    public static SystemDefinition DoubleBuffered<T>(
        string name,
        Field<T> field,
        StencilKernel<T> kernel,
        T boundary = default,
        Phase? phase = null,
        IReadOnlyList<string>? after = null,
        IReadOnlyList<string>? before = null)
        where T : unmanaged
        => Define(name, new StencilRunner<T>(field, kernel, boundary, inPlace: false), phase, after, before);

    public static SystemDefinition InPlace<T>(
        string name,
        Field<T> field,
        StencilKernel<T> kernel,
        T boundary = default,
        Phase? phase = null,
        IReadOnlyList<string>? after = null,
        IReadOnlyList<string>? before = null)
        where T : unmanaged
        => Define(name, new StencilRunner<T>(field, kernel, boundary, inPlace: true), phase, after, before);

    private static SystemDefinition Define<T>(string name, StencilRunner<T> runner, Phase? phase, IReadOnlyList<string>? after, IReadOnlyList<string>? before)
        where T : unmanaged
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return new SystemDefinition
        {
            Name = name,
            Phase = phase ?? Phase.Simulate,
            After = after ?? [],
            Before = before ?? [],
            Begin = (in SystemContext context) => runner.Run(context.World),
        };
    }
}

/// <summary>Per-schema stencil definition; holds one <see cref="State"/> per World it has run in.</summary>
internal sealed class StencilRunner<T>
    where T : unmanaged
{
    private readonly Field<T> _field;
    private readonly StencilKernel<T> _kernel;
    private readonly T _boundary;
    private readonly bool _inPlace;
    private readonly List<State> _states = [];

    public StencilRunner(Field<T> field, StencilKernel<T> kernel, T boundary, bool inPlace)
    {
        ArgumentNullException.ThrowIfNull(kernel);
        _field = field;
        _kernel = kernel;
        _boundary = boundary;
        _inPlace = inPlace;
    }

    public void Run(World world)
    {
        var state = StateFor(world);
        if (_inPlace)
        {
            for (int colour = 0; colour < ChunkLayout.ColourCount; colour++)
            {
                state.Colour = colour;
                world.RunParallel(state.Layout.OrdinalsOfColour(colour).Length, state);
            }

            return;
        }

        state.Pass = Pass.Compute;
        world.RunParallel(state.Layout.ChunkCount, state);
        state.Pass = Pass.Commit;
        world.RunParallel(state.Layout.ChunkCount, state);
    }

    private State StateFor(World world)
    {
        foreach (var state in _states)
        {
            if (ReferenceEquals(state.Fields.World, world))
            {
                return state;
            }
        }

        var fields = world.Fields();
        var created = new State(fields, _field.Id, fields.StorageOf(_field), _inPlace ? null : fields.CreateScratchStorage(_field), _kernel, _boundary);
        _states.Add(created);
        return created;
    }

    private enum Pass
    {
        Compute,
        Commit,
    }

    private sealed class State : IIndexedJob
    {
        private readonly int _fieldId;
        private readonly FieldStorage _front;
        private readonly FieldStorage? _back;
        private readonly byte[] _changed;
        private readonly StencilKernel<T> _kernel;
        private readonly T _boundary;

        public State(FieldSet fields, int fieldId, FieldStorage front, FieldStorage? back, StencilKernel<T> kernel, T boundary)
        {
            Fields = fields;
            Layout = fields.Layout;
            _fieldId = fieldId;
            _front = front;
            _back = back;
            _changed = new byte[Layout.ChunkCount];
            _kernel = kernel;
            _boundary = boundary;
        }

        public FieldSet Fields { get; }

        public ChunkLayout Layout { get; }

        public Pass Pass;

        public int Colour;

        public void Execute(int index)
        {
            if (_back is null)
            {
                ExecuteInPlace(Layout.OrdinalsOfColour(Colour)[index]);
            }
            else if (Pass == Pass.Compute)
            {
                Compute(index);
            }
            else
            {
                Commit(index);
            }
        }

        private void ExecuteInPlace(int ordinal)
        {
            // Materialise first so the input's centre aliases the output.
            var output = _front.WriteCells(ordinal);
            Fields.MarkWritten(_fieldId, ordinal);
            var input = new StencilInput<T>(Layout, _front, Layout.CoordOf(ordinal), _boundary);
            _kernel(in input, MemoryMarshal.Cast<byte, T>(output));
        }

        private void Compute(int ordinal)
        {
            var input = new StencilInput<T>(Layout, _front, Layout.CoordOf(ordinal), _boundary);
            var output = _back!.WriteCells(ordinal);
            _kernel(in input, MemoryMarshal.Cast<byte, T>(output));
            _changed[ordinal] = _front.CellsEqual(ordinal, output) ? (byte)0 : (byte)1;
        }

        private void Commit(int ordinal)
        {
            if (_changed[ordinal] == 0)
            {
                return;
            }

            _changed[ordinal] = 0;
            _back!.ReadCells(ordinal).CopyTo(_front.WriteCells(ordinal));
            Fields.MarkWritten(_fieldId, ordinal);
        }
    }
}
