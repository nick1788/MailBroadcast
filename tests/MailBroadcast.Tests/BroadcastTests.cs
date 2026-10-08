// File: BroadcastTests.cs — focused regression tests for scheduling, CSV validation, delivery limits, state, and reporting.
using MailBroadcast.Worker.Configuration;
using MailBroadcast.Worker.Models;
using MailBroadcast.Worker.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace MailBroadcast.Tests;

/// <summary>Verifies the core broadcast behaviors without connecting to an external SMTP service.</summary>
public sealed class BroadcastTests
{
    [Fact]
    /// <summary>Confirms the scheduler advances the UTC offset correctly across a daylight-saving transition.</summary>
    public void Scheduler_UsesSevenLocalTimeAndAdjustsAcrossDaylightSaving()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        var completed = new HashSet<string>(["2026-03-07"]);
        var next = DailyScheduler.GetNextRun(new DateTimeOffset(2026, 3, 7, 13, 0, 0, TimeSpan.Zero), zone, completed);

        Assert.Equal(new DateOnly(2026, 3, 8), next.Date);
        Assert.Equal(new DateTimeOffset(2026, 3, 8, 11, 0, 0, TimeSpan.Zero), next.AtUtc);
    }

    [Fact]
    /// <summary>Confirms quoted commas and multiline fields parse correctly while invalid and malformed rows are reported.</summary>
    public async Task CsvReader_ParsesQuotedFieldsAndReportsMalformedRows()
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path,
                "Campaign ID,Name,Email\r\n42,\"Doe, Jane\",jane@example.test\r\n43,\"Line 1\r\nLine 2\",multi@example.test\r\n44,Bad,not-an-email\r\n45,\"unclosed,bad@example.test");
            var result = await new CsvRecipientReader().ReadAsync(path, CancellationToken.None);

            Assert.Equal(2, result.Recipients.Count);
            Assert.Equal("Doe, Jane", result.Recipients[0].Name);
            Assert.Equal("Line 1\nLine 2", result.Recipients[1].Name);
            Assert.Equal(2, result.Issues.Count);
            Assert.Contains(result.Issues, issue => issue.Detail.Contains("invalid", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(result.Issues, issue => issue.Detail.Contains("not closed", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    /// <summary>Confirms the supported recipient values are substituted into an email template.</summary>
    public void Template_SubstitutesNameAndCampaignId()
    {
        var recipient = new CampaignRecipient("spring", "Ada", "ada@example.test");
        Assert.Equal("Hello Ada (spring)", EmailTemplate.Render("Hello {Name} ({CampaignId})", recipient));
    }

    [Fact]
    /// <summary>Confirms HTML rendering encodes recipient-provided values before inserting them into markup.</summary>
    public void Template_HtmlModeEncodesSubstitutions()
    {
        var recipient = new CampaignRecipient("<spring>", "Ada & Bob", "ada@example.test");
        Assert.Equal("<p>Ada &amp; Bob &lt;spring&gt;</p>",
            EmailTemplate.Render("<p>{Name} {CampaignId}</p>", recipient, isHtml: true));
    }

    [Fact]
    /// <summary>Confirms HTML mode chooses the HTML template and marks the campaign message as HTML for SMTP.</summary>
    public async Task Processor_HtmlModeUsesHtmlTemplateAndContentType()
    {
        var recipient = new CampaignRecipient("spring", "Ada & Bob", "ada@example.test");
        var sender = new RecordingSender();
        var processor = CreateProcessor([recipient], new MemoryStateStore(), sender,
            bodyFormat: "Html", htmlBodyTemplate: "<p>Hello {Name}: {CampaignId}</p>");

        await processor.ProcessAsync(new DateOnly(2026, 6, 1), CancellationToken.None);

        var message = Assert.Single(sender.RecipientMessages);
        Assert.True(message.IsHtml);
        Assert.Equal("<p>Hello Ada &amp; Bob: spring</p>", message.Body);
    }

    [Fact]
    /// <summary>Confirms a batch stops at 45 attempts, resumes deferred rows on a later day, and skips duplicate runs.</summary>
    public async Task Processor_EnforcesDailyCapRollsOverAndAvoidsSuccessfulDuplicates()
    {
        var recipients = Enumerable.Range(1, 46)
            .Select(number => new CampaignRecipient($"campaign-{number:00}", $"Person {number}", $"person{number}@example.test"))
            .ToArray();
        var sender = new RecordingSender();
        var store = new MemoryStateStore();
        var processor = CreateProcessor(recipients, store, sender);

        await processor.ProcessAsync(new DateOnly(2026, 1, 1), CancellationToken.None);
        Assert.Equal(45, sender.RecipientMessages.Count);
        Assert.Contains("Deferred due to daily attempt cap (1)", sender.ReportBodies.Single());

        await processor.ProcessAsync(new DateOnly(2026, 1, 2), CancellationToken.None);
        Assert.Single(sender.RecipientMessages, message => message.Recipient == "person46@example.test");
        Assert.Equal(46, sender.RecipientMessages.Count);

        await processor.ProcessAsync(new DateOnly(2026, 1, 2), CancellationToken.None);
        Assert.Equal(46, sender.RecipientMessages.Count);
    }

    [Fact]
    /// <summary>Confirms one recipient's SMTP failure does not block later recipients and appears in the admin report.</summary>
    public async Task Processor_ContinuesAfterFailureAndIncludesFailureInAdministratorReport()
    {
        var recipients = new[]
        {
            new CampaignRecipient("c1", "One", "one@example.test"),
            new CampaignRecipient("c2", "Two", "two@example.test")
        };
        var sender = new RecordingSender { FailingRecipient = "one@example.test" };
        var processor = CreateProcessor(recipients, new MemoryStateStore(), sender);

        await processor.ProcessAsync(new DateOnly(2026, 2, 1), CancellationToken.None);

        Assert.Equal(2, sender.RecipientMessages.Count);
        var report = Assert.Single(sender.ReportBodies);
        Assert.Contains("Result: Failure", report);
        Assert.Contains("temporary SMTP failure", report);
        Assert.Contains("Result: Success", report);
    }

    [Fact]
    /// <summary>Confirms persisted recipient results, attempt counts, and completed dates survive store reconstruction.</summary>
    public async Task JsonStateStore_PersistsAttemptsAndCompletionAcrossInstances()
    {
        var path = Path.Combine(Path.GetTempPath(), $"broadcast-state-{Guid.NewGuid():N}.json");
        try
        {
            var store = new JsonBroadcastStateStore(Options.Create(new BroadcastOptions { StatePath = path }));
            var state = new BroadcastState();
            state.AttemptsByDate["2026-04-01"] = 3;
            state.CompletedDates.Add("2026-03-31");
            state.Recipients["campaign\nUSER@EXAMPLE.TEST"] = new RecipientState
            {
                Succeeded = true,
                LastAttemptDate = "2026-04-01",
                LastAttemptSucceeded = true
            };
            await store.SaveAsync(state, CancellationToken.None);

            var reloaded = await new JsonBroadcastStateStore(Options.Create(new BroadcastOptions { StatePath = path }))
                .LoadAsync(CancellationToken.None);
            Assert.Equal(3, reloaded.AttemptsByDate["2026-04-01"]);
            Assert.Contains("2026-03-31", reloaded.CompletedDates);
            Assert.True(reloaded.Recipients["campaign\nUSER@EXAMPLE.TEST"].Succeeded);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData(" \t\r\n")]
    public async Task JsonStateStore_LoadsEmptyFileAsNewState(string contents)
    {
        var path = Path.Combine(Path.GetTempPath(), $"broadcast-state-{Guid.NewGuid():N}.json");
        try
        {
            await File.WriteAllTextAsync(path, contents);

            var state = await new JsonBroadcastStateStore(Options.Create(new BroadcastOptions { StatePath = path }))
                .LoadAsync(CancellationToken.None);

            Assert.Empty(state.Recipients);
            Assert.Empty(state.AttemptsByDate);
            Assert.Empty(state.CompletedDates);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task JsonStateStore_RejectsMalformedNonEmptyState()
    {
        var path = Path.Combine(Path.GetTempPath(), $"broadcast-state-{Guid.NewGuid():N}.json");
        try
        {
            await File.WriteAllTextAsync(path, "{ invalid");
            var store = new JsonBroadcastStateStore(Options.Create(new BroadcastOptions { StatePath = path }));

            await Assert.ThrowsAsync<System.Text.Json.JsonException>(() => store.LoadAsync(CancellationToken.None));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    /// <summary>Confirms failures retry the following day without resending recipients whose delivery succeeded.</summary>
    public async Task Processor_RetriesFailedRecipientOnLaterDayWithoutResendingSuccess()
    {
        var recipients = new[]
        {
            new CampaignRecipient("c1", "One", "one@example.test"),
            new CampaignRecipient("c2", "Two", "two@example.test")
        };
        var sender = new RecordingSender { FailFirstAttemptFor = "one@example.test" };
        var processor = CreateProcessor(recipients, new MemoryStateStore(), sender);

        await processor.ProcessAsync(new DateOnly(2026, 5, 1), CancellationToken.None);
        await processor.ProcessAsync(new DateOnly(2026, 5, 2), CancellationToken.None);

        Assert.Equal(2, sender.RecipientMessages.Count(message => message.Recipient == "one@example.test"));
        Assert.Single(sender.RecipientMessages, message => message.Recipient == "two@example.test");
    }

    /// <summary>Creates a processor with deterministic in-memory collaborators for isolated behavior tests.</summary>
    /// <param name="recipients">Rows returned by the fake recipient reader.</param>
    /// <param name="store">In-memory state store shared across processor invocations.</param>
    /// <param name="sender">Recording sender that simulates delivery success or failure.</param>
    /// <returns>A processor wired to test doubles and safe placeholder addresses.</returns>
    private static BroadcastProcessor CreateProcessor(IReadOnlyList<CampaignRecipient> recipients,
        MemoryStateStore store, RecordingSender sender, string bodyFormat = "PlainText", string htmlBodyTemplate = "")
    {
        var reader = new FixedReader(recipients);
        var broadcast = new BroadcastOptions
        {
            MaxAttemptsPerDay = 45,
            SubjectTemplate = "{CampaignId}: {Name}",
            BodyFormat = bodyFormat,
            HtmlBodyTemplate = htmlBodyTemplate
        };
        var smtp = new SmtpOptions { AdministratorAddress = "admin@example.test", Password = "secret" };
        return new BroadcastProcessor(reader, store, sender, Options.Create(broadcast), Options.Create(smtp),
            NullLogger<BroadcastProcessor>.Instance);
    }

    /// <summary>Test reader that always returns the supplied validated recipient list.</summary>
    private sealed class FixedReader(IReadOnlyList<CampaignRecipient> recipients) : IRecipientReader
    {
        /// <summary>Returns the fixed list without file I/O, honoring the production reader contract.</summary>
        /// <param name="path">Unused path accepted to match the reader interface.</param>
        /// <param name="cancellationToken">Cancellation token for interface compatibility.</param>
        /// <returns>The configured test recipients and no CSV issues.</returns>
        public Task<RecipientReadResult> ReadAsync(string path, CancellationToken cancellationToken) =>
            Task.FromResult(new RecipientReadResult(recipients, []));
    }

    /// <summary>Test state store that retains a mutable state object between processor calls.</summary>
    private sealed class MemoryStateStore : IBroadcastStateStore
    {
        private BroadcastState _state = new();

        /// <summary>Returns the current in-memory state snapshot.</summary>
        /// <param name="cancellationToken">Cancellation token for interface compatibility.</param>
        /// <returns>The current test state.</returns>
        public Task<BroadcastState> LoadAsync(CancellationToken cancellationToken) => Task.FromResult(_state);

        /// <summary>Replaces the in-memory state with the processor's latest snapshot.</summary>
        /// <param name="state">State snapshot to retain for subsequent calls.</param>
        /// <param name="cancellationToken">Cancellation token for interface compatibility.</param>
        /// <returns>A completed task.</returns>
        public Task SaveAsync(BroadcastState state, CancellationToken cancellationToken)
        {
            _state = state;
            return Task.CompletedTask;
        }
    }

    /// <summary>Test sender that records messages and can simulate recipient SMTP failures.</summary>
    private sealed class RecordingSender : IEmailSender
    {
        public List<(string Recipient, string Subject, string Body, bool IsHtml)> RecipientMessages { get; } = [];
        public List<string> ReportBodies { get; } = [];
        public string? FailingRecipient { get; init; }
        public string? FailFirstAttemptFor { get; init; }
        private readonly Dictionary<string, int> _attemptCounts = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Records each message and optionally throws for configured recipient attempts.</summary>
        /// <param name="recipient">Destination address; the admin destination is recorded separately as a report.</param>
        /// <param name="subject">Subject to record.</param>
        /// <param name="body">Body to record.</param>
        /// <param name="cancellationToken">Cancellation token for interface compatibility.</param>
        /// <returns>A completed task or a simulated SMTP exception.</returns>
        public Task SendAsync(string recipient, string subject, string body, bool isHtml, CancellationToken cancellationToken)
        {
            if (recipient == "admin@example.test")
            {
                ReportBodies.Add(body);
                return Task.CompletedTask;
            }

            // Record attempts before simulating failure so the fake reflects the processor's charged SMTP attempt.
            RecipientMessages.Add((recipient, subject, body, isHtml));
            var attempt = _attemptCounts.GetValueOrDefault(recipient) + 1;
            _attemptCounts[recipient] = attempt;
            if (recipient == FailingRecipient || (recipient == FailFirstAttemptFor && attempt == 1))
                throw new InvalidOperationException("temporary SMTP failure");
            return Task.CompletedTask;
        }
    }
}