using System.Globalization;
using MailBroadcast.Worker.Configuration;
using MailBroadcast.Worker.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

// File: BroadcastProcessor.cs — selects the daily batch, sends recipient messages, persists outcomes, and emails the report.
namespace MailBroadcast.Worker.Services;

/// <summary>Coordinates one scheduled broadcast date while enforcing durable deduplication and attempt limits.</summary>
public sealed class BroadcastProcessor(
    IRecipientReader reader,
    IBroadcastStateStore stateStore,
    IEmailSender emailSender,
    IOptions<BroadcastOptions> broadcastOptions,
    IOptions<SmtpOptions> smtpOptions,
    ILogger<BroadcastProcessor> logger)
{
    private readonly BroadcastOptions _options = broadcastOptions.Value;

    /// <summary>Processes one local scheduled date, including recipient sends, CSV issues, deferrals, and reporting.</summary>
    /// <param name="runDate">The local calendar date assigned to this scheduled run.</param>
    /// <param name="cancellationToken">Token used to cancel reading, sends, and persistence operations.</param>
    /// <returns>A task representing the complete batch processing operation.</returns>
    public async Task ProcessAsync(DateOnly runDate, CancellationToken cancellationToken)
    {
        var dateKey = runDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        // Load state before reading or sending: both daily caps and duplicate prevention depend on durable history.
        var state = await stateStore.LoadAsync(cancellationToken);
        if (state.CompletedDates.Contains(dateKey))
        {
            logger.LogInformation("Broadcast for {RunDate} is already complete; skipping duplicate run.", dateKey);
            return;
        }

        RecipientReadResult readResult;
        try
        {
            readResult = await reader.ReadAsync(_options.CsvPath, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            logger.LogError(exception, "Unable to read recipient CSV at {CsvPath}.", _options.CsvPath);
            readResult = new RecipientReadResult([], [new CsvIssue(0, $"CSV could not be read: {exception.Message}")]);
        }

        foreach (var issue in readResult.Issues)
        {
            logger.LogWarning("Invalid CSV row {RowNumber}: {Detail}", issue.RowNumber, issue.Detail);
        }

        // Keep valid CSV rows in their deterministic input order and distinguish retries from first-time backlog.
        var eligible = new List<(string Key, CampaignRecipient Recipient, bool IsRetry)>();
        var outcomes = new List<RecipientOutcome>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var recipient in readResult.Recipients)
        {
            var key = GetKey(recipient);
            if (!seen.Add(key)) continue;

            state.Recipients.TryGetValue(key, out var saved);
            if (saved?.LastAttemptDate == dateKey)
            {
                var detail = saved.LastAttemptSucceeded == false
                    ? saved.LastFailureDetail ?? "SMTP outcome was interrupted and could not be confirmed."
                    : saved.LastAttemptSucceeded is null && !saved.Succeeded
                        ? "SMTP outcome was interrupted and could not be confirmed."
                        : null;
                outcomes.Add(new RecipientOutcome(recipient, saved.Succeeded, detail));
                continue;
            }
            if (saved?.Succeeded == true) continue;
            if (saved?.LastAttemptDate is not null && !_options.RetryFailedNextDay) continue;
            eligible.Add((key, recipient, saved?.LastAttemptDate is not null));
        }

        // Include attempts from an interrupted same-date run even if the current CSV no longer contains those rows.
        var reportedKeys = outcomes.Select(outcome => GetKey(outcome.Recipient))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, saved) in state.Recipients)
        {
            if (saved.LastAttemptDate != dateKey || saved.CampaignId is null || saved.Name is null
                || saved.Email is null || !reportedKeys.Add(key))
                continue;

            var recipient = new CampaignRecipient(saved.CampaignId, saved.Name, saved.Email);
            var detail = saved.LastAttemptSucceeded == false
                ? saved.LastFailureDetail ?? "SMTP outcome was interrupted and could not be confirmed."
                : saved.LastAttemptSucceeded is null && !saved.Succeeded
                    ? "SMTP outcome was interrupted and could not be confirmed."
                    : null;
            outcomes.Add(new RecipientOutcome(recipient, saved.Succeeded, detail));
        }

        // Give deferred first-time recipients their place in the queue before retrying failures.
        eligible = eligible.OrderBy(item => item.IsRetry).ToList();
        // Charge already-reserved attempts before choosing this batch so restarts cannot exceed the configured cap.
        var attemptsSoFar = state.AttemptsByDate.GetValueOrDefault(dateKey);
        var remaining = Math.Max(0, _options.MaxAttemptsPerDay - attemptsSoFar);
        var batch = eligible.Take(remaining).ToArray();
        var deferred = eligible.Skip(batch.Length).Select(item => item.Recipient).ToArray();

        foreach (var (key, recipient, _) in batch)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var recipientState = state.Recipients.GetValueOrDefault(key) ?? new RecipientState();
            recipientState.CampaignId = recipient.CampaignId;
            recipientState.Name = recipient.Name;
            recipientState.Email = recipient.Email;
            recipientState.LastAttemptDate = dateKey;
            recipientState.LastAttemptSucceeded = null;
            recipientState.LastFailureDetail = null;
            state.Recipients[key] = recipientState;
            // Persist the attempt reservation before SMTP: conservative accounting protects the hard daily limit on crash.
            state.AttemptsByDate[dateKey] = state.AttemptsByDate.GetValueOrDefault(dateKey) + 1;
            await stateStore.SaveAsync(state, cancellationToken);

            try
            {
                var subject = EmailTemplate.Render(_options.SubjectTemplate, recipient);
                var isHtml = _options.BodyFormat == "Html";
                var bodyTemplate = isHtml ? _options.HtmlBodyTemplate : _options.BodyTemplate;
                var body = EmailTemplate.Render(bodyTemplate, recipient, isHtml);
                await emailSender.SendAsync(recipient.Email, subject, body, isHtml, cancellationToken);
                recipientState.Succeeded = true;
                recipientState.LastAttemptSucceeded = true;
                outcomes.Add(new RecipientOutcome(recipient, true, null));
                logger.LogInformation("Campaign {CampaignId} delivered to {Email}.", recipient.CampaignId, recipient.Email);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                await stateStore.SaveAsync(state, CancellationToken.None);
                throw;
            }
            catch (Exception exception)
            {
                // Sanitize provider exception text before storing, logging, or emailing it to avoid leaking credentials.
                var detail = Redact(exception.Message, smtpOptions.Value.Username, smtpOptions.Value.Password);
                recipientState.LastAttemptSucceeded = false;
                recipientState.LastFailureDetail = detail;
                outcomes.Add(new RecipientOutcome(recipient, false, detail));
                logger.LogError("Campaign {CampaignId} failed for {Email}: {FailureDetail}", recipient.CampaignId,
                    recipient.Email, detail);
            }

            await stateStore.SaveAsync(state, cancellationToken);
        }

        // Reporting happens once after all selected recipients have been processed; report-send errors are logged below.
        var report = BuildReport(runDate, outcomes, deferred, readResult.Issues);
        try
        {
            await emailSender.SendAsync(smtpOptions.Value.AdministratorAddress,
                $"Daily campaign broadcast report — {dateKey}", report, false, cancellationToken);
            logger.LogInformation("Administrator report sent for {RunDate}.", dateKey);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError("Administrator report failed for {RunDate}: {FailureDetail}", dateKey,
                Redact(exception.Message, smtpOptions.Value.Username, smtpOptions.Value.Password));
        }

        // Complete the date even if the admin email failed, preventing recipient messages from being sent twice.
        state.CompletedDates.Add(dateKey);
        await stateStore.SaveAsync(state, cancellationToken);
    }

    /// <summary>Builds a stable, case-insensitive key for a campaign and recipient email pair.</summary>
    /// <param name="recipient">Recipient identity to normalize.</param>
    /// <returns>A deterministic key used for durable success and duplicate checks.</returns>
    private static string GetKey(CampaignRecipient recipient) =>
        $"{recipient.CampaignId.Trim().ToUpperInvariant()}\n{recipient.Email.Trim().ToUpperInvariant()}";

    /// <summary>Replaces configured secret values in an exception detail before it is persisted or reported.</summary>
    /// <param name="detail">Untrusted error text from a transport or provider.</param>
    /// <param name="secrets">Credential values that must not appear in logs or reports.</param>
    /// <returns>Sanitized failure detail.</returns>
    private static string Redact(string detail, params string[] secrets)
    {
        foreach (var secret in secrets)
        {
            if (!string.IsNullOrEmpty(secret))
                detail = detail.Replace(secret, "[redacted]", StringComparison.Ordinal);
        }
        return detail;
    }

    /// <summary>Formats the administrator's plain-text summary for attempted, deferred, and invalid rows.</summary>
    /// <param name="runDate">Local date assigned to the broadcast run.</param>
    /// <param name="outcomes">Per-recipient results for attempts made or recovered from same-day state.</param>
    /// <param name="deferred">Eligible recipients postponed because the daily cap was reached.</param>
    /// <param name="issues">Malformed or invalid CSV rows encountered during the run.</param>
    /// <returns>Human-readable report body.</returns>
    private static string BuildReport(DateOnly runDate, IReadOnlyList<RecipientOutcome> outcomes,
        IReadOnlyList<CampaignRecipient> deferred, IReadOnlyList<CsvIssue> issues)
    {
        var report = new System.Text.StringBuilder()
            .AppendLine($"Broadcast run date: {runDate:yyyy-MM-dd}")
            .AppendLine($"Report generated (UTC): {DateTimeOffset.UtcNow:O}")
            .AppendLine($"Recipient send attempts: {outcomes.Count}")
            .AppendLine()
            .AppendLine("Recipient results:");

        if (outcomes.Count == 0) report.AppendLine("None.");
        foreach (var outcome in outcomes)
        {
            report.AppendLine($"- Campaign: {outcome.Recipient.CampaignId}; Name: {outcome.Recipient.Name}; Email: {outcome.Recipient.Email}; Result: {(outcome.Success ? "Success" : "Failure")}{(outcome.Detail is null ? "" : $"; Detail: {outcome.Detail}")}");
        }

        report.AppendLine().AppendLine($"Deferred due to daily attempt cap ({deferred.Count}):");
        foreach (var recipient in deferred)
            report.AppendLine($"- Campaign: {recipient.CampaignId}; Name: {recipient.Name}; Email: {recipient.Email}");

        report.AppendLine().AppendLine($"Invalid CSV rows ({issues.Count}):");
        foreach (var issue in issues)
            report.AppendLine($"- Row {issue.RowNumber}: {issue.Detail}");

        return report.ToString();
    }
}