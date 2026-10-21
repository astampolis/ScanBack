# Validation record

Validated on **21 October 2026 (Europe/Athens)** in the cloud development environment.

## Build and database

- .NET SDK 10.0.402 / runtime 10.0.13; frozen NuGet restore and full Debug build passed with zero warnings/errors.
- Real SQL Server 2022, Microsoft engine package 16.0.4305.5-1, in a development container. Both EF migrations and explicit baseline initialization completed; repeated setup preserved existing settings.
- SQL connections used mandatory TLS, `TrustServerCertificate=false` and an exact server-certificate pin with Microsoft.Data.SqlClient 6.1.7. Production-host tests retained this actual connection policy, without replacing the DbContext or disabling TLS validation.
- The idempotent SQL script is in `docs/schema.sql`. New privacy migration preserves owner contact requests, clears legacy effective contact permissions and does not invent recipient consent.
- NuGet advisory checks reported no known vulnerable direct or transitive packages at validation time.

## Automated checks

The final `scripts/test.sh` run executed **149 tests: 149 passed, 0 failed, 0 skipped**. The current report is ignored `artifacts/test-results/scanback.trx`.

Coverage includes real SQL/HTTP registration and explicit email confirmation, profile visibility/consent withdrawal, encryption checks, owner isolation, secure photo processing, export/deletion, independent contact permissions and email changes, QR replacement/revocation, optional GPS validation, concurrent accident-report suppression, single-use incident invitations and concurrent redemption, admin blocking, TOTP enrollment/login and recovery-code reuse.

Production-specific checks start the actual Production web host with a real SQL login granted DML and explicitly denied DDL. Schema changes fail under that identity; the web starts, reads the emergency profile and readiness endpoint, creates no roles/settings/admin accounts and hosts no notification/retention workers. The suite also exercises actual SQL outbox leases/retries and authorization suppression, retention deletion, explicit deployment commands and idempotent backfill of 205 legacy Identity tokens.

SEO checks cover all eight bilingual public pages, GET/HEAD, canonical URLs built from configuration, reciprocal language alternates, structured JSON, exact public sitemap membership, redirects, private headers and exclusion of authenticated rider details. Public editorial HTML supports gzip; account/CSRF responses remain uncompressed and no-store. Public brand assets are crawlable and cacheable.

## Runtime and delivery smoke checks

The real development web host returned healthy database readiness. Public English/Greek landing pages and articles returned indexable responses; accounts retained noindex/no-store. Explicit deployment CLI SMTP testing delivered a synthetic configuration-test email into the local Mailpit inbox. This proves local provider acceptance/capture, not external deliverability. Development startup now initializes explicitly through Tools rather than applying migrations in the website.

## Windows deployment package

PowerShell deployment scripts parsed successfully. The framework-dependent win-x64 Web, Worker and Tools publish completed with frozen package locks and zero warnings/errors. The ZIP checksum, manifest hashes and Windows AMD64 executables were checked; private local configuration, key rings and uploaded data were excluded. Offline production CLI validation was exercised with a disposable protected PFX, including refusal of insecure configuration and refusal of unconfirmed production SMTP sending.

The scripts install IIS and a separate Windows worker with external configuration/data, restricted runtime identities and explicit operator-only migration/provisioning. Windows package CI is included. **Actual IIS installation, Windows service lifecycle and your production SQL/SMTP/DNS/certificates were not executed on this Linux host.** Complete the target acceptance procedure in [the IIS guide](iis-deployment.md).

## Browser evidence

Current bilingual marketing pages/articles were checked in Chromium at desktop 1440 px and mobile 390 px: correct URL language, one H1, parsed structured data and no horizontal overflow. The public layout emits no account details, antiforgery secrets or cookies.

The earlier 15 October MVP browser demonstration exercised registration/mail capture, profile/photo editing, contact participation, QR access, gesture-requested mocked GPS, reporting and incident email/invitation access. Existing synthetic screenshots below are from that earlier version; they are not new evidence for the changed consent/invitation screens. Those changes are covered by the current SQL/HTTP suite.

- [Earlier desktop homepage](screenshots/home-desktop.png)
- [Earlier mobile emergency page](screenshots/emergency-mobile.png)

## Limits

These checks do not establish live production deployment, external email receipt, fresh-task cloud restoration, Google indexing/ranking, legal/GDPR certification, complete accessibility certification or a penetration test. No live emergency calls or messages were made. Legal/operator notice approval, hosting configuration, provider agreements, encrypted backups and a verified restore remain deployment responsibilities.
