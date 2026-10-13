# T007 / Risk R1 findings — webscraper.io

**Date**: 2026-10-11
**Host investigated**: `webscraper.io` (operator-specified: "ingest only from
https://webscraper.io/test-sites")
**Method**: fetched `/robots.txt`, then three allowed pages with a descriptive User-Agent and a
3-second spacing. No disallowed path was requested. No bot protection was encountered or
circumvented.

## Summary

| Question | Answer |
|----------|--------|
| Reachable without defeating bot protection? | **Yes** — HTTP/2 200, no challenge, no WAF |
| schema.org JSON-LD present? | **No — zero `application/ld+json` blocks on any page** |
| schema.org microdata present? | **Yes** — `itemprop` on name, price, priceCurrency, description, image, offers, aggregateRating, reviewCount |
| Conditional-request validators? | **No ETag, no Last-Modified.** Only `cache-control: max-age=300, public` |
| Crawl-delay declared? | No — our configured default applies |
| **Does it contain car data?** | **Yes — at `/test-sites/load-more`.** The e-commerce test sites do not; that page does. |

## 1. robots.txt — five test-site paths are disallowed

```
User-agent: *
Disallow: /test-sites/product/
Disallow: /test-sites/pagination/
Disallow: /test-sites/pagination*?page=
Disallow: /test-sites/scroll/
Disallow: /test-sites/load-more/

Sitemap: https://webscraper.io/sitemap.xml
```

Verified against our own `RobotsEvaluator`, which is the first real-world exercise of the
RFC 9309 implementation:

| Path | Allowed | Note |
|------|---------|------|
| `/test-sites` | yes | |
| `/test-sites/e-commerce/allinone` | yes | listing page |
| `/test-sites/e-commerce/allinone/computers/laptops` | yes | |
| `/test-sites/e-commerce/allinone/product/17` | yes | **not** under `/test-sites/product/` |
| `/test-sites/product/549` | **NO** | prefix rule |
| `/test-sites/pagination/` | **NO** | |
| `/test-sites/pagination?page=2` | **NO** | matched via the `*` wildcard rule |
| `/test-sites/scroll/` | **NO** | |
| `/test-sites/load-more/` | **NO** | |
| `/test-sites/tables` | yes | |

The near-miss matters: `/test-sites/e-commerce/allinone/product/17` is permitted while
`/test-sites/product/549` is not. A naive "contains /product/" check would have wrongly
excluded every usable page; a naive prefix-strip would have wrongly included the forbidden one.
The evaluator got both right, including the wildcard-plus-query rule.

## 2. JSON-LD is absent — D9's primary strategy does not apply to this host

```
/test-sites                                     200   0 JSON-LD blocks   0 itemprop
/test-sites/e-commerce/allinone                 200   0 JSON-LD blocks  29 itemprop
/test-sites/e-commerce/allinone/product/17      200   0 JSON-LD blocks  13 itemprop
```

**This resolves R1 for this host in the negative.** `JsonLdCarExtractor` would return
`jsonld-absent` for every page here — correctly, and loudly, which is the designed behaviour.
It does not silently fall back to selectors (research D9), so nothing is mis-extracted; the run
simply stores nothing.

Consequence: **T085 (per-host CSS/microdata extractor) is no longer optional for this host.** The
plan treated it as the exception; for webscraper.io it is the only path.

The microdata that *is* present is a schema.org **Product** shape:
`name`, `price`, `priceCurrency`, `description`, `image`, `offers`, `aggregateRating`,
`reviewCount`.

## 3. No conditional-request support — confirms research R2

Only `cache-control: max-age=300, public`. No `ETag`, no `Last-Modified`, so
`If-None-Match`/`If-Modified-Since` have nothing to replay and every run re-fetches in full.

Research R2 rated this LOW risk precisely because D13 made the canonical projection hash the
correctness mechanism and conditional requests a mere optimization. That judgement holds: this
host costs bandwidth, not correctness.

## 4. Car data is at `/test-sites/load-more` — correcting an earlier error in this document

An earlier revision of these findings concluded the host had no car data. That was wrong: it was
based on the e-commerce test sites (`/test-sites/e-commerce/*`), which really do only carry
laptops, tablets, and phones. The operator pointed to `/test-sites/load-more`, which is a
**classic-car listing page**:

- Mercedes-Benz W123 280E 1955
- BMW E24 635CSi 1954
- Jaguar XJ6 1983
- Nissan 300ZX Z31 1966
- Ferrari F40 1984
- Mercedes-Benz W123 280E 1972

Six cards are present in the served HTML, so **no JavaScript is required** to read them. The
"load more" button fetches further cards by AJAX, but that endpoint is under the disallowed
prefix and is therefore out of scope.

### robots.txt and the trailing slash — a real distinction, not a technicality we invented

| Path | Verdict |
|------|---------|
| `/test-sites/load-more` | **ALLOWED** |
| `/test-sites/load-more/` | **DISALLOWED** |
| `/test-sites/load-more/ajax` | **DISALLOWED** |

RFC 9309 matches a `Disallow` value as a prefix. `Disallow: /test-sites/load-more/` is *longer*
than the path `/test-sites/load-more`, so it cannot match it, while it does match the directory
and everything beneath. Our evaluator reports exactly that.

The practical consequence is favourable and worth stating plainly: **the one page we need is
permitted, and the AJAX pagination we do not need is forbidden.** We therefore read the served
HTML only and never request more.

### Detail pages are disallowed

Each card links to `https://webscraper.io/test-sites/product/<slug>`, which is under
`Disallow: /test-sites/product/` and **must not be fetched**. Fortunately the listing page
carries every field we store, so nothing is lost. This also means link-following would be
actively harmful here — consistent with FR-001, which already forbids it.

### The markup: `schema.org/Product` microdata plus labelled text

Each card is `itemscope itemtype="https://schema.org/Product"` — **not** `Car` or `Vehicle`, so
`JsonLdCarExtractor` would correctly reject it even if the data were in JSON-LD.

| Field | Source | Example |
|-------|--------|---------|
| name | `itemprop="name"` | `Mercedes-Benz W123 280E 1955` |
| description | `itemprop="description"` | `Uncompromising Stuttgart saloon` |
| price | `itemprop="price"` | `USD 228 511` (currency inside the text) |
| priceCurrency | `<meta itemprop="priceCurrency">` | `USD` |
| year | `<p class="card-text"><b>Year: </b> 1955` | not microdata |
| mileage | `<p class="card-text"><b>Mileage: </b> 104 338 km` | not microdata; space thousands separator |
| country of origin | `<p class="card-text"><b>Country of origin: </b>` | not an allowlisted field — discarded |
| rarity rating | `data-rating="3"` | not an allowlisted field — discarded |

### This maps onto the existing car spec without amendment

`Make`, `Model`, and `Year` — the three mandatory fields — are all derivable:

- **Year** is given explicitly in its own labelled field, and also appears as the trailing token
  of `name`. Having both allows cross-validation.
- **Make** is the leading token of `name` (`Mercedes-Benz`, `BMW`, `Jaguar`, `Nissan`,
  `Ferrari`). Hyphenated makes survive whitespace tokenization intact.
- **Model** is the remainder of `name` after removing the leading make and the trailing year
  (`W123 280E`, `E24 635CSi`, `XJ6`).

`MileageValue`, `MileageUnit`, `PriceAmount`, and `PriceCurrency` are all present.
`Trim`, `FuelType`, `Transmission`, `BodyStyle`, `Colour`, and `Vin` are absent — all six are
nullable, so their absence is not schema drift.

**No spec amendment is required.** `VehicleFields` fits this host as written.

### The description field stays unstored

`itemprop="description"` here is editorial copy ("Uncompromising Stuttgart saloon") with no
personal data, so storing it would be harmless *on this host*. It is still not stored, because
research D8 makes the absence of a free-text field structural rather than conditional, and a
field that exists for one host exists for all of them. D8 already provides the escape hatch — a
per-host opt-in after a documented PII review — and that remains available if retrieval quality
later justifies it.

## 5. Ethical and legal assessment — clean

Unlike the aggregators the earlier research attempted, this host is unambiguously appropriate:

- It is **purpose-built for scraping practice**, which is as close to express permission as a
  public site gets.
- It carries **no personal data at all** — no sellers, no contact details, no user accounts. Gate
  I-4 is satisfied trivially here, which also means this host cannot exercise the PII defences;
  those stay on the synthetic fixture that was built to carry them.
- No bot protection, so FR-007 is not engaged.
- Its robots.txt is honoured in full, including the five disallowed paths.

## Conclusion

**Adopt `webscraper.io` as the sole ingestion host for now, scoped to exactly one path:
`/test-sites/load-more`.**

Risk R1 is resolved for this host:

- **JSON-LD: absent.** The JSON-LD-first strategy does not apply. A microdata extractor is
  required, so T085's per-host extraction work is mandatory rather than optional — but it is one
  profile for one host, not the open-ended burden the risk contemplated.
- **Bot protection: none.** FR-007 is not engaged.
- **Car data: present and complete enough** for all three mandatory fields plus mileage and
  price. No spec amendment needed.
- **Conditional requests: unsupported.** Confirms R2 at LOW impact, exactly as assessed.

### Host policy to configure

| Setting | Value | Why |
|---------|-------|-----|
| Allowed path | `/test-sites/load-more` only | Everything else on the host is either disallowed or not car data |
| Extractor | microdata profile | No JSON-LD exists |
| Rate ceiling | 1 request / 5s | No `Crawl-delay` declared, so our conservative default stands |
| Permitted fields | Make, Model, Year, MileageValue, MileageUnit, PriceAmount, PriceCurrency | The six fields the page actually provides |

### What this host cannot validate

Car-field extraction from JSON-LD, and every PII containment assertion — there is no personal
data here to contain. Those remain on the synthetic fixtures, which is what they were built for.

### Fixtures remain synthetic

A fixture mirroring this markup is **hand-authored to match the observed structure**, not saved
from a capture. The prohibition in `fixtures/README.md` is kept intact even though this
particular host carries no PII and expressly invites scraping: the rule's value is that it needs
no case-by-case judgement.
