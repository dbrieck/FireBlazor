using System.Text.Json;
using System.Text.Json.Serialization;
using Google.Cloud.Firestore;

namespace FireBlazor;

/// <summary>
/// Deserializes Firestore protobuf-style timestamp maps to <see cref="Timestamp"/>.
/// Needed for models (e.g. accounting transactions) that use <see cref="Timestamp"/> on WASM.
/// </summary>
public sealed class FirestoreCloudTimestampJsonConverter : JsonConverter<Timestamp>
{
    private static readonly Timestamp UnparseableSentinel =
        Timestamp.FromDateTime(DateTime.SpecifyKind(DateTime.UnixEpoch, DateTimeKind.Utc));

    public override Timestamp Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return UnparseableSentinel;
        }

        if (reader.TokenType == JsonTokenType.String)
        {
            var str = reader.GetString();
            if (!string.IsNullOrWhiteSpace(str)
                && DateTime.TryParse(
                    str,
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal
                        | System.Globalization.DateTimeStyles.AdjustToUniversal,
                    out var dt))
            {
                return Timestamp.FromDateTime(DateTime.SpecifyKind(dt, DateTimeKind.Utc));
            }

            return UnparseableSentinel;
        }

        if (reader.TokenType != JsonTokenType.StartObject)
        {
            return UnparseableSentinel;
        }

        long? seconds = null;
        var nanoseconds = 0;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                continue;
            }

            var prop = reader.GetString();
            reader.Read();
            if (prop is "seconds" or "_seconds")
            {
                if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt64(out var s))
                {
                    seconds = s;
                }
            }
            else if (prop is "nanoseconds" or "_nanoseconds")
            {
                if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var n))
                {
                    nanoseconds = n;
                }
            }
            else
            {
                reader.Skip();
            }
        }

        if (seconds is null)
        {
            return UnparseableSentinel;
        }

        try
        {
            var utc = DateTime.UnixEpoch.AddSeconds(seconds.Value).AddTicks(nanoseconds / 100);
            return Timestamp.FromDateTime(DateTime.SpecifyKind(utc, DateTimeKind.Utc));
        }
        catch (ArgumentOutOfRangeException)
        {
            return UnparseableSentinel;
        }
    }

    public override void Write(Utf8JsonWriter writer, Timestamp value, JsonSerializerOptions options)
    {
        // Emit a FieldValue-style sentinel so fireblazor.js transformFieldValues can construct a real
        // firebase/firestore Timestamp. Plain {seconds,nanoseconds} maps are stored as maps and fail
        // rules that require `field is timestamp` (e.g. journalEntries.date).
        var utc = value.ToDateTime();
        if (utc.Kind != DateTimeKind.Utc)
        {
            utc = DateTime.SpecifyKind(utc, DateTimeKind.Utc);
        }

        var ticks = utc.Ticks - DateTime.UnixEpoch.Ticks;
        var seconds = ticks / TimeSpan.TicksPerSecond;
        var nanoseconds = (int)((ticks % TimeSpan.TicksPerSecond) * 100); // 1 tick = 100 ns

        writer.WriteStartObject();
        writer.WriteString("__fieldValue__", "timestamp");
        writer.WriteNumber("seconds", seconds);
        writer.WriteNumber("nanoseconds", nanoseconds);
        writer.WriteEndObject();
    }
}
