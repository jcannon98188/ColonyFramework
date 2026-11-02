using System.Runtime.InteropServices;

namespace Colony.Fields;

/// <summary>
/// Stencil Systems over a Field (spec section 5.1). Both modes run the kernel once per chunk on the
/// World's worker pool through <see cref="World.RunParallel"/> from the System's serial hook, with jobs
/// keyed by chunk ordinal, so results are bit-identical for any thread count.
/// <list type="bullet">
/// <item><c>DoubleBuffered</c>: every chunk is computed from the previous state into a back buffer,
/// then chunks whose cells actually changed are copied back and stamped; unchanged chunks keep their storage
/// (a uniform chunk stays uniform).</item>
/// <item><c>InPlace</c>: ordered mode for cellular automata. Chunks are coloured by coordinate parity
/// (8 colours, no two adjacent chunks alike) and processed colour by colour, in place: a kernel sees its own
/// earlier cells and lower-coloured neighbours already updated, higher-coloured ones not yet. Every visited
/// chunk is stamped as written.</item>
/// </list>
/// Each mode takes either kernel shape: <see cref="StencilKernel{T}"/> reads cell by cell anywhere in the 3x3x3 chunk
/// neighbourhood (<see cref="StencilInput{T}.Read"/>); <see cref="StencilHaloKernel{T}"/> walks rows of the gathered
/// one-cell halo (<see cref="StencilHalo{T}"/>) and is the fast path for 6- and 26-neighbour kernels.
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
        => Define(name, new StencilRunner<T>(field, kernel, null, boundary, inPlace: false), phase, after, before);

    /// <summary>A double-buffered stencil whose kernel walks halo rows (<see cref="StencilHalo{T}"/>) instead of reading cell by cell.</summary>
    public static SystemDefinition DoubleBuffered<T>(
        string name,
        Field<T> field,
        StencilHaloKernel<T> kernel,
        T boundary = default,
        Phase? phase = null,
        IReadOnlyList<string>? after = null,
        IReadOnlyList<string>? before = null)
        where T : unmanaged
        => Define(name, new StencilRunner<T>(field, null, kernel, boundary, inPlace: false), phase, after, before);

    public static SystemDefinition InPlace<T>(
        string name,
        Field<T> field,
        StencilKernel<T> kernel,
        T boundary = default,
        Phase? phase = null,
        IReadOnlyList<string>? after = null,
        IReadOnlyList<string>? before = null)
        where T : unmanaged
        => Define(name, new StencilRunner<T>(field, kernel, null, boundary, inPlace: true), phase, after, before);

    /// <summary>An in-place ordered stencil whose kernel walks halo rows (<see cref="StencilHalo{T}"/>); output rows alias the halo, so the kernel sees its own earlier writes.</summary>
    public static SystemDefinition InPlace<T>(
        string name,
        Field<T> field,
        StencilHaloKernel<T> kernel,
        T boundary = default,
        Phase? phase = null,
        IReadOnlyList<string>? after = null,
        IReadOnlyList<string>? before = null)
        where T : unmanaged
        => Define(name, new StencilRunner<T>(field, null, kernel, boundary, inPlace: true), phase, after, before);

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
    private readonly StencilKernel<T>? _kernel;
    private readonly StencilHaloKernel<T>? _haloKernel;
    private readonly T _boundary;
    private readonly bool _inPlace;
    private readonly List<State> _states = [];

    public StencilRunner(Field<T> field, StencilKernel<T>? kernel, StencilHaloKernel<T>? haloKernel, T boundary, bool inPlace)
    {
        if (kernel is null && haloKernel is null)
        {
            throw new ArgumentNullException(nameof(kernel));
        }

        _field = field;
        _kernel = kernel;
        _haloKernel = haloKernel;
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
        var created = new State(fields, _field.Id, fields.StorageOf(_field), _inPlace ? null : fields.CreateScratchStorage(_field), _kernel, _haloKernel, _boundary);
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
        private readonly StencilKernel<T>? _kernel;
        private readonly StencilHaloKernel<T>? _haloKernel;
        private readonly T _boundary;

        public State(FieldSet fields, int fieldId, FieldStorage front, FieldStorage? back, StencilKernel<T>? kernel, StencilHaloKernel<T>? haloKernel, T boundary)
        {
            Fields = fields;
            Layout = fields.Layout;
            _fieldId = fieldId;
            _front = front;
            _back = back;
            _changed = new byte[Layout.ChunkCount];
            _kernel = kernel;
            _haloKernel = haloKernel;
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
            // Only Active chunks compute (spec section 5.3); a Hibernating neighbour is read as a fixed boundary.
            if (Fields.ResidencyOf(ordinal) != ChunkResidency.Active)
            {
                return;
            }

            var coord = Layout.CoordOf(ordinal);
            if (_haloKernel is not null)
            {
                // The halo is the working buffer: gather (centre included) first, run, then copy the centre back.
                var gathered = new StencilInput<T>(Layout, _front, coord, _boundary, aliasCentre: false);
                var cells = MemoryMarshal.Cast<byte, T>(_front.WriteCells(ordinal));
                Fields.MarkWritten(_fieldId, ordinal);
                RunHalo(in gathered, coord, cells, inPlace: true);
                return;
            }

            // Materialise first so the input's centre aliases the output.
            var output = _front.WriteCells(ordinal);
            Fields.MarkWritten(_fieldId, ordinal);
            var input = new StencilInput<T>(Layout, _front, coord, _boundary, aliasCentre: true);
            _kernel!(in input, MemoryMarshal.Cast<byte, T>(output));
        }

        private void Compute(int ordinal)
        {
            if (Fields.ResidencyOf(ordinal) != ChunkResidency.Active)
            {
                _changed[ordinal] = 0;
                return;
            }

            var coord = Layout.CoordOf(ordinal);
            var input = new StencilInput<T>(Layout, _front, coord, _boundary, aliasCentre: false);
            var output = _back!.WriteCells(ordinal);
            if (_haloKernel is not null)
            {
                RunHalo(in input, coord, MemoryMarshal.Cast<byte, T>(output), inPlace: false);
            }
            else
            {
                _kernel!(in input, MemoryMarshal.Cast<byte, T>(output));
            }

            _changed[ordinal] = _front.CellsEqual(ordinal, output) ? (byte)0 : (byte)1;
        }

        /// <summary>Runs the halo kernel over a gathered input; <paramref name="cells"/> is the chunk's storage-order output.</summary>
        private unsafe void RunHalo(in StencilInput<T> input, ChunkCoord coord, Span<T> cells, bool inPlace)
        {
            var size = Layout.Size;
            var origin = coord.Origin;
            int extentX = Math.Min(FieldChunk.Size, size.X - origin.X);
            int extentY = Math.Min(FieldChunk.Size, size.Y - origin.Y);
            int extentZ = Math.Min(FieldChunk.Size, size.Z - origin.Z);
            T* halo = input.HaloPointer;
            fixed (T* output = cells)
            {
                var view = new StencilHalo<T>(halo, inPlace ? null : output, coord, _boundary, extentX, extentY, extentZ);
                _haloKernel!(in view);
            }

            if (inPlace)
            {
                for (int z = 0; z < FieldChunk.Size; z++)
                {
                    for (int y = 0; y < FieldChunk.Size; y++)
                    {
                        new ReadOnlySpan<T>(halo + StencilHalo.IndexOf(0, y, z), FieldChunk.Size)
                            .CopyTo(cells.Slice(FieldChunk.CellIndex(0, y, z), FieldChunk.Size));
                    }
                }
            }

            if ((extentX & extentY & extentZ) != FieldChunk.Size)
            {
                // Storage-only cells past the World bound always hold the boundary after a halo stencil.
                for (int i = 0; i < FieldChunk.CellCount; i++)
                {
                    var (x, y, z) = FieldChunk.CellCoordinates(i);
                    if (x >= extentX || y >= extentY || z >= extentZ)
                    {
                        cells[i] = _boundary;
                    }
                }
            }
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