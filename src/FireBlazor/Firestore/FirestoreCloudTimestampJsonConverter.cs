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
        writer.WriteStartObject();
        writer.WriteNumber("seconds", value.ToDateTime().Subtract(DateTime.UnixEpoch).Ticks / TimeSpan.TicksPerSecond);
        writer.WriteNumber("nanoseconds", 0);
        writer.WriteEndObject();
    }
}
