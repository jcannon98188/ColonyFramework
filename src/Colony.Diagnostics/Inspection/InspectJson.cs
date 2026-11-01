using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Colony.Diagnostics.Inspection;

/// <summary>
/// The JSON mapping of the inspection protocol (spec section 10.2). Null, Boolean, Text, List and Map map to
/// their JSON counterparts (map order kept). Whole is a JSON number without fraction or exponent; Number always
/// carries a fraction or exponent (<c>2.0</c>), so a round trip keeps the kind; non-finite Numbers are written
/// as the strings <c>"NaN"</c>, <c>"Infinity"</c>, <c>"-Infinity"</c> inside <c>{"$number": ...}</c>. Bytes are
/// <c>{"$bytes": "&lt;lowercase hex&gt;"}</c>. Reading accepts the same; any other object is a Map.
/// </summary>
public static class InspectJson
{
    public static string Write(InspectValue value, bool indented = false)
    {
        ArgumentNullException.ThrowIfNull(value);
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = indented, SkipValidation = false }))
        {
            Write(writer, value);
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    public static InspectValue Read(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(json), new JsonReaderOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        if (!reader.Read())
        {
            throw new FormatException("Empty JSON.");
        }

        var value = ReadValue(ref reader);
        if (reader.Read())
        {
            throw new FormatException("Trailing JSON after the value.");
        }

        return value;
    }

    private static void Write(Utf8JsonWriter writer, InspectValue value)
    {
        switch (value.Kind)
        {
            case InspectValueKind.Null:
                writer.WriteNullValue();
                break;
            case InspectValueKind.Boolean:
                writer.WriteBooleanValue(value.AsBoolean);
                break;
            case InspectValueKind.Whole:
                writer.WriteNumberValue(value.AsWhole);
                break;
            case InspectValueKind.Number:
                double number = value.AsNumber;
                if (!double.IsFinite(number))
                {
                    writer.WriteStartObject();
                    writer.WriteString("$number", double.IsNaN(number) ? "NaN" : number > 0 ? "Infinity" : "-Infinity");
                    writer.WriteEndObject();
                    break;
                }

                string text = number.ToString("R", CultureInfo.InvariantCulture);
                if (!text.Contains('.', StringComparison.Ordinal) && !text.Contains('E', StringComparison.Ordinal))
                {
                    text += ".0";
                }

                writer.WriteRawValue(text, skipInputValidation: true);
                break;
            case InspectValueKind.Text:
                writer.WriteStringValue(value.AsText);
                break;
            case InspectValueKind.Bytes:
                writer.WriteStartObject();
                writer.WriteString("$bytes", Convert.ToHexStringLower(value.AsBytes));
                writer.WriteEndObject();
                break;
            case InspectValueKind.List:
                writer.WriteStartArray();
                foreach (var item in value.Items)
                {
                    Write(writer, item);
                }

                writer.WriteEndArray();
                break;
            default:
                writer.WriteStartObject();
                foreach (var entry in value.Entries)
                {
                    writer.WritePropertyName(entry.Key);
                    Write(writer, entry.Value);
                }

                writer.WriteEndObject();
                break;
        }
    }

    private static InspectValue ReadValue(ref Utf8JsonReader reader)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Null:
                return InspectValue.Null;
            case JsonTokenType.True:
                return InspectValue.True;
            case JsonTokenType.False:
                return InspectValue.False;
            case JsonTokenType.String:
                return InspectValue.From(reader.GetString());
            case JsonTokenType.Number:
                var raw = reader.HasValueSequence ? reader.ValueSequence.ToArray() : reader.ValueSpan.ToArray();
                bool fractional = raw.AsSpan().IndexOfAny((byte)'.', (byte)'e', (byte)'E') >= 0;
                if (!fractional && reader.TryGetInt64(out long whole))
                {
                    return InspectValue.From(whole);
                }

                return InspectValue.From(reader.GetDouble());
            case JsonTokenType.StartArray:
                var items = new List<InspectValue>();
                while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                {
                    items.Add(ReadValue(ref reader));
                }

                return InspectValue.List(items);
            case JsonTokenType.StartObject:
                var entries = new List<KeyValuePair<string, InspectValue>>();
                while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
                {
                    string key = reader.GetString()!;
                    reader.Read();
                    entries.Add(new(key, ReadValue(ref reader)));
                }

                if (entries.Count == 1 && entries[0].Value.Kind == InspectValueKind.Text)
                {
                    if (string.Equals(entries[0].Key, "$bytes", StringComparison.Ordinal))
                    {
                        return InspectValue.From(Convert.FromHexString(entries[0].Value.AsText));
                    }

                    if (string.Equals(entries[0].Key, "$number", StringComparison.Ordinal))
                    {
                        return InspectValue.From(entries[0].Value.AsText switch
                        {
                            "NaN" => double.NaN,
                            "Infinity" => double.PositiveInfinity,
                            "-Infinity" => double.NegativeInfinity,
                            var other => throw new FormatException($"Bad $number '{other}'."),
                        });
                    }
                }

                return InspectValue.Map(entries);
            default:
                throw new FormatException($"Unexpected JSON token {reader.TokenType}.");
        }
    }
}
