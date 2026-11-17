using System.Text.Json;
using backend.Services;

namespace backend.Tests;

// Tidspunkter i forespørsler blir alltid UTC (PostgreSQL godtar bare UTC)
public class UtcDateTimeConverterTests
{
    private static readonly JsonSerializerOptions Options = new() { Converters = { new UtcDateTimeConverter() } };

    private static DateTime Read(string json) => JsonSerializer.Deserialize<DateTime>(json, Options);

    [Fact]
    public void Plain_date_is_read_as_utc_midnight() =>
        Assert.Equal(new DateTime(2026, 1, 15, 0, 0, 0, DateTimeKind.Utc), Read("\"2026-01-15\""));

    [Fact]
    public void Time_without_zone_is_read_as_utc()
    {
        var value = Read("\"2026-01-15T12:30:00\"");

        Assert.Equal(DateTimeKind.Utc, value.Kind);
        Assert.Equal(12, value.Hour);
    }

    [Fact]
    public void Time_with_offset_is_converted_to_utc()
    {
        var value = Read("\"2026-01-15T12:30:00+01:00\"");

        Assert.Equal(DateTimeKind.Utc, value.Kind);
        Assert.Equal(11, value.Hour);
    }
}
