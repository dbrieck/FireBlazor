using FireBlazor.Platform.Wasm;
using Google.Cloud.Firestore;

namespace FireBlazor.Tests.Firestore;

/// <summary>
/// Guards the fix for the WASM timestamp-query bug: a bare <see cref="Timestamp"/> used as a Where
/// bound (or cursor value) must be serialized as the <c>{ __fieldValue__: "timestamp", seconds,
/// nanoseconds }</c> sentinel so the JS bridge can rebuild a real Firestore Timestamp. Left unnormalized
/// it serializes to <c>{}</c> and every date-range / timestamp-cursor query silently returns nothing.
/// </summary>
public class FirestoreQueryValueTests
{
    private static (string? fieldValue, long seconds, int nanoseconds) ReadSentinel(object? value)
    {
        Assert.NotNull(value);
        var type = value!.GetType();
        var fv = type.GetProperty("__fieldValue__")?.GetValue(value) as string;
        var seconds = (long)type.GetProperty("seconds")!.GetValue(value)!;
        var nanoseconds = (int)type.GetProperty("nanoseconds")!.GetValue(value)!;
        return (fv, seconds, nanoseconds);
    }

    [Fact]
    public void Normalize_Timestamp_EmitsTimestampSentinel()
    {
        var utc = new DateTime(2026, 6, 15, 0, 0, 0, DateTimeKind.Utc);
        var ts = Timestamp.FromDateTime(utc);

        var (fieldValue, seconds, nanoseconds) = ReadSentinel(FirestoreQueryValue.Normalize(ts));

        Assert.Equal("timestamp", fieldValue);
        Assert.Equal((long)(utc - DateTime.UnixEpoch).TotalSeconds, seconds);
        Assert.Equal(0, nanoseconds);
    }

    [Fact]
    public void Normalize_DateTime_EmitsTimestampSentinel()
    {
        var utc = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);

        var (fieldValue, seconds, _) = ReadSentinel(FirestoreQueryValue.Normalize(utc));

        Assert.Equal("timestamp", fieldValue);
        Assert.Equal((long)(utc - DateTime.UnixEpoch).TotalSeconds, seconds);
    }

    [Fact]
    public void Normalize_LeavesScalarsUnchanged()
    {
        Assert.Equal("WS1", FirestoreQueryValue.Normalize("WS1"));
        Assert.Equal(42L, FirestoreQueryValue.Normalize(42L));
        Assert.Equal(false, FirestoreQueryValue.Normalize(false));
        Assert.Null(FirestoreQueryValue.Normalize(null));
    }

    [Fact]
    public void Normalize_Collection_NormalizesEachElement()
    {
        var ts = Timestamp.FromDateTime(new DateTime(2026, 6, 15, 0, 0, 0, DateTimeKind.Utc));

        var normalized = FirestoreQueryValue.Normalize(new object[] { "a", ts });

        var list = Assert.IsAssignableFrom<System.Collections.IEnumerable>(normalized)
            .Cast<object?>()
            .ToList();
        Assert.Equal("a", list[0]);
        Assert.Equal("timestamp", ReadSentinel(list[1]).fieldValue);
    }

    [Fact]
    public void NormalizeCursor_NormalizesTimestampAndPreservesDocId()
    {
        var ts = Timestamp.FromDateTime(new DateTime(2026, 6, 15, 0, 0, 0, DateTimeKind.Utc));

        var cursor = FirestoreQueryValue.NormalizeCursor(new object[] { ts, "L0" });

        Assert.NotNull(cursor);
        Assert.Equal("timestamp", ReadSentinel(cursor![0]).fieldValue);
        Assert.Equal("L0", cursor[1]);
    }
}
