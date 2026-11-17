using backend.Models;
using backend.Services;

namespace backend.Tests;

// Helligdager, arbeidstid og SLA-status (rene beregninger, ingen database)
public class SlaCalculatorTests
{
    private static DateTime Utc(int year, int month, int day, int hour, int minute = 0) =>
        new(year, month, day, hour, minute, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(2024, 3, 31)]
    [InlineData(2025, 4, 20)]
    [InlineData(2026, 4, 5)]
    [InlineData(2027, 3, 28)]
    public void Easter_sunday_is_correct(int year, int month, int day) =>
        Assert.Equal(new DateOnly(year, month, day), NorwegianHolidays.EasterSunday(year));

    [Fact]
    public void Holidays_2026_include_moving_and_fixed_days()
    {
        var expected = new[]
        {
            new DateOnly(2026, 1, 1), new DateOnly(2026, 4, 2), new DateOnly(2026, 4, 3),
            new DateOnly(2026, 4, 5), new DateOnly(2026, 4, 6), new DateOnly(2026, 5, 1),
            new DateOnly(2026, 5, 14), new DateOnly(2026, 5, 17), new DateOnly(2026, 5, 24),
            new DateOnly(2026, 5, 25), new DateOnly(2026, 12, 25), new DateOnly(2026, 12, 26),
        };

        Assert.Equal(expected.Order(), NorwegianHolidays.In(2026).Order());
        Assert.False(NorwegianHolidays.IsHoliday(new DateOnly(2026, 12, 24)));
    }

    [Fact]
    public void Time_within_the_working_day_is_added_directly()
    {
        // Tirsdag 17. nov 2026 10:00 norsk tid (UTC+1) + 2 t = 12:00 norsk tid
        var due = SlaCalculator.AddTime(Utc(2026, 11, 17, 9), 120, twentyFourSeven: false);

        Assert.Equal(Utc(2026, 11, 17, 11), due);
    }

    [Fact]
    public void Time_before_opening_starts_at_eight()
    {
        // 06:00 norsk tid + 1 t = 09:00 norsk tid
        var due = SlaCalculator.AddTime(Utc(2026, 11, 17, 5), 60, twentyFourSeven: false);

        Assert.Equal(Utc(2026, 11, 17, 8), due);
    }

    [Fact]
    public void Time_continues_after_the_weekend()
    {
        // Fredag 13. nov 15:00 + 2 t: 1 t fredag, 1 t mandag → mandag 09:00
        var due = SlaCalculator.AddTime(Utc(2026, 11, 13, 14), 120, twentyFourSeven: false);

        Assert.Equal(Utc(2026, 11, 16, 8), due);
    }

    [Fact]
    public void Easter_holidays_are_skipped()
    {
        // Onsdag 1. april 2026 15:00 (sommertid, UTC+2) + 2 t: skjærtorsdag,
        // langfredag, helg og 2. påskedag hoppes over → tirsdag 7. april 09:00
        var due = SlaCalculator.AddTime(Utc(2026, 4, 1, 13), 120, twentyFourSeven: false);

        Assert.Equal(Utc(2026, 4, 7, 7), due);
    }

    [Fact]
    public void Several_working_days_are_counted_in_eight_hour_days()
    {
        // Mandag 16. nov 10:00 + 3 arbeidsdager = torsdag 19. nov 10:00
        var due = SlaCalculator.AddTime(Utc(2026, 11, 16, 9), 3 * 8 * 60, twentyFourSeven: false);

        Assert.Equal(Utc(2026, 11, 19, 9), due);
    }

    [Fact]
    public void Twenty_four_seven_ignores_working_hours()
    {
        // Lørdag 14. nov 23:00 + 4 t
        var due = SlaCalculator.AddTime(Utc(2026, 11, 14, 22), 240, twentyFourSeven: true);

        Assert.Equal(Utc(2026, 11, 15, 2), due);
    }

    private static Ticket TicketWithDeadlines(DateTime created) => new()
    {
        CreatedAt = created,
        ResponseDueAt = created.AddHours(1),
        ResolutionDueAt = created.AddHours(4),
    };

    [Fact]
    public void Status_is_none_without_deadlines() =>
        Assert.Equal(SlaStatuses.None, SlaEvaluator.Evaluate(new Ticket(), DateTime.UtcNow));

    [Fact]
    public void Status_follows_the_deadlines()
    {
        var created = Utc(2026, 11, 17, 8);
        var ticket = TicketWithDeadlines(created);

        Assert.Equal(SlaStatuses.Ok, SlaEvaluator.Evaluate(ticket, created.AddMinutes(10)));

        // 75 % av responstiden er brukt
        Assert.Equal(SlaStatuses.AtRisk, SlaEvaluator.Evaluate(ticket, created.AddMinutes(45)));

        Assert.Equal(SlaStatuses.Breached, SlaEvaluator.Evaluate(ticket, created.AddMinutes(61)));

        // Responsen kom i tide; løsningsfristen er ikke i fare ennå
        ticket.FirstRespondedAt = created.AddMinutes(20);
        Assert.Equal(SlaStatuses.Ok, SlaEvaluator.Evaluate(ticket, created.AddMinutes(61)));
        Assert.Equal(SlaStatuses.AtRisk, SlaEvaluator.Evaluate(ticket, created.AddHours(3.5)));
        Assert.Equal(SlaStatuses.Breached, SlaEvaluator.Evaluate(ticket, created.AddHours(5)));

        // Løst i tide forblir oppfylt, også senere
        ticket.ResolvedAt = created.AddHours(2);
        Assert.Equal(SlaStatuses.Met, SlaEvaluator.Evaluate(ticket, created.AddDays(10)));

        // Løst for sent forblir brudd
        ticket.ResolvedAt = created.AddHours(5);
        Assert.Equal(SlaStatuses.Breached, SlaEvaluator.Evaluate(ticket, created.AddDays(10)));
    }
}
