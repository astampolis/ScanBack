# Emergency access security model

## Sticker capabilities and visibility

QR and NFC use the same configured HTTPS URL with a 256-bit random token. SHA-256 hashes support lookup; an encrypted token copy supports later QR downloads. Random links resist enumeration but can be copied, photographed or forwarded. Neither possession of a sticker nor a report proves an emergency or the holder's identity.

All profile fields are private by default. The Application public projection includes only selected fields; health fields additionally require affirmative health-processing consent. It excludes account identifiers, private emails, dates of birth and unselected health fields. Age is calculated from the birth date. Medical information is labelled rider supplied and unverified. Public photos require an active sticker and current photo visibility, are decoded and re-encoded to strip metadata, and remain outside the public web root.

Owner requests and recipient permissions for contacts are separate. Public contact display requires the owner's Contacts visibility, verified recipient email, a requested publication and recipient-accepted public phone permission with a consent timestamp. The recipient sees the proposed name, relationship and number and may independently decline notifications or publication. Changing the email invalidates verification and rotates withdrawal capability; changing the displayed identity or number removes publication until new confirmation. Owner requests alone cannot enable notifications or publish a phone number.

Health consent records store the configured policy version and wording. Health consent withdrawal deletes health fields and clears their visibility. Consent versions identify the wording accepted at that time; deployment of a new notice does not silently rewrite prior consent. The next profile edit asks for a fresh health choice if its version changed.

## Incident access and revocation

Opening a sticker does not request GPS or send alerts. An explicit antiforgery-protected POST submits an unverified report. Browser location is requested only after a separate user action; landmarks and reporting without coordinates remain available. Paired finite coordinates and report size/count bounds are validated.

The reporter receives a protected HttpOnly cookie for that incident. Each participating verified contact receives its own random invitation. GET displays a confirmation page without authorizing a session. A successful POST atomically consumes the invitation and generates a different random session secret stored as a hash. The browser receives a protected incident cookie and redirects to a token-free URL. Replaying the invitation fails; it is no longer a second browser session credential. Reporter cookies created before this upgrade remain recognized, while legacy contact invitations must be exchanged under the new flow.

Every incident request checks expiry, closed/revoked state, active owner, identifier status and current contact permission. Owner deactivation/replacement revokes existing incident grants. Administrator blocking has a separate durable flag; the owner cannot clear it with Activate or Replace. An owner may issue a new identifier unless the account is also disabled. Contact withdrawal/removal revokes their grants; later re-enabling participation cannot restore those grants.

Default access is 24 hours, retention 30 days, and per-sticker notification cooldown 10 minutes. Validated administrator settings can change these values. Access expires immediately at its deadline; the separate worker periodically deletes eligible retained incidents, grants, notification records and old audit metadata. Retention is measured from report creation and does not delete a still-accessible incident. Accounts remain until deleted; no automatic inactive-account deletion is claimed.

## Authentication and web controls

Identity uses password hashing, confirmed email, bounded lockout and MFA support. Administrative routes require the Administrator role and a completed MFA sign-in; an enabled authenticator without an MFA-authenticated session is insufficient. The administrator interface queries operational metadata, not medical profile content. Inactive accounts lose authenticated and emergency access. Last-active-administrator deletion and deactivation checks run in serializable transactions.

State-changing account, contact, profile, sticker and administration forms require antiforgery protection. Account email confirmation, contact confirmation and invitation redemption use POST confirmation, so GET scanners do not consume them. Ownership predicates protect private resources. Database row locks serialize contact verification/edit/withdrawal, profile changes/health withdrawal, per-sticker reports, and identifier status changes. Password-reset and account-confirmation emails have recipient cooldowns as well as source authentication limits. Reports use transient source limits, submission keys and durable sticker cooldowns. Source limits run in each web process; multi-instance installations require an equivalent trusted-edge limit.

Sensitive pages receive no-store, no-referrer, noindex, CSP and nosniff policies. There are no analytics scripts or external fonts. Opening Google Maps deliberately discloses the selected location to Google. The application does not continuously track location or dispatch emergency services.

## Storage and delivery

Profile medical strings and outbox bodies use ASP.NET Data Protection. New or rewritten Identity token values, including authenticator keys and recovery codes, are protected with a separate purpose and an SBID1 prefix. Existing plaintext Identity token rows remain readable for an explicit upgrade backfill; deploying this converter alone does not encrypt historical rows. Run the deployment backfill before accepting the upgraded database as fully protected. Key loss makes protected data, sticker copies and sessions unreadable; retain protected historical keys and their wrapping certificates with controlled backups.

Other database fields, including contact details and report consciousness/location fields, rely on database/disk and backup encryption plus access controls. Production configuration requires validated SQL TLS and certificate-wrapped persisted Data Protection keys. Keep the web and worker SQL identities at data-access privileges, and supply schema-change credentials only to the deployment tool. Photos, keys, certificates, configuration and backups require separate ACLs outside the installation and web root.

The separate worker atomically claims bounded outbox batches, rechecks incident/sticker/owner/contact permissions and retries at most five attempts with backoff. SMTP requires validated TLS. An already in-flight SMTP transmission cannot be recalled by subsequent withdrawal. SMTP acceptance does not prove delivery or reading; uncertain acceptance can cause duplicates. Incident emails carry a general alert and invitation, without medical fields, report descriptions or precise coordinates. Application logs retain sanitized transport categories and operational identifiers, never message bodies or exception dumps from SMTP.

Audit events contain actor/action/entity/time metadata. The application does not store source IPs in database records; in-process abuse controls hold source keys transiently. Disable or redact capability paths and sensitive queries in IIS, proxies, CDNs, tracing and APM: /e/, /incident/join/, /contacts/verify/, /contacts/unsubscribe/, and account confirmation/reset queries. Disable request/response body capture. A protected database does not protect copies written into external access logs.

## Operational and legal boundaries

Account deletion removes active account/profile/contact/identifier/incident/outbox/consent data, removes its photo and unlinks retained audit references. Erasure from backup copies follows the operator's documented backup schedule; a restored backup must have intervening erasures reapplied before serving traffic. See [privacy operations](privacy-operations.md) for rights requests and launch review.

These are technical controls, not a GDPR certification. The controller must review lawful bases, a health-data DPIA, provider agreements, international transfers, access governance, incident response and retention with appropriate legal advice. A public emergency link cannot guarantee confidentiality after somebody views or copies it, and the service cannot guarantee a timely emergency response.
