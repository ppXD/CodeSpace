using Cronos;

namespace CodeSpace.Core.Services.Workflows.RunSources.Schedule;

/// <summary>
/// Pure cron math for the schedule trigger. Wraps Cronos so the producer (and its tests) reason
/// about "which scheduled instants fall in this window" without touching the DB or the clock —
/// <c>now</c> is always passed in.
///
/// <para>Standard 5-field cron (minute hour day-of-month month day-of-week), evaluated in UTC —
/// matching the server-side, minute-granularity scheduler. Seconds-level (6-field) cron is
/// deliberately NOT accepted: the producer ticks per minute, so sub-minute precision can't be
/// honoured and would mislead operators.</para>
/// </summary>
public static class ScheduleOccurrenceCalculator
{
    /// <summary>
    /// Occurrences of <paramref name="cronExpression"/> in the half-open window
    /// <c>(fromExclusive, toInclusive]</c>, in UTC. Returns <c>true</c> with the (possibly empty)
    /// list when the expression parses; <c>false</c> with an empty list when it's malformed — the
    /// caller logs + skips rather than throwing, so one bad schedule never blocks the others.
    /// </summary>
    public static bool TryGetOccurrences(string cronExpression, DateTimeOffset fromExclusive, DateTimeOffset toInclusive, out IReadOnlyList<DateTimeOffset> occurrences)
    {
        occurrences = Array.Empty<DateTimeOffset>();

        if (string.IsNullOrWhiteSpace(cronExpression)) return false;

        CronExpression parsed;
        try
        {
            parsed = CronExpression.Parse(cronExpression.Trim(), CronFormat.Standard);
        }
        catch (CronFormatException)
        {
            return false;
        }

        occurrences = parsed
            .GetOccurrences(fromExclusive, toInclusive, TimeZoneInfo.Utc, fromInclusive: false, toInclusive: true)
            .ToList();

        return true;
    }

    /// <summary>True iff <paramref name="cronExpression"/> is a valid standard 5-field cron.</summary>
    public static bool IsValid(string cronExpression)
    {
        if (string.IsNullOrWhiteSpace(cronExpression)) return false;

        try
        {
            CronExpression.Parse(cronExpression.Trim(), CronFormat.Standard);
            return true;
        }
        catch (CronFormatException)
        {
            return false;
        }
    }
}
