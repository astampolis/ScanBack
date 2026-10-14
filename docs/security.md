# Emergency access security model

## Public sticker capability

QR and NFC contain the same configured HTTPS URL and a 256-bit cryptographically random token. Lookup uses SHA-256; an encrypted token copy supports subsequent QR downloads. There are no sequential rider IDs in public URLs. Revocation, replacement and account deactivation are checked at every emergency access.

A random URL prevents enumeration, not disclosure by a photograph or forwarded link. All profile fields are private by default. A rider explicitly publishes individual fields and previews the result. Public views use a dedicated projection; private email addresses, birth dates and unselected medical fields are excluded. Published age is computed from date of birth. Blood type and medical information are labelled rider supplied.

Health processing consent and publication are separate. Withdrawing health consent deletes health fields and clears medical visibility. Account deletion removes active profile/contact/sticker/incident/outbox/consent/photo data and anonymizes its audit references. Backup deletion follows the operator's separately documented backup retention policy.

## Incident access

Opening a sticker does not collect GPS or send notifications. An explicit form submission creates an unverified report. Browser location is requested only on a separate user gesture; location denial or unavailability permits manual landmarks and reporting without coordinates. GPS can be inaccurate and bystander statements are unverified.

Each reporter receives a protected, HttpOnly incident-specific cookie. Each verified and enabled contact receives its own random, hashed invitation token. Opening an invitation presents a confirmation page; a POST establishes a restricted session and redirects to a token-free URL. GET prefetching by email scanners does not authorize a session. Expiry, account/identifier status and current contact permission are rechecked on every incident access.

Defaults: 24-hour incident access, 30-day incident retention and a 10-minute notification cooldown. Administrator settings can change these within validated bounds. Database-backed cleanup deletes expired retained incidents and related invitations/outbox. Audit retention follows the configured incident retention setting. The precise values and lawful bases need legal review.

## Abuse and web controls

Reports require antiforgery tokens and valid active stickers; coordinates are paired, finite and bounded. Submission keys prevent duplicate incidents. SQL row locking serializes same-sticker reporting decisions and revocations. Source rate limits and sticker cooldowns reduce notification abuse; rejected/unavailable pages retain 112/166 calling links.

Authentication uses Identity hashing, confirmed email, lockout, secure HttpOnly cookies and safe local return URLs. Inactive users lose authenticated access. Admin routes require role membership and a completed MFA sign-in, and do not load medical content. All changes use antiforgery protection and scoped ownership checks.

Public/incident/account pages use `no-store`, `no-referrer`, `noindex`, CSP and `nosniff`. There are no tracking scripts or external fonts. Opening Google Maps deliberately sends the selected location to Google. Profile images are size/dimension limited, decoded, and re-encoded to remove original metadata; files reside outside the public web root.

## Storage, email and operational controls

Medical strings and notification bodies are encrypted with ASP.NET Data Protection. Production key files require certificate wrapping; certificate/password management and key backup remain deployment responsibilities. SQL connection certificate validation is mandatory in production, and storage/backups must also be encrypted. Development SQL uses a self-signed server certificate only in the explicitly local environment.

The durable outbox claims messages, retries at most five times with backoff, and records sanitized error categories. Delivery validates active incident/sticker/account/contact permissions again. No medical data or coordinates are placed in incident emails. SMTP uses ordinary validated TLS. “Provider accepted” does not guarantee receipt or timely reading, and uncertain SMTP outcomes can lead to duplicates.

Audit records contain actor/action/entity/time metadata, not medical content, coordinates, invitation tokens or email bodies. Do not add request-path logging for capability routes at your reverse proxy, CDN or APM: redact `/e/`, `/incident/join/`, `/contacts/verify/`, `/contacts/unsubscribe/`, and account token query strings. Limit database, key, photo, audit and backup access separately.

Before launch, review hosting/provider contracts, international transfers, lawful bases, data-subject processes, incident response, consent wording and backup retention with a qualified adviser. ScanBack is not integrated with emergency dispatch and does not certify GDPR compliance.
