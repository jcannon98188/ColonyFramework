using System.Text;
using System.Text.Json;

namespace Colony.Plugins;

/// <summary>
/// The reference <see cref="IDefinitionSource"/> (spec section 8): a UTF-8 JSON document shaped
/// <c>{ "type": { "name": { "field": value, ... }, ... }, ... }</c>, read with <see cref="Utf8JsonReader"/>
/// token by token (no reflection, no object model), so it runs under Native AOT and trimming. Values are
/// integers, numbers, booleans, strings or null; nested objects and arrays are rejected. Documents come back
/// in file order, which is the order they apply in.
/// </summary>
public sealed class JsonDefinitionSource : IDefinitionSource
{
    private readonly ReadOnlyMemory<byte> _utf8;

    private JsonDefinitionSource(ReadOnlyMemory<byte> utf8, string name)
    {
        _utf8 = utf8;
        Name = name;
    }

    public string Name { get; }

    public static JsonDefinitionSource FromUtf8(ReadOnlyMemory<byte> utf8, string name = "json")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return new JsonDefinitionSource(utf8, name);
    }

    public static JsonDefinitionSource FromText(string json, string name = "json")
    {
        ArgumentNullException.ThrowIfNull(json);
        return FromUtf8(Encoding.UTF8.GetBytes(json), name);
    }

    public static JsonDefinitionSource FromFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return FromUtf8(File.ReadAllBytes(path), Path.GetFileName(path));
    }

    public IEnumerable<DefinitionDocument> Read()
    {
        var documents = new List<DefinitionDocument>();
        var reader = new Utf8JsonReader(_utf8.Span, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        Expect(ref reader, JsonTokenType.StartObject, "the root");
        while (Next(ref reader) != JsonTokenType.EndObject)
        {
            string type = PropertyName(ref reader);
            Expect(ref reader, JsonTokenType.StartObject, $"Definition type '{type}'");
            while (Next(ref reader) != JsonTokenType.EndObject)
            {
                string name = PropertyName(ref reader);
                Expect(ref reader, JsonTokenType.StartObject, $"Definition '{type}/{name}'");
                var fields = new List<DefinitionField>();
                while (Next(ref reader) != JsonTokenType.EndObject)
                {
                    string field = PropertyName(ref reader);
                    fields.Add(new DefinitionField(field, Scalar(ref reader, $"'{type}/{name}.{field}'")));
                }

                documents.Add(new DefinitionDocument(type, name, fields));
            }
        }

        if (reader.Read())
        {
            throw Error($"{Name}: unexpected content after the root object at byte {reader.TokenStartIndex}.");
        }

        return documents;
    }

    private DefinitionValue Scalar(ref Utf8JsonReader reader, string what)
    {
        switch (Next(ref reader))
        {
            case JsonTokenType.Number:
                return reader.TryGetInt64(out long integer) ? DefinitionValue.FromWhole(integer) : DefinitionValue.FromNumber(reader.GetDouble());
            case JsonTokenType.True:
                return DefinitionValue.FromBoolean(true);
            case JsonTokenType.False:
                return DefinitionValue.FromBoolean(false);
            case JsonTokenType.String:
                return DefinitionValue.FromText(reader.GetString()!);
            case JsonTokenType.Null:
                return DefinitionValue.Null;
            default:
                throw Error($"{Name}: {what} is {reader.TokenType}; Definition fields are scalars (number, boolean, string or null).");
        }
    }

    private JsonTokenType Next(ref Utf8JsonReader reader)
    {
        if (!reader.Read())
        {
            throw Error($"{Name}: the document ended early.");
        }

        return reader.TokenType;
    }

    private void Expect(ref Utf8JsonReader reader, JsonTokenType token, string what)
    {
        if (Next(ref reader) != token)
        {
            throw Error($"{Name}: {what} is {reader.TokenType}, expected {token}.");
        }
    }

    private string PropertyName(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.PropertyName)
        {
            throw Error($"{Name}: expected a property name at byte {reader.TokenStartIndex}, found {reader.TokenType}.");
        }

        return reader.GetString()!;
    }

    private static FormatException Error(string message) => new(message);
}
