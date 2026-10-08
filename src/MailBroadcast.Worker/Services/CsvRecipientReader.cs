using System.Net.Mail;
using System.Text;
using MailBroadcast.Worker.Models;

// File: CsvRecipientReader.cs — parses RFC-style quoted CSV records and validates the broadcast's required columns.
namespace MailBroadcast.Worker.Services;

/// <summary>Reads CSV recipient rows, preserving valid records and reporting every malformed or invalid row.</summary>
public sealed class CsvRecipientReader : IRecipientReader
{
    /// <summary>Reads a CSV file, validates its header and recipient values, and returns issues without dropping them silently.</summary>
    /// <param name="path">CSV file path to read.</param>
    /// <param name="cancellationToken">Token checked during asynchronous reading and row validation.</param>
    /// <returns>Validated recipients plus header, parsing, and validation issues.</returns>
    public async Task<RecipientReadResult> ReadAsync(string path, CancellationToken cancellationToken)
    {
        // Read asynchronously so a large file does not block the worker's hosting thread.
        var text = await File.ReadAllTextAsync(path, cancellationToken);
        var records = ParseRecords(text);
        var issues = new List<CsvIssue>();
        var recipients = new List<CampaignRecipient>();
        if (records.Count == 0)
        {
            return new RecipientReadResult(recipients, [new CsvIssue(1, "CSV is empty; the required header row is missing.")]);
        }

        var header = records[0];
        if (header.Error is not null)
        {
            issues.Add(new CsvIssue(header.Line, $"Malformed header: {header.Error}"));
            return new RecipientReadResult(recipients, issues);
        }

        // Resolve columns by name so the CSV may reorder its three required fields.
        var headers = header.Fields.Select((value, index) => (Name: value.Trim(), Index: index)).ToArray();
        var campaignIndex = FindHeader(headers, "Campaign Id", "Campaign ID");
        var nameIndex = FindHeader(headers, "Name");
        var emailIndex = FindHeader(headers, "Email");
        if (headers.Length != 3 || campaignIndex < 0 || nameIndex < 0 || emailIndex < 0)
        {
            issues.Add(new CsvIssue(header.Line, "Header must contain exactly Campaign Id (or Campaign ID), Name, and Email."));
            return new RecipientReadResult(recipients, issues);
        }

        foreach (var record in records.Skip(1))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (record.Error is not null)
            {
                issues.Add(new CsvIssue(record.Line, record.Error));
                continue;
            }
            if (record.Fields.Length != 3)
            {
                issues.Add(new CsvIssue(record.Line, $"Expected 3 fields but found {record.Fields.Length}."));
                continue;
            }

            var campaignId = record.Fields[campaignIndex].Trim();
            var name = record.Fields[nameIndex].Trim();
            var email = record.Fields[emailIndex].Trim();
            // Validate all required fields together so one malformed row produces a useful consolidated issue.
            var errors = new List<string>();
            if (campaignId.Length == 0) errors.Add("Campaign Id is required");
            if (name.Length == 0) errors.Add("Name is required");
            if (!IsValidEmail(email)) errors.Add("Email is missing or invalid");
            if (errors.Count > 0)
            {
                issues.Add(new CsvIssue(record.Line, string.Join("; ", errors) + "."));
                continue;
            }

            recipients.Add(new CampaignRecipient(campaignId, name, email));
        }

        return new RecipientReadResult(recipients, issues);
    }

    /// <summary>Splits CSV text into logical records while supporting escaped quotes and multiline quoted fields.</summary>
    /// <param name="text">Complete CSV document text.</param>
    /// <returns>Records with their physical starting line, parsed fields, and any syntax error.</returns>
    internal static IReadOnlyList<CsvRecord> ParseRecords(string text)
    {
        var records = new List<CsvRecord>();
        var fields = new List<string>();
        var field = new StringBuilder();
        var line = 1;
        var recordLine = 1;
        var inQuotes = false;
        var closedQuote = false;
        string? error = null;

        // EndField moves the current field into the record and resets quote-closure state for the next field.
        void EndField() { fields.Add(field.ToString()); field.Clear(); closedQuote = false; }

        // FinishRecord stores one logical CSV record, including any syntax error detected while parsing it.
        void EndRecord()
        {
            EndField();
            records.Add(new CsvRecord(recordLine, fields.ToArray(), error));
            fields.Clear();
            error = null;
            recordLine = line + 1;
        }

        for (var index = 0; index < text.Length; index++)
        {
            var current = text[index];
            if (inQuotes)
            {
                if (current == '"')
                {
                    // CSV escapes an embedded quote by doubling it; a single quote closes the quoted field.
                    if (index + 1 < text.Length && text[index + 1] == '"')
                    {
                        field.Append('"');
                        index++;
                    }
                    else
                    {
                        inQuotes = false;
                        closedQuote = true;
                    }
                }
                else
                {
                    if (current == '\r' && index + 1 < text.Length && text[index + 1] == '\n') index++;
                    if (current is '\r' or '\n') line++;
                    field.Append(current == '\r' ? '\n' : current);
                }
                continue;
            }

            if (current == ',')
            {
                EndField();
            }
            else if (current is '\r' or '\n')
            {
                // Treat CRLF as one physical newline so row numbers remain accurate on Windows CSV files.
                if (current == '\r' && index + 1 < text.Length && text[index + 1] == '\n') index++;
                EndRecord();
                line++;
                recordLine = line;
            }
            else if (current == '"')
            {
                if (field.Length == 0 && !closedQuote) inQuotes = true;
                else
                {
                    error ??= "Quote found inside an unquoted field.";
                    field.Append(current);
                    closedQuote = false;
                }
            }
            else
            {
                if (closedQuote)
                {
                    // A closing quote may only be followed by a delimiter or record ending.
                    error ??= "Unexpected character after a quoted field.";
                    closedQuote = false;
                }
                field.Append(current);
            }
        }

        // An open quote at EOF makes only its containing record invalid; report it instead of losing the row.
        if (inQuotes) error ??= "Quoted field is not closed before end of file.";
        if (fields.Count > 0 || field.Length > 0 || error is not null)
        {
            EndRecord();
        }
        return records;
    }

    /// <summary>Finds the index of the first header matching one of the accepted names, case-insensitively.</summary>
    /// <param name="headers">Parsed header names and their positions.</param>
    /// <param name="acceptedNames">Allowed spellings for the requested column.</param>
    /// <returns>The matching field index, or -1 when no name matches.</returns>
    private static int FindHeader((string Name, int Index)[] headers, params string[] acceptedNames)
    {
        foreach (var header in headers)
        {
            if (acceptedNames.Any(name => string.Equals(header.Name, name, StringComparison.OrdinalIgnoreCase)))
                return header.Index;
        }
        return -1;
    }

    /// <summary>Checks that a nonempty email value parses as a single address without normalization changes.</summary>
    /// <param name="value">Candidate email value from a CSV field.</param>
    /// <returns>True when the value is a valid address.</returns>
    private static bool IsValidEmail(string value)
    {
        try { return value.Length > 0 && new MailAddress(value).Address == value; }
        catch (FormatException) { return false; }
    }

    /// <summary>Represents one logical CSV record and any parser error associated with it.</summary>
    internal sealed record CsvRecord(int Line, string[] Fields, string? Error);
}