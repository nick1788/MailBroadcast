// File: Program.cs — configures dependency injection, validates runtime settings, and starts the hosted worker.
using MailBroadcast.Worker;
using MailBroadcast.Worker.Configuration;
using MailBroadcast.Worker.Services;

// Build the generic host so appsettings, environment overrides, logging, DI, and shutdown are managed consistently.
var builder = Host.CreateApplicationBuilder(args);

// Fail fast on invalid broadcast configuration instead of silently running with an unsafe daily cap or timezone.
builder.Services.AddOptions<BroadcastOptions>()
    .BindConfiguration(BroadcastOptions.SectionName)
    .Validate(options => options.MaxAttemptsPerDay is > 0 and <= 45,
        "MaxAttemptsPerDay must be between 1 and 45.")
    .Validate(options => IsTimeZone(options.TimeZoneId),
        "TimeZoneId must name a time zone installed on this system.")
    .Validate(options => !string.IsNullOrWhiteSpace(options.CsvPath), "CsvPath is required.")
    .Validate(options => !string.IsNullOrWhiteSpace(options.StatePath), "StatePath is required.")
    .Validate(options => options.BodyFormat is "PlainText" or "Html", "BodyFormat must be PlainText or Html.")
    .Validate(options => options.BodyFormat != "Html" || !string.IsNullOrWhiteSpace(options.HtmlBodyTemplate),
        "HtmlBodyTemplate is required when BodyFormat is Html.")
    .ValidateOnStart();

// Validate SMTP destinations and security settings before any broadcast can be scheduled.
builder.Services.AddOptions<SmtpOptions>()
    .BindConfiguration(SmtpOptions.SectionName)
    .Validate(options => !string.IsNullOrWhiteSpace(options.Host), "Smtp:Host is required.")
    .Validate(options => options.Port is > 0 and <= 65535, "Smtp:Port must be between 1 and 65535.")
    .Validate(options => IsEmail(options.SenderAddress), "Smtp:SenderAddress must be a valid email address.")
    .Validate(options => IsEmail(options.AdministratorAddress), "Smtp:AdministratorAddress must be a valid email address.")
    .Validate(options => options.SecurityMode is "StartTls" or "None", "Smtp:SecurityMode must be StartTls or None.")
    .Validate(options => string.IsNullOrEmpty(options.Username) == string.IsNullOrEmpty(options.Password),
        "Smtp:Username and Smtp:Password must either both be set or both be empty.")
    .ValidateOnStart();

// Register shared stateless services and one state store/processor for the lifetime of this worker process.
builder.Services.AddSingleton<IBroadcastStateStore, JsonBroadcastStateStore>();
builder.Services.AddSingleton<IRecipientReader, CsvRecipientReader>();
builder.Services.AddSingleton<IEmailSender, SmtpEmailSender>();
builder.Services.AddSingleton<BroadcastProcessor>();
builder.Services.AddHostedService<BroadcastWorker>();

// RunAsync keeps the process alive for scheduled work and honors host shutdown signals.
await builder.Build().RunAsync();

/// <summary>Returns whether a value is a syntactically valid single email address.</summary>
/// <param name="value">Address value from SMTP configuration.</param>
/// <returns>True when the input parses and round-trips without modification.</returns>
static bool IsEmail(string value)
{
    try
    {
        return !string.IsNullOrWhiteSpace(value) && new System.Net.Mail.MailAddress(value).Address == value;
    }
    catch (FormatException)
    {
        return false;
    }
}

/// <summary>Checks whether a configured time-zone ID exists and its definition is valid on this host.</summary>
/// <param name="value">Time-zone ID supplied by configuration.</param>
/// <returns>True when the platform can resolve the time zone.</returns>
static bool IsTimeZone(string value)
{
    if (string.IsNullOrWhiteSpace(value)) return false;
    try
    {
        _ = TimeZoneInfo.FindSystemTimeZoneById(value);
        return true;
    }
    catch (TimeZoneNotFoundException)
    {
        return false;
    }
    catch (InvalidTimeZoneException)
    {
        return false;
    }
}