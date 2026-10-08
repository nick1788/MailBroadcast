---
name: Build Daily Email Broadcast Worker
description: "Use the C# .NET Developer agent to implement a scheduled background worker that reads a recipient CSV, sends up to 45 campaign emails daily over SMTP, and emails an administrator a delivery report."
argument-hint: "Optional: specify timezone, admin address, SMTP provider, or message-template requirements"
agent: "C# .NET Developer"
---

Using the C# .NET Developer agent, inspect this repository and implement a production-minded .NET background worker service for daily campaign email broadcasts.

## Before implementation

Follow your required analysis workflow: inspect the repository, identify the target framework and existing patterns, state a concise plan and assumptions, then implement and verify. If the repository is empty, create a minimal, runnable .NET Worker Service with a clear structure and tests. Do not assume credentials, email addresses, or a timezone; make them configurable and use safe placeholders in examples.

## Requirements

- Run the broadcast automatically once per day at **7:00 AM**. Make the timezone configurable and document the default. Compute the next scheduled run correctly across daylight-saving changes. Avoid duplicate runs if the worker restarts or remains running across the scheduled time.
- Read campaign recipients from a configurable CSV file. It has a header row with three fields: `Campaign Id`, `Name`, and `Email` (also accept the common spelling `Campaign ID`). Handle quoted CSV fields correctly, validate required values and email addresses, and report malformed rows without silently dropping them.
- Send each recipient a standard, configurable email template. Support at least `{Name}` and `{CampaignId}` substitutions in subject/body. Use **SMTP** (assuming “sntp” in the request means SMTP), with host, port, TLS/security mode, username, password, sender address, and administrator address supplied through configuration/environment variables. Never commit or log credentials.
- Enforce a hard limit of **45 recipient send attempts per calendar day**. Process the input list in a deterministic order; if more than 45 eligible recipients remain, continue with the next recipients on the following day, preserving the daily cap. Keep campaign identity and processing state durable across restarts so already-successful recipients are not sent duplicate campaign emails.
- Define and document how failed attempts affect the daily cap and whether/how they are retried on a later day. Make the policy configurable where practical and ensure retries cannot cause more than 45 send attempts in a day. Do not mark a recipient successful unless the SMTP send operation succeeds.
- After each daily batch finishes, send one summary email to the configured administrator. Include the run date/time and per-recipient campaign ID, name, email, and result (`Success` or `Failure`); include useful failure detail without exposing secrets. Also report invalid CSV rows and recipients deferred because of the daily cap. Handle and log summary-email failures without concealing them.
- Add structured logging, cancellation handling, configuration validation, and sensible error handling for missing/unreadable CSV files and SMTP failures. Do not let one recipient's failure prevent processing the rest of that day's eligible batch.
- Add focused automated tests for scheduling/timezone behavior, the 45-recipient cap and rollover, CSV parsing/validation, durable state/duplicate prevention, template substitution, and per-recipient failure reporting. Use the repository's existing test framework and patterns.

## Acceptance criteria

1. The worker schedules a run at 7:00 AM in the configured timezone and runs no more than once for a given scheduled date, including after restart.
2. No daily run makes more than 45 SMTP send attempts; remaining eligible recipients are handled on later days without losing their place.
3. Successful campaign deliveries are not duplicated after a restart; failures and invalid rows are visible in the administrator report and logs.
4. The administrator receives one batch report after the run, with a success/failure outcome for each attempted recipient and a clear account of deferred/invalid rows.
5. Configuration and setup instructions explain how to set SMTP credentials securely, select the timezone, specify the CSV path, and run/test the service.
6. Build and relevant tests pass, or the final report identifies exactly what could not be run and why.

If an important policy choice cannot be safely inferred from the existing project, call it out before implementation; otherwise choose a conservative configurable default and document it. Keep the implementation proportional and avoid unrelated changes.