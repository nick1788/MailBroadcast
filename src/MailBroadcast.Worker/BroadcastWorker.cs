using MailBroadcast.Worker.Configuration;
using MailBroadcast.Worker.Services;
using Microsoft.Extensions.Options;

// File: BroadcastWorker.cs — long-running hosted service that waits for and executes daily broadcast batches.
namespace MailBroadcast.Worker;

/// <summary>Schedules the daily broadcast and delegates each due date to the batch processor.</summary>
public sealed class BroadcastWorker(
    IBroadcastStateStore stateStore,
    BroadcastProcessor processor,
    IOptions<BroadcastOptions> options,
    ILogger<BroadcastWorker> logger) : BackgroundService
{
    /// <summary>Waits for the next uncompleted local 7:00 AM date and processes it until shutdown.</summary>
    /// <param name="stoppingToken">Host cancellation token for delay and processing operations.</param>
    /// <returns>A task representing the hosted service lifetime.</returns>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var timeZone = TimeZoneInfo.FindSystemTimeZoneById(options.Value.TimeZoneId);
        if (options.Value.RunImmediately)
        {
            var localNow = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, timeZone);
            var runDate = DateOnly.FromDateTime(localNow.DateTime);
            logger.LogInformation("Immediate broadcast requested; processing for local date {RunDate}.", runDate);
            await processor.ProcessAsync(runDate, stoppingToken);
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            // Reload persisted completion dates before scheduling so a restart cannot repeat a finished date.
            var state = await stateStore.LoadAsync(stoppingToken);
            var scheduled = DailyScheduler.GetNextRun(DateTimeOffset.UtcNow, timeZone, state.CompletedDates);
            var delay = scheduled.AtUtc - DateTimeOffset.UtcNow;
            if (delay > TimeSpan.Zero)
            {
                logger.LogInformation("Next broadcast is scheduled for {LocalDate} at 7:00 AM ({TimeZoneId}).",
                    scheduled.Date, options.Value.TimeZoneId);
                await Task.Delay(delay, stoppingToken);
            }

            // ProcessAsync also checks durable completion to guard against duplicate dispatches.
            logger.LogInformation("Starting daily broadcast for scheduled date {RunDate}.", scheduled.Date);
            await processor.ProcessAsync(scheduled.Date, stoppingToken);
        }
    }
}