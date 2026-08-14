using System.Text.Json;

namespace FireBlazor.Platform.Wasm;

/// <summary>
/// Shared JSON serialization options for Firestore operations.
/// </summary>
internal static class FirestoreJsonOptions
{
    public static readonly JsonSerializerOptions Default = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters =
        {
            new FieldValueConverter(),
            new FirestoreTimestampConverter(),
            new FirestoreCloudTimestampJsonConverter(),
            new FirestoreLenientNullableStringJsonConverter(),
        }
    };
}

/// <summary>
/// Shared helper for parsing Firestore document snapshots from JSON.
/// </summary>
internal static class SnapshotParser
{
    public static DocumentSnapshot<T> Parse<T>(JsonElement item, string? fallbackId = null, string? fallbackPath = null) where T : class
    {
        var id = item.TryGetProperty("id", out var idElement) ? idElement.GetString() ?? fallbackId ?? "" : fallbackId ?? "";
        var path = item.TryGetProperty("path", out var pathElement) ? pathElement.GetString() ?? fallbackPath ?? "" : fallbackPath ?? "";
        var exists = item.TryGetProperty("exists", out var existsElement) && existsElement.GetBoolean();

        T? docData = default;
        SnapshotMetadata? metadata = null;

        if (exists && item.TryGetProperty("data", out var dataElement))
        {
            docData = JsonSerializer.Deserialize<T>(dataElement.GetRawText(), FirestoreJsonOptions.Default);
        }

        if (item.TryGetProperty("metadata", out var metaElement))
        {
            metadata = new SnapshotMetadata
            {
                IsFromCache = metaElement.TryGetProperty("isFromCache", out var fromCache) && fromCache.GetBoolean(),
                HasPendingWrites = metaElement.TryGetProperty("hasPendingWrites", out var pending) && pending.GetBoolean()
            };
        }

        return new DocumentSnapshot<T>
        {
            Id = id,
            Path = path,
            Exists = exists,
            Data = docData,
            Metadata = metadata
        };
    }

    public static IReadOnlyList<DocumentSnapshot<T>> ParseMany<T>(JsonElement data) where T : class
    {
        var snapshots = new List<DocumentSnapshot<T>>();

        if (data.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in data.EnumerateArray())
            {
                snapshots.Add(Parse<T>(item));
            }
        }
        else if (data.ValueKind == JsonValueKind.Object)
        {
            snapshots.Add(Parse<T>(data));
        }

        return snapshots;
    }
}

/// <summary>
/// Shared helper for converting property names to camelCase for JavaScript interop.
/// </summary>
internal static class CamelCaseHelper
{
    public static string ToCamelCase(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;
        return char.ToLowerInvariant(name[0]) + name[1..];
    }
}

/// <summary>
/// Normalizes a query comparison value (Where clause / cursor value) into a JS-interop-safe shape.
///
/// Blazor's JS interop serializes call arguments with the WASM runtime's own <see cref="JsonSerializerOptions"/>,
/// which do NOT carry FireBlazor's <see cref="FirestoreCloudTimestampJsonConverter"/>. A bare
/// <see cref="Google.Cloud.Firestore.Timestamp"/> therefore serializes to <c>{}</c> (it exposes no
/// serializable public properties), so a <c>where("date", "&gt;=", ts)</c> bound reaches the Firebase JS SDK
/// as an empty map and matches nothing — silently breaking every timestamp range / cursor query on WASM.
///
/// This emits the same <c>{ __fieldValue__: "timestamp", seconds, nanoseconds }</c> sentinel the document
/// write path uses, which <c>fireblazor.js transformFieldValues</c> reconstructs into a real Firestore
/// <c>Timestamp</c> before applying the constraint. Non-temporal values pass through unchanged; collections
/// (for <c>in</c> / <c>array-contains-any</c>) are normalized element-wise.
/// </summary>
internal static class FirestoreQueryValue
{
    public static object? Normalize(object? value) => value switch
    {
        null => null,
        Google.Cloud.Firestore.Timestamp ts => TimestampSentinel(ts.ToDateTime()),
        DateTime dt => TimestampSentinel(DateTime.SpecifyKind(dt, DateTimeKind.Utc)),
        DateTimeOffset dto => TimestampSentinel(dto.UtcDateTime),
        string => value,
        System.Collections.IEnumerable items => NormalizeMany(items),
        _ => value,
    };

    public static object?[]? NormalizeCursor(object[]? values) =>
        values?.Select(Normalize).ToArray();

    private static List<object?> NormalizeMany(System.Collections.IEnumerable items)
    {
        var list = new List<object?>();
        foreach (var item in items)
        {
            list.Add(Normalize(item));
        }

        return list;
    }

    private static object TimestampSentinel(DateTime utc)
    {
        if (utc.Kind != DateTimeKind.Utc)
        {
            utc = DateTime.SpecifyKind(utc, DateTimeKind.Utc);
        }

        var ticks = utc.Ticks - DateTime.UnixEpoch.Ticks;
        var seconds = ticks / TimeSpan.TicksPerSecond;
        var nanoseconds = (int)(ticks % TimeSpan.TicksPerSecond * 100); // 1 tick = 100 ns
        return new { __fieldValue__ = "timestamp", seconds, nanoseconds };
    }
}
