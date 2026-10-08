// File: IEmailSender.cs — transport-neutral email delivery contract for recipient and administrator messages.
namespace MailBroadcast.Worker.Services;

/// <summary>Defines the asynchronous operation used to send plain-text email.</summary>
public interface IEmailSender
{
    /// <summary>Sends one message in the selected body format and completes when the transport reports submission.</summary>
    /// <param name="recipient">Destination email address.</param>
    /// <param name="subject">Message subject.</param>
    /// <param name="body">Plain-text message body.</param>
    /// <param name="isHtml">True when the body contains HTML markup.</param>
    /// <param name="cancellationToken">Token used to cancel the send operation.</param>
    /// <returns>A task that completes after the email send operation.</returns>
    Task SendAsync(string recipient, string subject, string body, bool isHtml, CancellationToken cancellationToken);
}