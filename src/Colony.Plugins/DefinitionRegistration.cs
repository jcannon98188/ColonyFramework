using System.Runtime.CompilerServices;
using Colony.Numerics;

namespace Colony.Plugins;

/// <summary>Edits one Definition value in place (<see cref="DefinitionRegistration.Patch{T}"/>).</summary>
public delegate void DefinitionPatch<T>(ref T value)
    where T : unmanaged;

/// <summary>Returns a reference to one field of a Definition, so sources can fill it without reflection.</summary>
public delegate ref TField DefinitionFieldAccessor<T, TField>(ref T definition)
    where T : unmanaged
    where TField : unmanaged;

/// <summary>
/// How a Definition struct's fields are named for sources (spec section 8). Each <see cref="Field{TField}"/>
/// binds a source field name to a struct field through an accessor delegate, so filling a Definition from a
/// document is plain code: no reflection, Native AOT safe. Supported field types: the integer primitives,
/// <see cref="bool"/>, <see cref="float"/>, <see cref="double"/>, <see cref="Fixed32"/>, <see cref="Fixed64"/>
/// and <see cref="StringId"/> (text, interned in the registry).
/// </summary>
public sealed class DefinitionLayout<T>
    where T : unmanaged
{
    private delegate void Setter(ref T target, DefinitionValue value, string context, string pluginId);

    private readonly Dictionary<string, Setter> _setters = new(StringComparer.Ordinal);
    private readonly DefinitionRegistry _registry;

    internal DefinitionLayout(DefinitionRegistry registry)
    {
        _registry = registry;
    }

    public int FieldCount => _setters.Count;

    public DefinitionLayout<T> Field<TField>(string name, DefinitionFieldAccessor<T, TField> accessor)
        where TField : unmanaged
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(accessor);
        if (_setters.ContainsKey(name))
        {
            throw new ArgumentException($"Definition field '{name}' is already declared.", nameof(name));
        }

        _setters.Add(name, MakeSetter(name, accessor));
        return this;
    }

    internal void Set(ref T target, DefinitionField field, string context, string pluginId)
    {
        if (!_setters.TryGetValue(field.Name, out var setter))
        {
            throw new PluginException(pluginId, $"{context} sets field '{field.Name}', which the Definition type does not declare.");
        }

        setter(ref target, field.Value, context, pluginId);
    }

    private Setter MakeSetter<TField>(string name, DefinitionFieldAccessor<T, TField> accessor)
        where TField : unmanaged
    {
        if (typeof(TField) == typeof(int))
        {
            return (ref T t, DefinitionValue v, string c, string p) => Unsafe.As<TField, int>(ref accessor(ref t)) = checked((int)Integer(v, name, c, p));
        }

        if (typeof(TField) == typeof(long))
        {
            return (ref T t, DefinitionValue v, string c, string p) => Unsafe.As<TField, long>(ref accessor(ref t)) = Integer(v, name, c, p);
        }

        if (typeof(TField) == typeof(short))
        {
            return (ref T t, DefinitionValue v, string c, string p) => Unsafe.As<TField, short>(ref accessor(ref t)) = checked((short)Integer(v, name, c, p));
        }

        if (typeof(TField) == typeof(byte))
        {
            return (ref T t, DefinitionValue v, string c, string p) => Unsafe.As<TField, byte>(ref accessor(ref t)) = checked((byte)Integer(v, name, c, p));
        }

        if (typeof(TField) == typeof(sbyte))
        {
            return (ref T t, DefinitionValue v, string c, string p) => Unsafe.As<TField, sbyte>(ref accessor(ref t)) = checked((sbyte)Integer(v, name, c, p));
        }

        if (typeof(TField) == typeof(uint))
        {
            return (ref T t, DefinitionValue v, string c, string p) => Unsafe.As<TField, uint>(ref accessor(ref t)) = checked((uint)Integer(v, name, c, p));
        }

        if (typeof(TField) == typeof(ulong))
        {
            return (ref T t, DefinitionValue v, string c, string p) => Unsafe.As<TField, ulong>(ref accessor(ref t)) = checked((ulong)Integer(v, name, c, p));
        }

        if (typeof(TField) == typeof(ushort))
        {
            return (ref T t, DefinitionValue v, string c, string p) => Unsafe.As<TField, ushort>(ref accessor(ref t)) = checked((ushort)Integer(v, name, c, p));
        }

        if (typeof(TField) == typeof(bool))
        {
            return (ref T t, DefinitionValue v, string c, string p) =>
            {
                if (v.Kind != DefinitionValueKind.Boolean)
                {
                    throw Mismatch(name, "a boolean", v, c, p);
                }

                Unsafe.As<TField, bool>(ref accessor(ref t)) = v.Whole != 0;
            };
        }

        if (typeof(TField) == typeof(float))
        {
            return (ref T t, DefinitionValue v, string c, string p) => Unsafe.As<TField, float>(ref accessor(ref t)) = (float)Number(v, name, c, p);
        }

        if (typeof(TField) == typeof(double))
        {
            return (ref T t, DefinitionValue v, string c, string p) => Unsafe.As<TField, double>(ref accessor(ref t)) = Number(v, name, c, p);
        }

        if (typeof(TField) == typeof(Fixed32))
        {
            return (ref T t, DefinitionValue v, string c, string p) => Unsafe.As<TField, Fixed32>(ref accessor(ref t)) = Fixed32.FromDouble(Number(v, name, c, p));
        }

        if (typeof(TField) == typeof(Fixed64))
        {
            return (ref T t, DefinitionValue v, string c, string p) => Unsafe.As<TField, Fixed64>(ref accessor(ref t)) = Fixed64.FromDouble(Number(v, name, c, p));
        }

        if (typeof(TField) == typeof(StringId))
        {
            var registry = _registry;
            return (ref T t, DefinitionValue v, string c, string p) =>
            {
                if (v.Kind == DefinitionValueKind.Null)
                {
                    Unsafe.As<TField, StringId>(ref accessor(ref t)) = StringId.Null;
                    return;
                }

                if (v.Kind != DefinitionValueKind.Text)
                {
                    throw Mismatch(name, "text", v, c, p);
                }

                Unsafe.As<TField, StringId>(ref accessor(ref t)) = registry.Strings.Intern(v.Text!);
            };
        }

        throw new ArgumentException($"Definition field '{name}' has type {typeof(TField).Name}, which no source can fill; use an integer, bool, float, double, Fixed32, Fixed64 or StringId field.", nameof(accessor));
    }

    private static long Integer(DefinitionValue value, string name, string context, string pluginId)
    {
        if (value.Kind == DefinitionValueKind.Whole)
        {
            return value.Whole;
        }

        if (value.Kind == DefinitionValueKind.Number && Math.Floor(value.Number) == value.Number && Math.Abs(value.Number) < 9007199254740992.0)
        {
            return (long)value.Number;
        }

        throw Mismatch(name, "an integer", value, context, pluginId);
    }

    private static double Number(DefinitionValue value, string name, string context, string pluginId)
        => value.Kind is DefinitionValueKind.Whole or DefinitionValueKind.Number
            ? value.Number
            : throw Mismatch(name, "a number", value, context, pluginId);

    private static PluginException Mismatch(string name, string expected, DefinitionValue value, string context, string pluginId)
        => new(pluginId, $"{context} sets field '{name}' to {value.Kind.ToString().ToUpperInvariant()} '{value}'; it takes {expected}.");
}

