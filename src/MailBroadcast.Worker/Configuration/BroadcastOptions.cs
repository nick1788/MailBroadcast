// File: BroadcastOptions.cs — configurable schedule, recipient source, retry policy, and message templates.
namespace MailBroadcast.Worker.Configuration;

/// <summary>Defines settings that control campaign input, scheduling, daily limits, retries, and templates.</summary>
public sealed class BroadcastOptions
{
    /// <summary>Gets the configuration section name used to bind these settings.</summary>
    public const string SectionName = "Broadcast";

    /// <summary>Gets or sets the CSV file path containing campaign recipients.</summary>
    public string CsvPath { get; set; } = "data/recipients.csv";
    /// <summary>Gets or sets the durable JSON state file path.</summary>
    public string StatePath { get; set; } = "data/broadcast-state.json";
    /// <summary>Gets or sets the system time-zone ID used for the daily 7:00 AM schedule.</summary>
    public string TimeZoneId { get; set; } = "UTC";
    /// <summary>Gets or sets whether to run one broadcast immediately when the worker starts.</summary>
    public bool RunImmediately { get; set; }
    /// <summary>Gets or sets the maximum number of recipient send attempts allowed per day, capped at 45.</summary>
    public int MaxAttemptsPerDay { get; set; } = 45;
    /// <summary>Gets or sets whether failed deliveries can be retried on a later scheduled day.</summary>
    public bool RetryFailedNextDay { get; set; } = true;
    /// <summary>Gets or sets the subject template; supports {Name} and {CampaignId} placeholders.</summary>
    public string SubjectTemplate { get; set; } = "Your campaign update, {Name}";
    /// <summary>Gets or sets the campaign body format: PlainText or Html.</summary>
    public string BodyFormat { get; set; } = "PlainText";
    /// <summary>Gets or sets the plain-text body; supports {Name} and {CampaignId} placeholders.</summary>
    public string BodyTemplate { get; set; } = "Hi {Name},\n\nWhat if your Tally could work for you—even when you’re away from your desk?\n\nIntroducing Finclick360 — a smarter business platform designed by industry veterans.\n\n📱 Tally on Mobile — Access key business data and insights, anytime, anywhere.\n\n🔔 Predictive Alerts — Know what needs your attention before it becomes a problem.\n\n💰 Debtor Collection Universe — Track receivables and drive faster collections.\n\n📁 Document Vault — Keep critical business documents secure and accessible.\n\nTally visibility. Predictive intelligence. Faster collections. All in one place.\n\nReady to see Finclick360 in action?\n\nBook a 15-minute demo.\n\nRegards,\nVishal\nFinclick360";
    /// <summary>Gets or sets the HTML body for HTML mode; supports HTML-encoded {Name} and {CampaignId} placeholders.</summary>
    public string HtmlBodyTemplate { get; set; } = "";
}