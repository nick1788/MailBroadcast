// File: DailyScheduler.cs — computes the next local 7:00 AM run as a UTC instant with DST handling.
namespace MailBroadcast.Worker.Services;

/// <summary>Represents the local scheduled date and its corresponding UTC execution instant.</summary>
public sealed record ScheduledRun(DateOnly Date, DateTimeOffset AtUtc);

/// <summary>Provides pure scheduling calculations for the worker's daily broadcast.</summary>
public static class DailyScheduler
{
    /// <summary>Finds the next uncompleted 7:00 AM run in a time zone, catching up when today's run is due.</summary>
    /// <param name="nowUtc">Current time as a UTC instant.</param>
    /// <param name="timeZone">Time zone used to identify local dates and 7:00 AM.</param>
    /// <param name="completedDates">Local dates already completed, formatted as yyyy-MM-dd.</param>
    /// <returns>The next scheduled local date and UTC execution time.</returns>
    public static ScheduledRun GetNextRun(DateTimeOffset nowUtc, TimeZoneInfo timeZone, ISet<string> completedDates)
    {
        var localNow = TimeZoneInfo.ConvertTime(nowUtc, timeZone);
        var localDate = DateOnly.FromDateTime(localNow.DateTime);

        for (var offset = 0; offset < 3700; offset++)
        {
            var date = localDate.AddDays(offset);
            var localRun = date.ToDateTime(new TimeOnly(7, 0), DateTimeKind.Unspecified);
            // A nonexistent wall-clock time is advanced to the first valid local minute.
            while (timeZone.IsInvalidTime(localRun))
            {
                localRun = localRun.AddMinutes(1);
            }

            DateTimeOffset runUtc;
            if (timeZone.IsAmbiguousTime(localRun))
            {
                // The larger UTC offset maps this wall-clock time to the earlier of the two UTC instants.
                var earlierInstantOffset = timeZone.GetAmbiguousTimeOffsets(localRun).Max();
                runUtc = new DateTimeOffset(localRun, earlierInstantOffset).ToUniversalTime();
            }
            else
            {
                runUtc = TimeZoneInfo.ConvertTimeToUtc(localRun, timeZone);
            }

            if (completedDates.Contains(date.ToString("yyyy-MM-dd")))
            {
                continue;
            }

            if (offset == 0 && runUtc <= nowUtc)
            {
                // Catch up once for today instead of waiting an additional 24 hours after a late start.
                return new ScheduledRun(date, nowUtc);
            }

            if (runUtc > nowUtc)
            {
                return new ScheduledRun(date, runUtc);
            }
        }

        throw new InvalidOperationException("Could not calculate a scheduled broadcast run.");
    }
}