/// <summary>
/// The Definition part of a Plugin's registration (spec section 8): declare types with their field layouts,
/// load Definitions from sources, add or patch them in code. Everything applies in Plugin load order as it
/// is called, so a later Plugin's source or patch changes what an earlier one defined; the schema build bakes
/// the result.
/// </summary>
public sealed class DefinitionRegistration
{
    private readonly DefinitionRegistry _registry;
    private readonly PluginManifest _manifest;

    internal DefinitionRegistration(DefinitionRegistry registry, PluginManifest manifest, int pluginIndex)
    {
        _registry = registry;
        _manifest = manifest;
        PluginIndex = pluginIndex;
    }

    public DefinitionRegistry Registry => _registry;

    internal int PluginIndex { get; }

    /// <summary>
    /// Registers the Definition type <paramref name="typeName"/> held in <typeparamref name="T"/>, describing
    /// the fields sources may set. Returns the table that holds the baked Definitions after build; Systems
    /// capture it and look Definitions up by name without allocating.
    /// </summary>
    public DefinitionTable<T> AddType<T>(string typeName, Action<DefinitionLayout<T>> describe)
        where T : unmanaged
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(typeName);
        ArgumentNullException.ThrowIfNull(describe);
        var layout = new DefinitionLayout<T>(_registry);
        describe(layout);
        return _registry.AddType(typeName, layout, _manifest.Id);
    }

    /// <summary>Registers a Definition type by layout and field offsets (a Native Plugin's type); every field must lie inside the value.</summary>
    internal RawDefinitionTable AddRawType(string typeName, int size, int alignment, System.Collections.Immutable.ImmutableArray<RawDefinitionField> fields)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(typeName);
        if (size <= 0 || alignment <= 0 || (alignment & (alignment - 1)) != 0 || size % alignment != 0)
        {
            throw new ArgumentException($"Definition type '{typeName}': size {size} and alignment {alignment} are not a valid layout.", nameof(size));
        }

        foreach (var field in fields)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(field.Name, nameof(fields));
            if (field.Offset < 0 || field.Offset + RawDefinitionField.SizeOf(field.Kind) > size)
            {
                throw new ArgumentException($"Definition type '{typeName}': field '{field.Name}' at offset {field.Offset} does not fit in {size} bytes.", nameof(fields));
            }
        }

        return _registry.AddRawType(typeName, size, alignment, fields, _manifest.Id);
    }

    /// <summary>Reads every document of <paramref name="source"/> now: new names are added, existing ones patched field by field.</summary>
    public DefinitionRegistration AddSource(IDefinitionSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        _registry.Apply(source, _manifest.Id);
        return this;
    }

    /// <summary>Adds a Definition from code; the name must be new.</summary>
    public DefinitionRegistration Add<T>(string typeName, string name, in T value)
        where T : unmanaged
    {
        ArgumentNullException.ThrowIfNull(typeName);
        _registry.Type<T>(typeName, _manifest.Id).Add(name, in value, _manifest.Id);
        return this;
    }

    /// <summary>Edits an existing Definition in code (a later Plugin tuning an earlier one's content).</summary>
    public DefinitionRegistration Patch<T>(string typeName, string name, DefinitionPatch<T> patch)
        where T : unmanaged
    {
        ArgumentNullException.ThrowIfNull(typeName);
        ArgumentNullException.ThrowIfNull(patch);
        _registry.Type<T>(typeName, _manifest.Id).Patch(name, patch, _manifest.Id);
        return this;
    }

    /// <summary>Interns <paramref name="text"/> in the registry, for Definitions added from code that refer to other names.</summary>
    public StringId Intern(string text) => _registry.Strings.Intern(text);
}
