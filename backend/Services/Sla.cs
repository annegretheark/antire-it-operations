using System.Collections.Concurrent;
using backend.Models;

namespace backend.Services;

// Offentlige høytidsdager i Norge (helligdager og 1. og 17. mai).
// Julaften og nyttårsaften er ikke med: de er vanlige virkedager etter loven.
public static class NorwegianHolidays
{
    private static readonly ConcurrentDictionary<int, HashSet<DateOnly>> Cache = new();

    public static bool IsHoliday(DateOnly date) =>
        Cache.GetOrAdd(date.Year, Compute).Contains(date);

    public static IReadOnlyCollection<DateOnly> In(int year) =>
        Cache.GetOrAdd(year, Compute);

    private static HashSet<DateOnly> Compute(int year)
    {
        var easter = EasterSunday(year);

        return
        [
            new DateOnly(year, 1, 1),    // 1. nyttårsdag
            easter.AddDays(-3),          // skjærtorsdag
            easter.AddDays(-2),          // langfredag
            easter,                      // 1. påskedag
            easter.AddDays(1),           // 2. påskedag
            new DateOnly(year, 5, 1),    // arbeidernes dag
            new DateOnly(year, 5, 17),   // grunnlovsdag
            easter.AddDays(39),          // Kristi himmelfartsdag
            easter.AddDays(49),          // 1. pinsedag
            easter.AddDays(50),          // 2. pinsedag
            new DateOnly(year, 12, 25),  // 1. juledag
            new DateOnly(year, 12, 26),  // 2. juledag
        ];
    }

    // Gregoriansk påskedag (Meeus/Jones/Butcher)
    public static DateOnly EasterSunday(int year)
    {
        int a = year % 19;
        int b = year / 100;
        int c = year % 100;
        int d = b / 4;
        int e = b % 4;
        int f = (b + 8) / 25;
        int g = (b - f + 1) / 3;
        int h = (19 * a + b - d - g + 15) % 30;
        int i = c / 4;
        int k = c % 4;
        int l = (32 + 2 * e + 2 * i - h - k) % 7;
        int m = (a + 11 * h + 22 * l) / 451;
        int month = (h + l - 7 * m + 114) / 31;
        int day = (h + l - 7 * m + 114) % 31 + 1;

        return new DateOnly(year, month, day);
    }
}

// Beregner SLA-frister i arbeidstid: 08:00–16:00 norsk tid, mandag til fredag,
// unntatt helligdager. Med 24/7 legges tiden bare til.
public static class SlaCalculator
{
    public static readonly TimeSpan WorkdayStart = TimeSpan.FromHours(8);
    public static readonly TimeSpan WorkdayEnd = TimeSpan.FromHours(16);

    public static readonly TimeZoneInfo Norway = TimeZoneInfo.FindSystemTimeZoneById("Europe/Oslo");

    public static bool IsWorkingDay(DateTime localDate) =>
        localDate.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday) &&
        !NorwegianHolidays.IsHoliday(DateOnly.FromDateTime(localDate));

    // Fristen når minutes skal legges til startUtc. Svaret er i UTC.
    public static DateTime AddTime(DateTime startUtc, int minutes, bool twentyFourSeven)
    {
        startUtc = DateTime.SpecifyKind(startUtc, DateTimeKind.Utc);

        if (twentyFourSeven)
            return startUtc.AddMinutes(minutes);

        var local = MoveToWorkingTime(TimeZoneInfo.ConvertTimeFromUtc(startUtc, Norway));
        var remaining = TimeSpan.FromMinutes(minutes);

        while (true)
        {
            var endOfDay = local.Date + WorkdayEnd;
            var available = endOfDay - local;

            if (remaining <= available)
            {
                local += remaining;
                break;
            }

            remaining -= available;
            local = MoveToWorkingTime(endOfDay);
        }

        return TimeZoneInfo.ConvertTimeToUtc(local, Norway);
    }

    // Neste tidspunkt i arbeidstiden (eller tidspunktet selv om det er innenfor)
    private static DateTime MoveToWorkingTime(DateTime local)
    {
        while (true)
        {
            if (IsWorkingDay(local.Date))
            {
                if (local.TimeOfDay < WorkdayStart)
                    return local.Date + WorkdayStart;

                if (local.TimeOfDay < WorkdayEnd)
                    return local;
            }

            local = local.Date.AddDays(1);
        }
    }
}

// SLA-status for en sak på et gitt tidspunkt
public static class SlaEvaluator
{
    // Andel av tiden som må være brukt før en frist regnes som «i fare»
    public const double AtRiskShare = 0.75;

    public static string Evaluate(Ticket ticket, DateTime nowUtc)
    {
        if (ticket.ResponseDueAt == null && ticket.ResolutionDueAt == null)
            return SlaStatuses.None;

        var responseBreached = ticket.ResponseDueAt is { } responseDue &&
            (ticket.FirstRespondedAt ?? nowUtc) > responseDue;

        var resolutionBreached = ticket.ResolutionDueAt is { } resolutionDue &&
            (ticket.ResolvedAt ?? nowUtc) > resolutionDue;

        if (responseBreached || resolutionBreached)
            return SlaStatuses.Breached;

        if (ticket.ResolvedAt != null)
            return SlaStatuses.Met;

        var responseAtRisk = ticket.FirstRespondedAt == null &&
            IsAtRisk(ticket.CreatedAt, ticket.ResponseDueAt, nowUtc);

        return responseAtRisk || IsAtRisk(ticket.CreatedAt, ticket.ResolutionDueAt, nowUtc)
            ? SlaStatuses.AtRisk
            : SlaStatuses.Ok;
    }

    private static bool IsAtRisk(DateTime start, DateTime? due, DateTime now) =>
        due is { } d && now >= start + (d - start) * AtRiskShare;
}
