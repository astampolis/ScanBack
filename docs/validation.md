# Validation record

Validated on 15 October 2026 (Europe/Athens) in the current cloud instance.

## Build and database

- .NET SDK 10.0.402 / runtime 10.0.13.
- Real SQL Server 2022, Microsoft engine package 16.0.4305.5-1, on Ubuntu 22.04 in a development container.
- Initial EF migration applied successfully. Application stop/restart and repeated setup succeeded without resetting data.
- Frozen NuGet restore and Debug build succeeded with zero warnings and errors.
- Release publish succeeded; generated SQL schema is in `docs/schema.sql`.
- NuGet's configured advisory sources reported no known vulnerable direct or transitive packages at validation time.

## Automated checks

`scripts/test.sh` executed **49 tests: 49 passed, 0 failed, 0 skipped**. This includes 11 unit tests and 38 real SQL Server/MVC integration cases. Each run creates/deletes a separate SQL database; production authentication, antiforgery and rate-limit behavior remain enabled.

The suite covers public-field exclusion, consent withdrawal, raw SQL encryption checks, owner isolation, profile photo processing/privacy, registration confirmation, export/deletion, contact verification/email changes, QR download/replacement, GPS validation and Greek decimal binding, duplicate/concurrent reports, scoped invitation expiry/revocation, deactivated accounts, admin metadata/settings, actual TOTP enrollment/login and recovery-code reuse rejection.

The current TRX report is under ignored `artifacts/test-results/scanback.trx`. Background delivery/retention workers are removed from the HTTP test host to keep queued-message assertions deterministic.

## Browser demonstration

Chromium/Playwright exercised:

1. Registration and real confirmation email received by the local Mailpit inbox.
2. Login, explicit health consent, selected public fields, and upload of a real PNG photo.
3. Contact creation and explicit email participation confirmation.
4. Sticker/QR creation and anonymous emergency-page access.
5. GPS requested only after a user gesture, with browser location permission mocked for the demonstration.
6. Accident reporting, actual incident email received by Mailpit, reporter incident access, and a separate contact invitation/session.
7. Provider-accepted notification status and an email containing unverified-report wording and a restricted invitation, without health contents or precise GPS.

Home, authentication, profile, stickers, emergency and incident pages were checked at 1440, 390 and 320 px widths. Final checks found no horizontal overflow or JavaScript/CSP errors. Mobile logout, the keyboard skip link and profile/emergency input labels were verified. Representative axe-core checks on home, registration and emergency pages at desktop/mobile widths found **zero serious or critical findings**; this is not a complete accessibility certification.

Token-free synthetic demonstration screenshots:

- [Desktop homepage](screenshots/home-desktop.png)
- [Mobile emergency page](screenshots/emergency-mobile.png)

## Reusable environment

The official-package SQL fallback verifies Microsoft's pinned signing key, signed repository metadata and package SHA256, retaining Ubuntu package verification. It was run twice; the generated image archive was checksum-verified and loaded again successfully. The install/start configuration has been saved for environment review, including persisted SQL files and Data Protection keys.

## Limits of this evidence

This validates the current local development instance. It does not establish production deployment, external email deliverability, a newly restored cloud task, legal/GDPR certification, or a full penetration test. Mailpit captures local email and does not send to real contacts. Live hosting, production SMTP, protected deployment keys, encryption/backups and operator/legal configuration must be completed before launch. The included Dockerfile and GitHub Actions workflow have not been executed on a production host or GitHub runner here.
