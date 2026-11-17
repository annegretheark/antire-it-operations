using System.Text.Json;
using System.Text.Json.Serialization;

namespace backend.Services;

// Tidspunkter i forespørsler lagres alltid som UTC. Uten dette ville en ren dato
// («2026-01-15») eller et tidspunkt uten tidssone gi DateTimeKind.Unspecified, som
// PostgreSQL (timestamp with time zone) nekter å lagre. Tidspunkter med tidssone
// gjøres om til UTC; tidspunkter uten tolkes som UTC. Svarene endres ikke.
public sealed class UtcDateTimeConverter : JsonConverter<DateTime>
{
    public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var value = reader.GetDateTime();

        return value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        };
    }

    public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value);
}
