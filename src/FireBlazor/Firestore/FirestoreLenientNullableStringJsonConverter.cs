using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FireBlazor;

/// <summary>
/// Firestore / WASM interop sometimes surfaces scalars as typed value maps
/// (<c>stringValue</c>, <c>referenceValue</c>, <c>nullValue</c>) instead of plain JSON strings.
/// Coerces those into nullable strings without failing property loads.
/// </summary>
public sealed class FirestoreLenientNullableStringJsonConverter : JsonConverter<string?>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return reader.TokenType switch
        {
            JsonTokenType.Null => null,
            JsonTokenType.String => Normalize(reader.GetString()),
            JsonTokenType.Number => NormalizeNumber(ref reader),
            JsonTokenType.True or JsonTokenType.False => null,
            JsonTokenType.StartObject => CoerceFromObject(ref reader),
            _ => null,
        };
    }

    public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStringValue(value);
    }

    private static string? CoerceFromObject(ref Utf8JsonReader reader)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        return Coerce(doc.RootElement);
    }

    private static string? Coerce(JsonElement el) =>
        el.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.String => Normalize(el.GetString()),
            JsonValueKind.Number => el.GetRawText(),
            JsonValueKind.Object when HasNullValue(el) => null,
            JsonValueKind.Object when TryGetScopedString(el, "stringValue", out var sv) =>
                Normalize(sv),
            JsonValueKind.Object when TryGetScopedString(el, "referenceValue", out var rv) =>
                LastPathSegment(rv),
            JsonValueKind.Object when TryGetScopedString(el, "path", out var path) =>
                LastPathSegment(path),
            JsonValueKind.Object when TryGetPathFromSegments(el, out var segPath) =>
                LastPathSegment(segPath),
            _ => null,
        };

    private static bool TryGetPathFromSegments(JsonElement obj, out string? path)
    {
        path = null;
        if (!obj.TryGetProperty("_path", out var pathEl) || pathEl.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (!pathEl.TryGetProperty("segments", out var segments) || segments.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var parts = new List<string>();
        foreach (var segment in segments.EnumerateArray())
        {
            if (segment.ValueKind == JsonValueKind.String)
            {
                var s = segment.GetString();
                if (!string.IsNullOrWhiteSpace(s))
                {
                    parts.Add(s.Trim());
                }
            }
        }

        if (parts.Count == 0)
        {
            return false;
        }

        path = string.Join('/', parts);
        return true;
    }

    private static bool HasNullValue(JsonElement obj) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty("nullValue", out _);

    private static bool TryGetScopedString(JsonElement obj, string name, out string? text)
    {
        text = null;
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(name, out var nested))
        {
            return false;
        }

        if (nested.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        text = nested.GetString();
        return !string.IsNullOrWhiteSpace(text);
    }

    private static string? LastPathSegment(string? path)
    {
        var t = Normalize(path);
        if (string.IsNullOrEmpty(t))
        {
            return null;
        }

        ReadOnlySpan<char> span = t.AsSpan().Trim('/');
        if (span.IsEmpty)
        {
            return null;
        }

        var lastSlash = span.LastIndexOf('/');
        var seg = lastSlash >= 0 ? span[(lastSlash + 1)..] : span;
        var decoded = Uri.UnescapeDataString(seg.ToString()).Trim();
        return string.IsNullOrEmpty(decoded) ? null : decoded;
    }

    private static string? Normalize(string? raw)
    {
        var t = raw?.Trim();
        return string.IsNullOrEmpty(t) ? null : t;
    }

    private static string? NormalizeNumber(ref Utf8JsonReader reader)
    {
        if (reader.TryGetInt64(out var i))
        {
            return i.ToString(CultureInfo.InvariantCulture);
        }

        return reader.TryGetDouble(out var d)
            ? d.ToString(CultureInfo.InvariantCulture)
            : null;
    }
}
