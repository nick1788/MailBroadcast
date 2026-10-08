// File: BroadcastModels.cs — immutable CSV/report records and serializable delivery progress models.
namespace MailBroadcast.Worker.Models;

/// <summary>Represents one validated campaign recipient row.</summary>
public sealed record CampaignRecipient(string CampaignId, string Name, string Email);

/// <summary>Describes a malformed or invalid CSV row using its physical starting line number.</summary>
public sealed record CsvIssue(int RowNumber, string Detail);

/// <summary>Contains the valid recipients and all row-level issues found while reading a CSV file.</summary>
public sealed record RecipientReadResult(IReadOnlyList<CampaignRecipient> Recipients, IReadOnlyList<CsvIssue> Issues);

/// <summary>Captures the outcome of one recipient delivery attempt.</summary>
public sealed record RecipientOutcome(CampaignRecipient Recipient, bool Success, string? Detail);

/// <summary>Stores durable campaign delivery state, per-date attempt totals, and completed run dates.</summary>
public sealed class BroadcastState
{
    /// <summary>Gets or sets recipient states indexed by normalized campaign and email identity.</summary>
    public Dictionary<string, RecipientState> Recipients { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Gets or sets SMTP recipient-attempt totals indexed by local scheduled date.</summary>
    public Dictionary<string, int> AttemptsByDate { get; set; } = new(StringComparer.Ordinal);
    /// <summary>Gets or sets dates for which batch processing and reporting have completed.</summary>
    public HashSet<string> CompletedDates { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>Persists a recipient's latest attempt and whether delivery has ever succeeded.</summary>
public sealed class RecipientState
{
    /// <summary>Gets or sets the campaign ID associated with this durable identity.</summary>
    public string? CampaignId { get; set; }
    /// <summary>Gets or sets the name used for the last attempted recipient.</summary>
    public string? Name { get; set; }
    /// <summary>Gets or sets the email address used for the last attempted recipient.</summary>
    public string? Email { get; set; }
    /// <summary>Gets or sets whether a campaign delivery has succeeded at least once.</summary>
    public bool Succeeded { get; set; }
    /// <summary>Gets or sets the local date of the most recent attempt.</summary>
    public string? LastAttemptDate { get; set; }
    /// <summary>Gets or sets the last attempt result; null means the SMTP outcome is unconfirmed.</summary>
    public bool? LastAttemptSucceeded { get; set; }
    /// <summary>Gets or sets a sanitized failure detail for the most recent unsuccessful attempt.</summary>
    public string? LastFailureDetail { get; set; }
}