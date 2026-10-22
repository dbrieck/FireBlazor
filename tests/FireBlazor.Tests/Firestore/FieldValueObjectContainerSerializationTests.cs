using System.Text.Json;
using FireBlazor.Platform.Wasm;

namespace FireBlazor.Tests.Firestore;

/// <summary>
/// Every FieldValue sentinel must serialize to the <c>__fieldValue__</c> marker form whatever the declared type
/// of the slot holding it. System.Text.Json resolves converters by runtime type for <see cref="object"/> slots
/// (<c>Dictionary&lt;string, object&gt;</c> updates, <c>object</c> properties, nested maps), so a converter that
/// only matched <c>typeof(FieldValue)</c> wrote sentinels there as plain maps (<c>{}</c>, <c>{"elements":[...]}</c>).
/// </summary>
public class FieldValueObjectContainerSerializationTests
{
    public static TheoryData<string> Kinds => new()
    {
        "serverTimestamp", "increment", "incrementDouble", "arrayUnion", "arrayRemove", "delete",
    };

    private static FieldValue Create(string kind) => kind switch
    {
        "serverTimestamp" => FieldValue.ServerTimestamp(),
        "increment" => FieldValue.Increment(3),
        "incrementDouble" => FieldValue.Increment(1.5),
        "arrayUnion" => FieldValue.ArrayUnion("a", "b"),
        "arrayRemove" => FieldValue.ArrayRemove("a"),
        "delete" => FieldValue.Delete(),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static void AssertSentinel(string kind, JsonElement element)
    {
        Assert.Equal(JsonValueKind.Object, element.ValueKind);
        var marker = kind == "incrementDouble" ? "increment" : kind;
        Assert.Equal(marker, element.GetProperty("__fieldValue__").GetString());

        switch (kind)
        {
            case "increment":
                Assert.Equal(3, element.GetProperty("value").GetInt64());
                break;
            case "incrementDouble":
                Assert.Equal(1.5, element.GetProperty("value").GetDouble());
                break;
            case "arrayUnion":
                Assert.Equal(["a", "b"], element.GetProperty("elements").EnumerateArray().Select(e => e.GetString()));
                break;
            case "arrayRemove":
                Assert.Equal(["a"], element.GetProperty("elements").EnumerateArray().Select(e => e.GetString()));
                break;
            default:
                Assert.Single(element.EnumerateObject());
                break;
        }
    }

    private static JsonElement Serialize(object value) =>
        JsonSerializer.SerializeToElement(value, FirestoreJsonOptions.Default);

    [Theory]
    [MemberData(nameof(Kinds))]
    public void Declared_as_FieldValue(string kind)
    {
        var json = Serialize(new { field = Create(kind) });
        AssertSentinel(kind, json.GetProperty("field"));
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public void Declared_as_object_property(string kind)
    {
        object boxed = Create(kind);
        var json = Serialize(new { field = boxed });
        AssertSentinel(kind, json.GetProperty("field"));
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public void Serialized_as_root_object(string kind)
    {
        object boxed = Create(kind);
        AssertSentinel(kind, Serialize(boxed));
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public void Inside_object_dictionary(string kind)
    {
        var fields = new Dictionary<string, object> { ["field"] = Create(kind), ["plain"] = "x" };
        var json = Serialize(fields);
        AssertSentinel(kind, json.GetProperty("field"));
        Assert.Equal("x", json.GetProperty("plain").GetString());
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public void Inside_nested_object_dictionary(string kind)
    {
        var fields = new Dictionary<string, object>
        {
            ["outer"] = new Dictionary<string, object> { ["inner"] = Create(kind) },
        };
        var json = Serialize(fields);
        AssertSentinel(kind, json.GetProperty("outer").GetProperty("inner"));
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public void Inside_nested_anonymous_object(string kind)
    {
        object boxed = Create(kind);
        var json = Serialize(new { outer = new { inner = boxed } });
        AssertSentinel(kind, json.GetProperty("outer").GetProperty("inner"));
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public void Inside_nullable_object_dictionary(string kind)
    {
        var fields = new Dictionary<string, object?> { ["field"] = Create(kind), ["cleared"] = null };
        var json = Serialize(fields);
        AssertSentinel(kind, json.GetProperty("field"));
        Assert.Equal(JsonValueKind.Null, json.GetProperty("cleared").ValueKind);
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public void Standalone_converter_options_cover_object_slots(string kind)
    {
        var options = new JsonSerializerOptions { Converters = { new FieldValueConverter() } };
        var json = JsonSerializer.SerializeToElement(new Dictionary<string, object> { ["field"] = Create(kind) }, options);
        AssertSentinel(kind, json.GetProperty("field"));
    }

    [Fact]
    public void Converter_claims_every_FieldValue_subtype_and_nothing_else()
    {
        var converter = new FieldValueConverter();
        foreach (var subtype in typeof(FieldValue).Assembly.GetTypes().Where(t => t.IsSubclassOf(typeof(FieldValue))))
        {
            Assert.True(converter.CanConvert(subtype), subtype.Name);
        }
        Assert.True(converter.CanConvert(typeof(FieldValue)));
        Assert.False(converter.CanConvert(typeof(object)));
        Assert.False(converter.CanConvert(typeof(string)));
    }
}
