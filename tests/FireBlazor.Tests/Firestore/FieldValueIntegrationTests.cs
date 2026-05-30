using System.Text.Json;
using FireBlazor;
using FireBlazor.Platform.Wasm;

namespace FireBlazor.Tests.Firestore;

public class FieldValueIntegrationTests
{
    [Fact]
    public void FirestoreJsonOptions_ContainsFieldValueConverter()
    {
        var options = FirestoreJsonOptions.Default;
        var hasConverter = options.Converters.Any(c => c is FieldValueConverter);
        Assert.True(hasConverter, "FirestoreJsonOptions should include FieldValueConverter");
    }

    [Fact]
    public void FirestoreJsonOptions_ContainsLenientStringConverter()
    {
        var options = FirestoreJsonOptions.Default;
        var hasConverter = options.Converters.Any(c => c is FirestoreLenientNullableStringJsonConverter);
        Assert.True(hasConverter, "FirestoreJsonOptions should include FirestoreLenientNullableStringJsonConverter");
    }

    [Fact]
    public void DeserializeWithFirestoreOptions_accepts_stringValue_map_for_string_fields()
    {
        const string json = """{"auctionType":{"stringValue":"sheriff-sale"}}""";
        var model = JsonSerializer.Deserialize<SamplePropertyDoc>(json, FirestoreJsonOptions.Default);
        Assert.Equal("sheriff-sale", model!.AuctionType);
    }

    private sealed class SamplePropertyDoc
    {
        public string? AuctionType { get; set; }
    }

    [Fact]
    public void SerializeWithFirestoreOptions_HandlesFieldValues()
    {
        var data = new { lastUpdated = FieldValue.ServerTimestamp() };
        var json = JsonSerializer.Serialize(data, FirestoreJsonOptions.Default);
        Assert.Contains("__fieldValue__", json);
    }
}
