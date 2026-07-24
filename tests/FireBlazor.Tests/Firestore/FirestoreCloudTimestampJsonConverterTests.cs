using System.Text.Json;
using FireBlazor;
using Google.Cloud.Firestore;
using Xunit;

namespace FireBlazor.Tests.Firestore;

public sealed class FirestoreCloudTimestampJsonConverterTests
{
    private static readonly JsonSerializerOptions Options = new()
    {
        Converters = { new FirestoreCloudTimestampJsonConverter() },
    };

    [Fact]
    public void Write_emits_fieldValue_timestamp_sentinel_with_seconds_and_nanoseconds()
    {
        var ts = Timestamp.FromDateTime(DateTime.SpecifyKind(
            new DateTime(2026, 7, 24, 12, 0, 0, DateTimeKind.Utc),
            DateTimeKind.Utc));

        var json = JsonSerializer.Serialize(ts, Options);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal(JsonValueKind.Object, root.ValueKind);
        Assert.Equal("timestamp", root.GetProperty("__fieldValue__").GetString());
        var expectedSeconds = ts.ToDateTime().Subtract(DateTime.UnixEpoch).Ticks / TimeSpan.TicksPerSecond;
        Assert.Equal(expectedSeconds, root.GetProperty("seconds").GetInt64());
        Assert.Equal(0, root.GetProperty("nanoseconds").GetInt32());
    }

    [Fact]
    public void Read_plain_seconds_nanoseconds_map_round_trips()
    {
        var utc = new DateTime(2026, 7, 24, 12, 0, 0, DateTimeKind.Utc);
        var seconds = utc.Subtract(DateTime.UnixEpoch).Ticks / TimeSpan.TicksPerSecond;
        var json = "{\"seconds\":" + seconds + ",\"nanoseconds\":0}";
        var ts = JsonSerializer.Deserialize<Timestamp>(json, Options);
        Assert.Equal(utc, ts.ToDateTime());
    }
}
