# ScanBack

A Greek/English, mobile-first motorcycle emergency identification MVP built with **ASP.NET Core MVC / .NET 10 LTS**, **SQL Server**, **Entity Framework Core**, and **ASP.NET Core Identity**. The frontend uses **Bootstrap (MIT)** and custom CSS: no paid UI license, Node build, React, Angular, or mobile app is required.

The sticker stores an HTTPS URL, never medical information. Anyone with a copy of that URL can see the fields the rider deliberately publishes. ScanBack does not dispatch emergency services, verify accidents, or diagnose injuries.

## What is implemented

- Registration, email confirmation, login, password reset, lockout, authenticator MFA and recovery codes.
- Rider profile editing, explicit health-data consent, per-field public visibility, public preview, secure optional JPEG/PNG photos, consent withdrawal, JSON data export, and password-confirmed account deletion.
- Emergency contacts with confirmation before notifications, private email addresses, optional public phone numbers, and unsubscribe links.
- Multiple QR/NFC identifiers (up to ten), labels, activation/deactivation, permanent replacement, SVG/PNG downloads, and printable sticker sheets.
- Anonymous emergency pages, prominent 112/166 dialing, and Greek/English safety reminders.
- Explicit possible-accident reporting, optional permission-based GPS, manual landmarks, duplicate suppression, notification cooldowns, and per-source limits.
- Incident-specific reporter sessions and separate expiring contact invitations, restricted location access, Google Maps links, email delivery status, and a rule-based dispatcher script.
- Persistent encrypted notification outbox, SMTP with verified TLS for remote providers, bounded retries, and automatic incident/outbox/audit cleanup.
- MFA-protected administration of account/identifier status, limited incident metadata, delivery configuration, retention, and technical audit events. Administrators do not get medical-profile access.

Optional SignalR chat, SMS, external AI, payments, physical sticker fulfilment, and extended incident reporting are **not part of this mandatory MVP**. No external AI receives health data.

## Local development

Requires Docker, Python 3, Bash and ripgrep for the helper scripts. The pinned .NET SDK can be installed by `scripts/install.sh`. Windows developers can use WSL2 or the manual .NET commands below.

```bash
git clone https://github.com/astampolis/ScanBack.git
cd ScanBack
./scripts/install.sh
./scripts/dev-up.sh
./scripts/run.sh
```

`dev-up.sh` starts SQL Server 2022 Developer edition and Mailpit, creates a random **local-only** SQL password, and writes ignored private configuration. It does not overwrite existing configuration. The application listens at `http://localhost:5080`; the local Mailpit inbox is at `http://localhost:8025`. Confirm registration and contact emails using that inbox. The mail catcher never sends mail to external recipients.

If MCR image storage is restricted in a cloud environment, see [the verified SQL image fallback](docs/sql-server-development.md). Use the current checkout; a cloud task is already isolated and does not need another Git worktree.

With an existing .NET 10 SDK and SQL Server, configure `ConnectionStrings__DefaultConnection`, `Site__BaseUrl`, and the mail settings instead, then:

```bash
dotnet restore ScanBack.sln --locked-mode
dotnet tool restore
ASPNETCORE_ENVIRONMENT=Development dotnet run --project src/ScanBack.Web
```

Development mode applies EF migrations on startup. Data Protection keys and uploaded photos are in ignored `src/ScanBack.Web/App_Data/` directories. Preserve the keys together with the database; losing them makes encrypted medical fields, notification bodies and stored sticker tokens unreadable. Local key files are suitable for development; production requires certificate protection.

## Run the tests

```bash
./scripts/test.sh
```

The tests use a new SQL Server database per run and delete that database afterward. The SQL login must be allowed to create/drop **test databases**. They do not use a mocked or SQLite database. Only the notification and retention background workers are removed from the HTTP test host, keeping queued-mail assertions deterministic; production authentication and rate limits remain enabled.

The test connection is read from `TestConnectionString`, or the JSON file named by `ScanBackTestConfig`. `dev-up.sh` prepares `.runtime/test-config.json`. In this cloud instance the file is outside the checkout at `/workspace/scanback-runtime/test-config.json`:

```bash
ScanBackTestConfig=/workspace/scanback-runtime/test-config.json ./scripts/test.sh
```

Missing SQL prerequisites fail the tests explicitly. Test reports are generated under ignored `artifacts/test-results/`.

## Demonstrate the complete flow

