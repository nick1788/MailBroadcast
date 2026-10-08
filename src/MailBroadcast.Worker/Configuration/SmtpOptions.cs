// File: SmtpOptions.cs — SMTP connection and addressing settings, normally overridden with environment secrets.
namespace MailBroadcast.Worker.Configuration;

/// <summary>Defines SMTP transport, authentication, sender, and administrator-report settings.</summary>
public sealed class SmtpOptions
{
    /// <summary>Gets the configuration section name used to bind these settings.</summary>
    public const string SectionName = "Smtp";

    /// <summary>Gets or sets the SMTP server host name.</summary>
    public string Host { get; set; } = "mail.avencoree.com";
    /// <summary>Gets or sets the SMTP server port.</summary>
    public int Port { get; set; } = 587;
    /// <summary>Gets or sets the security mode: StartTls or None.</summary>
    public string SecurityMode { get; set; } = "StartTls";
    /// <summary>Gets or sets the SMTP authentication username.</summary>
    public string Username { get; set; } = "nitish.s@avencoree.com";
    /// <summary>Gets or sets the SMTP authentication password; never log this value.</summary>
    public string Password { get; set; } = "Yug@0102";
    /// <summary>Gets or sets the sender email address.</summary>
    public string SenderAddress { get; set; } = "noreply@avencoree.com";
    /// <summary>Gets or sets the administrator address that receives batch reports.</summary>
    public string AdministratorAddress { get; set; } = "hr@avencoree.com";
}