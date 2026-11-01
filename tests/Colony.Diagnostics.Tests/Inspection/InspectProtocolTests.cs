using Colony.Diagnostics.Inspection;

namespace Colony.Diagnostics.Tests.Inspection;

/// <summary>Spec section 10.2: one versioned protocol, compact binary on the wire with a JSON mapping.</summary>
public sealed class InspectProtocolTests
{
    private static InspectValue Sample() => InspectValue.Map(
        new("op", "entity"),
        new("id", -3),
        new("big", long.MaxValue),
        new("ratio", 2.0),
        new("ok", true),
        new("none", InspectValue.Null),
        new("raw", InspectValue.From([0xDE, 0xAD])),
        new("list", InspectValue.List(1, "two", 3.5)));

    [Fact]
    public void Binary_encoding_matches_the_documented_layout()
    {
        // Map(1){ "a": Whole -1 } = tag 7, count 1, key len 1 'a', tag 2, zigzag(-1) = 1.
        byte[] bytes = InspectCodec.Encode(InspectValue.Map(new KeyValuePair<string, InspectValue>("a", -1)));
        Assert.Equal<byte>([7, 1, 1, (byte)'a', 2, 1], bytes);

        // Whole 300 = zigzag 600 = varint 0xD8 0x04.
        Assert.Equal<byte>([2, 0xD8, 0x04], InspectCodec.Encode(300));
        Assert.Equal<byte>([3, 0, 0, 0, 0, 0, 0, 0xF0, 0x3F], InspectCodec.Encode(1.0));
    }

    [Fact]
    public void Binary_and_json_round_trips_keep_kinds_and_order()
    {
        var value = Sample();
        Assert.Equal(value, InspectCodec.Decode(InspectCodec.Encode(value)));
        string json = InspectJson.Write(value);
        Assert.Equal("""{"op":"entity","id":-3,"big":9223372036854775807,"ratio":2.0,"ok":true,"none":null,"raw":{"$bytes":"dead"},"list":[1,"two",3.5]}""", json);
        Assert.Equal(value, InspectJson.Read(json));
    }

    [Fact]
    public void Frames_carry_one_value_each_and_end_cleanly()
    {
        using var stream = new MemoryStream();
        InspectCodec.WritePreamble(stream);
        InspectCodec.WriteFrame(stream, Sample());
        InspectCodec.WriteFrame(stream, "second");
        stream.Position = 0;
        Assert.Equal(InspectCodec.ProtocolVersion, InspectCodec.ReadPreamble(stream));
        Assert.Equal(Sample(), InspectCodec.ReadFrame(stream));
        Assert.Equal("second", InspectCodec.ReadFrame(stream)!.AsText);
        Assert.Null(InspectCodec.ReadFrame(stream));
    }

    [Fact]
    public void Malformed_input_is_a_format_error()
    {
        Assert.Throws<FormatException>(() => InspectCodec.Decode([9]));
        Assert.Throws<FormatException>(() => InspectCodec.Decode([4, 5, (byte)'a']));
        Assert.Throws<FormatException>(() => InspectCodec.Decode([0, 0]));
        using var bad = new MemoryStream("NOTCOLONY0"u8.ToArray());
        Assert.Throws<FormatException>(() => InspectCodec.ReadPreamble(bad));
    }
}
