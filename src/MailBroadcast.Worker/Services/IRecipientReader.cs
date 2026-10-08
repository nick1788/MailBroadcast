using MailBroadcast.Worker.Models;

// File: IRecipientReader.cs — abstraction for validated recipient-list input.
namespace MailBroadcast.Worker.Services;

/// <summary>Defines an asynchronous reader that returns recipients and explicit row-level validation issues.</summary>
public interface IRecipientReader
{
    /// <summary>Reads and validates recipient records from the configured source file.</summary>
    /// <param name="path">CSV file path to read.</param>
    /// <param name="cancellationToken">Token used to cancel file reading and validation.</param>
    /// <returns>Valid recipients and all malformed or invalid row details.</returns>
    Task<RecipientReadResult> ReadAsync(string path, CancellationToken cancellationToken);
}