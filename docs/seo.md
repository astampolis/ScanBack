# Public search visibility

ScanBack publishes useful English and Greek pages about QR/NFC motorcycle helmet stickers, emergency profiles and preparing emergency contacts. Search engines decide whether and where to show them. Correct technical setup, original content and a reliable service improve eligibility; no implementation can guarantee a Google position or immediate indexing.

## What may appear in search

The sitemap is an explicit list of eight public marketing pages:

| English | Greek |
| --- | --- |
| `/en` | `/el` |
| `/en/how-it-works` | `/el/how-it-works` |
| `/en/qr-nfc-helmet-stickers` | `/el/qr-nfc-helmet-stickers` |
| `/en/motorcycle-emergency-guide` | `/el/motorcycle-emergency-guide` |

Each language URL serves its language directly, without requiring a language cookie or browser preference. `/` redirects permanently to `/en`. Each page has a canonical URL, reciprocal English/Greek `hreflang` links and an English `x-default` link. Canonicals omit query strings. Their origin comes from trusted `Site:BaseUrl`, never the request Host header.

Public pages provide a distinct title, description, social metadata and truthful WebSite, Organization and breadcrumb structured data. These describe ScanBack and the page; they do not assert reviews, emergency-service endorsement, medical effectiveness or guaranteed search features. Google may choose different search snippets or omit structured-data enhancements.

Accounts, rider profiles, photos, contacts, sticker downloads, emergency links and incidents must stay out of search. They are excluded from the sitemap and receive `noindex, nofollow` and `Cache-Control: no-store`. The privacy notice and operational endpoints are not marketing landing pages. Keep the indexing exception restricted to the explicitly marked marketing endpoints when adding routes.

`robots.txt` reduces crawling of private route families. It is public and provides neither authorization nor confidentiality. A disallowed URL can still be discovered from a link; a crawler may also be unable to read its `noindex` response while crawling is disallowed. Access controls, unguessable and revocable capabilities, careful link handling and response policies remain necessary. Never publish an emergency token, incident invitation, real rider example or private image in marketing HTML, a sitemap, structured data, screenshots or Search Console submissions. Use synthetic examples for checks. See [the security model](security.md) and [privacy operations](privacy-operations.md).

## Before enabling the production domain

1. Configure the real public HTTPS origin in `Site:BaseUrl` and the matching exact hostname in `AllowedHosts`. Complete DNS, the IIS HTTPS binding and a trusted certificate using [the deployment guide](iis-deployment.md). Choose one public hostname and redirect aliases at the trusted edge. Keep staging environments access controlled and excluded from indexing.
2. Request every public language URL on the live domain. Confirm a successful response, the intended language, one meaningful H1, readable mobile layout and working links. Inspect the rendered HTML for its canonical, description, reciprocal alternate URLs and structured data. Confirm the public response does not carry `X-Robots-Tag: noindex` or a robots `noindex` meta tag.
3. Fetch `/robots.txt` and `/sitemap.xml`. Confirm the sitemap contains only the eight configured-origin URLs, with neither private IDs nor tokens. Check that public CSS, JavaScript and brand assets are crawlable. Redirects, errors and private responses must retain their restrictive indexing policy.
4. With synthetic data, check an emergency response and authenticated account/profile/incident responses for `X-Robots-Tag: noindex, nofollow` and `Cache-Control: no-store`. Check redirects and unavailable links too. Do not test by submitting a live capability to a public validation service.
5. Verify the production domain in [Google Search Console](https://search.google.com/search-console/about), preferably with the domain property's DNS record. Submit the production `/sitemap.xml`. Inspect only the public marketing URLs and request indexing when a page is ready. Review Page indexing, HTTPS, Crawl stats and enhancement reports after Google has recrawled.

Repeat the checks after changing the hostname, proxy, middleware, caching or routing. A successful local test does not establish live DNS, certificate validity, crawl access or Google indexing. If a private URL was exposed, revoke access where appropriate, investigate the disclosure and use Search Console's removal tools as part of the response; search removal alone does not revoke a capability or delete third-party copies.

## Content and service accuracy

Maintain the English and Greek pages together. Use natural language answering real rider questions: how a phone opens the QR link, how NFC uses the same link, what a helper can see, how contacts agree to participate, how to replace a lost sticker and what happens without connectivity. Explain limits clearly. Public emergency information is rider supplied and unverified; anyone with the capability URL may be able to read the selected fields. Keep health information private by default and describe actual choices without promising confidentiality after disclosure.

In Greece, a helper calls **112** for emergency assistance or **166** for EKAB ambulance assistance. Scanning a sticker or submitting a ScanBack report does not call or dispatch emergency services. Keep the guide focused on preparation and communicating location and observed facts to the dispatcher; do not add unsupported medical instructions. Review number/service descriptions against the official [Greek 112 information](https://civilprotection.gov.gr/en/112) and [EKAB](https://www.ekab.gr/) when updating the content.

Use original, practical explanations and descriptive internal links between the related pages. Keep titles and descriptions specific to each page. Add new public pages only when they answer a distinct question with reviewed content, then update both language versions, indexing tests and the explicit sitemap list. Avoid duplicated location pages, keyword repetition, invented statistics, fake reviews, purchased links and promises of emergency outcomes. Earn relevant links through accurate product information and legitimate rider/community resources.

If `Site:ContentUpdatedAt` is configured for sitemap `lastmod`, use the actual date of a substantive public-content revision. Omit it when unknown. Do not set it to the current date on every request or alter it for unrelated private-data activity.

## Performance and ongoing review

Public pages are rendered on the server and use local assets. Keep the initial HTML useful without JavaScript, compress responses at the server/edge, retain explicit image dimensions, and avoid unnecessary third-party scripts or oversized imagery. Shared caches must not store authenticated or personalized pages. A public cache rule must never expand to emergency links, contact verification, account pages or private photos.

Measure the live mobile experience with PageSpeed Insights and Search Console's Core Web Vitals report. Track LCP, INP and CLS using field data when traffic is sufficient; laboratory scores help diagnosis but do not prove field performance or ranking. Check accessibility, keyboard use, text contrast, language switching, internal links and real phone QR/NFC behavior after visual changes. No analytics or external trackers are needed to publish these pages; review consent and privacy requirements before introducing them.

Review search queries, index coverage, broken links and content accuracy regularly. Investigate unexpected private URLs immediately. Keep Search Console access limited to trusted operators and avoid copying reports containing capability URLs into general tickets or chat.

Official guidance: [Google Search Essentials](https://developers.google.com/search/docs/essentials), [SEO starter guide](https://developers.google.com/search/docs/fundamentals/seo-starter-guide), [localized versions](https://developers.google.com/search/docs/specialty/international/localized-versions), [sitemaps](https://developers.google.com/search/docs/crawling-indexing/sitemaps/overview), [robots meta and headers](https://developers.google.com/search/docs/crawling-indexing/robots-meta-tag), and [Core Web Vitals](https://web.dev/articles/vitals).
