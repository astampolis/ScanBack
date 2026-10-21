# Privacy operations

## Controller decisions before processing real data

Configure Privacy:ControllerName, Privacy:ContactEmail and Privacy:PolicyVersion with the actual operator and a monitored address. Production startup rejects missing values and deployment placeholders. The /privacy notice displays these values and the current incident access/retention settings. Privacy:HealthConsentTextPublic supplies the affirmative health wording; keep the public-link disclosure, optional nature and withdrawal route explicit. Keep a dated copy of every notice and consent wording under its immutable policy version. Do not reuse a version for materially changed purposes or recipients.

The notice describes account/service processing under Article 6(1)(b), optional health processing under Articles 6(1)(a) and 9(2)(a), and security/abuse controls under Article 6(1)(f). These require the controller's assessment for its actual service and jurisdiction. Separately assess bystander reports, contact invitations and unregistered individuals, legitimate-interest necessity/balancing, age/guardian requirements and emergency disclosures. Consent to public access must be freely given and understandable: anyone with the sticker URL can read the selected health fields and may retain copies. Private-by-default controls and withdrawal cannot recover a stranger's copy.

Document a processing inventory, purposes, recipients, provider/subprocessor contracts, processing locations and transfers/safeguards. Evaluate whether a DPIA and DPO are required before launch, given special-category health data and public emergency capabilities. Set an approved backup retention period and deletion schedule, incident-response owner, and access-review schedule. Document organizational and technical controls rather than treating this repository as a compliance certificate.

## Consent and contact participation

Registration records the displayed notice version as an acknowledgement. It does not grant health consent. Health entry requires a separate affirmative choice and individual public-field selections. The consent record includes wording, policy version, action and time. Withdrawing health consent deletes current medical fields and clears medical visibility. A changed policy version asks for a new affirmative choice on the next edit; prior records retain the version actually accepted.

Contact invitations require recipient confirmation. The recipient can separately accept or decline emergency notifications and public display of the proposed name, relationship and telephone number. A profile owner can request these choices but cannot grant them for the recipient. Altering public identity/phone requires fresh publication permission; changing email invalidates old recipient verification and withdrawal tokens. Removing or unsubscribing a contact stops future notifications and revokes incident access. Mail already handed to a provider cannot be recalled.

For upgrades, legacy contacts have no proof of the new explicit public-phone choice, so they are not publicly displayed until they accept it. Legacy withdrawal URLs using the old payload are invalid after the capability-binding change; ensure affected recipients receive a current invitation/withdrawal link or can reach the monitored privacy contact. Do not backfill an invented historical public consent timestamp.

## Rights requests

Monitor the configured address. Record receipt date, requested scope, deadline and the minimum information needed to resolve the request in a restricted case system. Avoid copying medical details or capability links into tickets. Verify identity proportionately through the existing account where possible; do not request identity documents automatically. For an emergency contact without an account, verify control of the recorded contact address without revealing the rider's other contacts or incidents.

Respond without undue delay, normally within one month. If a lawful complexity extension is necessary, explain it within the first month; the extension can be up to two further months. Document any lawful refusal and the available complaint route. Handle rights of access, correction, erasure, restriction, objection, portability and consent withdrawal as applicable; do not assume every right applies identically to every legal basis.

Self-service /profile/export provides the authenticated owner's account, source profile fields, birth date, visibility and consent metadata, contact records, identifier metadata, own incidents/access metadata, notification metadata, own audit actions and photo. It excludes passwords, MFA secrets and raw access capabilities. Send any operator-assisted export through an authenticated or otherwise protected channel. Review third-party information and scope before external disclosure; an administrator role is not permission to browse medical records or download arbitrary profiles.

Use self-service editing/withdrawal/deletion for account holders. Contact withdrawal links are confirmed via POST. For operator-assisted erasure or correction, verify the request, use an approved scoped administrative procedure with a second check on target identity, and record completion without retaining the removed medical content. The current admin interface deliberately offers no unrestricted medical export or deletion console.

Account deletion removes linked active data and the configured external photo. Audit references are unlinked. Check completion and filesystem errors; failed physical photo deletion needs controlled follow-up. Record erasures needed for backup recovery in a minimal restricted suppression ledger for no longer than the backup lifetime. Before restoring service from backups, reapply intervening erasures, revocations, expiries and consent withdrawals.

## Retention and verification

Default incident access lasts 24 hours; report/outbox/audit retention is 30 days, with periodic worker cleanup and no deletion of an incident before access expires. Record any changed administrator settings and their justification. Accounts/profiles remain until deletion; the application has no automatic inactive-account policy. Define a transparent, proportionate account-retention policy before introducing one; do not silently delete accounts through an unrelated cleanup setting.

Run the worker continuously as the documented service. Monitor sanitized cleanup counts and outbox failure counts, and alert on service downtime, expiring certificates, disk exhaustion and failed backups. Periodically verify expiry/revocation, contact withdrawal, owner isolation, export, account deletion, photo removal, last-administrator protection and restore-with-erasure behavior using synthetic records. Verify database/disk, photo and backup encryption and permissions; database field encryption does not cover every data column.

On upgrade, run the deployment Identity-secret backfill and verify that legacy AspNetUserTokens.Value rows have the protected SBID1 prefix without displaying their contents. Do not print tokens, medical values, passwords or connection strings during checks. Back up the key ring and wrapping certificates securely before an upgrade; key deletion is not a safe routine erasure mechanism for a shared application database.

## Incidents and external disclosures

Maintain a breach-response procedure: contain access, preserve minimal restricted evidence, determine affected data/people and assess notification duties. Where required, notify the supervisory authority without undue delay and within 72 hours of awareness; document delay and risk assessment. Notify affected individuals without undue delay when the applicable high-risk threshold is met. Operational troubleshooting must not copy protected bodies, capability URLs or source medical records into general logs or email.

Review IIS/proxy/CDN/APM logging before traffic is enabled. Disable capability paths, token query strings and request/response bodies; justify and limit any external IP retention. Keep hosting, SMTP, backup and certificate access restricted to assigned operators. Incident emails omit medical details and coordinates, but SMTP providers still process recipient addresses and alert/invitation metadata. A recipient's deliberate map action sends coordinates to the map provider. Reflect actual providers and transfer details in the controller's published information and respond accurately to related requests.
