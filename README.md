# Daily Email Broadcast Worker

A .NET 9 Worker Service that sends campaign messages at 7:00 AM in a configured time zone, with a hard limit of 45 recipient SMTP attempts per local calendar day.

## Configuration

Settings are read from `appsettings.json` and can be overridden with environment variables using the `Section__Property` convention. Configure these values in the service manager or deployment secret store; do not put SMTP credentials in source control or logs.

| Setting | Default | Description |
| --- | --- | --- |
| `Broadcast__CsvPath` | `data/recipients.csv` | Input CSV path, relative to the process working directory unless absolute. |
| `Broadcast__StatePath` | `data/broadcast-state.json` | Durable progress and daily-attempt state. Back this file up and restrict access. |
| `Broadcast__TimeZoneId` | `UTC` | Installed Windows or IANA time-zone ID for the 7:00 AM schedule. |
| `Broadcast__RunImmediately` | `false` | When `true`, runs one batch on worker startup, then resumes the normal 7:00 AM schedule. Turn it off after a test run. |
| `Broadcast__MaxAttemptsPerDay` | `45` | Configurable cap from 1 through 45; values above 45 are rejected. |
| `Broadcast__RetryFailedNextDay` | `true` | Failed sends are eligible for retry on a later scheduled day. `false` disables further retries. |
| `Broadcast__SubjectTemplate` | `Your campaign update, {Name}` | Subject supports `{Name}` and `{CampaignId}`. |
| `Broadcast__BodyFormat` | `PlainText` | Select `PlainText` or `Html` for campaign messages. |
| `Broadcast__BodyTemplate` | See `appsettings.json` | Plain-text campaign body supports `{Name}` and `{CampaignId}`. |
| `Broadcast__HtmlBodyTemplate` | *(empty)* | Required when `BodyFormat` is `Html`; provide HTML markup with `{Name}` and `{CampaignId}` placeholders. Inserted values are HTML-encoded. |
| `Smtp__Host` | *(required)* | SMTP server host. |
| `Smtp__Port` | `587` | SMTP server port. |
| `Smtp__SecurityMode` | `StartTls` | `StartTls` or `None`. `StartTls` negotiates TLS after connecting; implicit TLS is not supported by the built-in SMTP transport. |
| `Smtp__Username` / `Smtp__Password` | *(empty)* | Set both for authenticated SMTP; leave both empty for an unauthenticated relay. |
| `Smtp__SenderAddress` | *(required)* | From address. |
| `Smtp__AdministratorAddress` | *(required)* | Receives one batch report after each scheduled run. |

Example PowerShell environment setup (use your provider's actual values; enter secrets into a secure secret store in production):

```powershell
$env:Broadcast__TimeZoneId = 'Pacific Standard Time'
$env:Broadcast__CsvPath = 'D:\MailBroadcast\recipients.csv'
$env:Smtp__Host = 'smtp.example.test'
$env:Smtp__Port = '587'
$env:Smtp__SecurityMode = 'StartTls'
$env:Smtp__Username = 'REPLACE_WITH_SMTP_USERNAME'
$env:Smtp__Password = 'REPLACE_WITH_SMTP_PASSWORD'
$env:Smtp__SenderAddress = 'sender@example.test'
$env:Smtp__AdministratorAddress = 'admin@example.test'
```

The defaults intentionally contain no usable sender, administrator address, or SMTP host. The worker validates required SMTP settings at startup. Never commit real credentials or recipient data.

The configured plain-text body contains the Finclick360 campaign message and is sent as plain text by default. To use HTML, set `Broadcast__BodyFormat=Html` and provide the corresponding markup through `Broadcast__HtmlBodyTemplate`. The administrator's batch report remains plain text. The HTML body must provide its own markup and layout; recipient placeholder values are encoded before insertion.

## Recipient CSV

The first row must contain exactly these three headers (order is flexible): `Campaign Id` or `Campaign ID`, `Name`, and `Email`.

```csv
Campaign Id,Name,Email
spring-2026,"Doe, Jane",jane@example.test
spring-2026,Alex,alex@example.test
```

Quoted fields, escaped double quotes, commas, and quoted multiline fields are supported. Blank required values, invalid email addresses, malformed CSV records, and unreadable/missing files are included in the administrator report and structured logs. Rows retain CSV order. Repeated campaign-ID/email pairs within one CSV are sent only once.

## Scheduling, attempts, and durable state

- The first run occurs at the next 7:00 AM in the configured time zone. If the worker starts after 7:00 AM and that local date has not been completed, it catches up immediately. Completed scheduled dates are persisted to prevent a second run for that date.
- Set `Broadcast__RunImmediately` to `true` for a one-time startup run that does not wait for 7:00 AM; turn it off after testing. Durable state still prevents recipients already sent successfully from being sent again.
- Scheduling is based on local calendar dates and recalculated after each run, so daylight-saving changes are respected. If 7:00 AM is an invalid local time in a time zone, the run advances to the first valid minute; if it is ambiguous, the earlier instant is selected.
- At most 45 recipient SMTP send attempts are charged per local scheduled date. Attempts are persisted before contacting SMTP, so a restart cannot reset the cap. This is conservative: a process interruption between recording the attempt and actually contacting SMTP can consume a slot without a confirmed send.
- Successful campaign-ID/email pairs are durably recorded and not sent again. Failures consume an attempt and, by default, become retryable on a later scheduled day. Never-attempted rows are prioritized ahead of retries, preserving the backlog's place; within those groups, CSV order is retained. Set `RetryFailedNextDay` to `false` to stop retries.
- A cancellation during SMTP leaves the result marked unconfirmed and does not retry that recipient again on the same date. A resumed run reports the unconfirmed outcome and it can be retried on a later day.
- SMTP delivery and local JSON state cannot participate in one transaction. If SMTP accepts a message and the process stops before success is persisted, a later-day retry can duplicate that message. The state file minimizes, but cannot eliminate, this SMTP exactly-once limitation.
- Run one worker instance against a given state file. The JSON state is atomically replaced for crash resistance, but is not a distributed multi-instance lock or a multi-host database.
- A missing or empty state file starts with fresh progress. Invalid non-empty JSON fails startup rather than discarding delivery history.
- The summary includes run date/time, each attempted recipient and outcome, failure detail, invalid rows, and recipients deferred by the cap. A failed administrator email is logged; the scheduled date is still marked complete to avoid duplicate broadcasts. Review service logs and alert on report-delivery errors.

The state file contains recipient addresses and delivery metadata; secure it accordingly. It contains no SMTP password.

## Build, test, and run

From the repository root:

```powershell
dotnet test .\MailBroadcast.sln
dotnet run --project .\src\MailBroadcast.Worker\MailBroadcast.Worker.csproj -- --Broadcast:CsvPath=../../data/recipients.csv
```

The worker remains running between scheduled broadcasts. Stop it with the normal service shutdown signal; cancellation is propagated to delays and SMTP sends.