1. Register an account. Open the confirmation email in the local inbox and confirm it.
2. Sign in, edit the emergency profile, grant health-data consent if storing medical fields, and select only the information strangers should see.
3. Add an emergency contact. Open its confirmation email and explicitly accept notifications.
4. Create a helmet sticker, download its QR code, and open the sticker URL in another browser/private window. The same URL is the NFC payload.
5. Verify the public information and the 112/166 dialing links. These links initiate an ordinary phone call on compatible devices.
6. Report a possible accident. Use the location button only when at the incident; denied/unavailable GPS does not block reporting.
7. View the reporter's incident page, location and dispatcher script. Read the contact notification in the local inbox, accept its invitation in another session, and view that incident.
8. Revoke/replace the sticker and verify the old URL and associated incident access stop working.

No live emergency-service calls are needed for this demonstration. Use synthetic profile/contact/location data.

See [validation results and demonstration screenshots](docs/validation.md) for the flows exercised in the cloud instance.

## Administrator provisioning

There is no hardcoded administrator password. Supply a strong password and email via secure environment configuration and explicitly provision:

```bash
ASPNETCORE_ENVIRONMENT=Development dotnet run --project src/ScanBack.Web -- --provision-admin
```

This reads `Admin__Email` and `Admin__Password` from the environment; do not enter real values into shell history or commit them. Remove those bindings after provisioning. Existing accounts are not silently elevated or given a new password.

Sign in with the administrator account, open Account security, enroll a TOTP authenticator, save the one-time recovery codes, and sign in again with MFA. `/admin` requires both the role and an MFA-authenticated session. Self-deactivation and removal of the last active administrator are prevented.

## Configuration and production

[`.env.example`](.env.example) lists configuration names and non-secret placeholders. ASP.NET does not automatically load dotenv files; provide them through your hosting configuration or a supported secret manager.

Production requires:

- A real HTTPS `Site__BaseUrl`, matching `AllowedHosts`, TLS termination and explicitly trusted reverse proxies.
- SQL Server/Azure SQL with a least-privilege application login, verified encrypted connections, encrypted storage/backups, and a tested recovery procedure. The application rejects production connections with certificate validation disabled.
- A protected, persistent key directory and `Encryption__CertificatePath` plus its securely injected password. Store key backups separately and restrict access.
- A transactional SMTP provider. Remote SMTP uses STARTTLS (or implicit TLS on port 465) with normal certificate validation. Configure SPF, DKIM and DMARC at the provider/domain. A local Mailpit acceptance is not evidence of real-world deliverability.
- Explicit migration execution under a separate deployment identity (`--migrate`) before running the production application. Production startup does not automatically apply migrations.
- Suitable incident/access/cooldown settings, monitoring, backups, privacy notices, operator contact information and legal review.

The included Dockerfile is a production packaging example; live hosting, DNS, TLS certificates, provider accounts and legal approval must be configured for your deployment. No live production site is published by this repository.

## Architecture and database

`ScanBack.Domain` contains the entities and visibility flags. `ScanBack.Infrastructure` contains Identity/EF persistence, protected-token handling, field encryption, SMTP delivery and background workers. `ScanBack.Web` contains MVC controllers, validated input models, explicit public projections and Razor UI. `ScanBack.Tests` exercises real SQL persistence and HTTP security boundaries.

EF migrations are in `src/ScanBack.Infrastructure/Migrations`. The initial migration creates Identity tables and `RiderProfiles`, `EmergencyContacts`, `EmergencyIdentifiers`, `Incidents`, `IncidentAccesses`, `OutboxMessages`, `ConsentRecords`, `AuditEvents`, and singleton `PlatformSettings`. Unique token/submission indexes and coordinate constraints reinforce application checks.

```bash
dotnet ef migrations add YourChange --project src/ScanBack.Infrastructure --startup-project src/ScanBack.Web
dotnet ef migrations script --idempotent --project src/ScanBack.Infrastructure --startup-project src/ScanBack.Web --output artifacts/migrations.sql
```

## Privacy and delivery limits

See [the security model](docs/security.md) and the in-app privacy notice. The notice is a technical draft requiring your legal/operator review before launch. Explicit consent and technical safeguards are not GDPR certification.

Email delivery is at least once: a retry after an uncertain SMTP outcome can produce a duplicate. Status says **provider accepted**, never “contact informed” or “help dispatched.” Invitation possession authorizes access and can be forwarded; expiration/revocation limits that risk but cannot verify the recipient's real-world identity.

Application source is editable under the repository license. Third-party libraries retain their licenses; see [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
