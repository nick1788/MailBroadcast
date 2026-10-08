using System.Net;
using System.Net.Mail;
using MailBroadcast.Worker.Configuration;
using Microsoft.Extensions.Options;

// File: SmtpEmailSender.cs — adapts configured SMTP settings to the shared email-sender contract.
namespace MailBroadcast.Worker.Services;

/// <summary>Sends plain-text or HTML email over SMTP using configured TLS and optional authentication.</summary>
public sealed class SmtpEmailSender(IOptions<SmtpOptions> options) : IEmailSender
{
    private readonly SmtpOptions _options = options.Value;

    /// <summary>Submits a message in the requested content format to the configured SMTP server.</summary>
    /// <param name="recipient">Destination email address.</param>
    /// <param name="subject">Message subject.</param>
    /// <param name="body">Message body, either plain text or HTML.</param>
    /// <param name="isHtml">True when the body contains HTML markup.</param>
    /// <param name="cancellationToken">Token used to cancel the SMTP operation.</param>
    /// <returns>A task that completes when SMTP reports successful submission.</returns>
    public async Task SendAsync(string recipient, string subject, string body, bool isHtml, CancellationToken cancellationToken)
    {
        using var message = new MailMessage(_options.SenderAddress, recipient, subject, body);
        message.IsBodyHtml = isHtml;
        using var client = new SmtpClient(_options.Host, _options.Port)
        {
            EnableSsl = _options.SecurityMode == "StartTls",
            DeliveryMethod = SmtpDeliveryMethod.Network,
            UseDefaultCredentials = false,
            Timeout = 100_000
        };
        // Use explicit credentials only when configured; never fall back to ambient machine credentials.
        if (!string.IsNullOrEmpty(_options.Username))
        {
            client.Credentials = new NetworkCredential(_options.Username, _options.Password);
        }

        await client.SendMailAsync(message, cancellationToken);
    }
}