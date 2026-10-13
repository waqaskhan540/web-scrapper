# Phase 0 Research: Car Data Vector Ingestion

**Date**: 2026-10-11 | **Plan**: [plan.md](./plan.md) | **Spec**: [spec.md](./spec.md)

Resolves the four items the pre-research Constitution Check left open: **I-4** (PII screening
mechanism), **III-3** (vector schema via reversible migrations), **T-1** (concrete .NET LTS
version), **T-7** (the dependency choices this feature is first to need).

Package versions and limits below were verified against current registries and docs on
2026-10-11. Items that could **not** be verified are called out in
[Unresolved risks](#unresolved-risks) rather than smoothed over — two of them are load-bearing.

---

## D1. Runtime: .NET 10 (`net10.0`)

- **Decision**: Target `net10.0`. Resolves gate T-1 and pins the constitution's
  "current LTS" binding.
- **Rationale**: Verified present on this machine (SDK 10.0.102, ASP.NET Core runtime 10.0.2),
  alongside 9.0.102, 8.0.203, 6.0.419. .NET 10 is the current LTS, so no preview runtime is
  involved and gate T-1 passes without a waiver.
- **Alternatives considered**: .NET 9 — not LTS, so it would violate T-1. .NET 8 — still LTS
  but would force `Pgvector.EntityFrameworkCore` down to 0.2.2 (see D3) for no benefit.

## D2. Shape: one ASP.NET Core host, API + in-process worker

- **Decision**: A single ASP.NET Core host exposing a versioned control API, with ingestion
  executed by a `BackgroundService` reading from a bounded in-process `Channel`. No external
  scheduler, no separate worker deployment.
- **Rationale**: FR-019 requires operator-triggered runs, not scheduled ones, and the spec puts
  scheduled ingestion out of scope. A `Channel` plus one `BackgroundService` satisfies FR-022's
  isolation requirement (the worker cannot fault the request pipeline) with no new dependency.
  The constitution's lightweight principle set makes "justify each package" the default posture.
- **Alternatives considered**: **Hangfire / Quartz.NET** — real scheduling, durable job state, a
  dashboard; rejected because nothing in scope schedules anything, and run state is already
  persisted in Postgres (D7) which makes the durability argument redundant. **Separate worker
  process** — stronger isolation; rejected as premature for a single-operator service, and it
  would double the deployment surface. Both become reasonable if scheduled ingestion lands.

## D3. Vector storage: Postgres + pgvector via `Pgvector.EntityFrameworkCore`

- **Decision**: `Pgvector` 0.3.2 + `Pgvector.EntityFrameworkCore` 0.3.0 over
  `Npgsql.EntityFrameworkCore.PostgreSQL` (>= 9.0.1, EF Core 10 on .NET 10). Register with
  **both** `dataSourceBuilder.UseVector()` and `UseNpgsql(cs, o => o.UseVector())` — one
  configures Npgsql type mapping, the other the EF provider.
- **Rationale**: The `pgvector/pgvector-dotnet` packages are the official client path. Distance
  operators translate through LINQ (`L2Distance` → `<->`, `CosineDistance` → `<=>`,
  `MaxInnerProduct` → `<#>`, `L1Distance` → `<+>`), which covers the whole of FR-028's
  top-K query.
- **Constraints this imposes**:
  - Pass the query vector as a `Pgvector.Vector` **parameter**, never a raw array — otherwise
    the vector is inlined as a literal, bloating the plan cache.
  - To return the distance, project it explicitly; `OrderBy` alone discards it.
  - Session tuning (`hnsw.ef_search`, `ivfflat.probes`) has **no LINQ surface** — raw SQL only.
    EF Core also cannot inject raw SQL into `ORDER BY`, so any multi-term scoring later needs
    `FromSqlInterpolated` for the whole statement.
- **Alternatives considered**: **Raw Npgsql / Dapper** — full SQL control, no ORM translation
  risk; rejected because EF Core migrations are how gate III-3 gets satisfied, and LINQ covers
  the one query shape in scope. **Dedicated vector DB (Qdrant, Weaviate)** — better ANN
  tooling; rejected outright, the spec names Postgres and a second datastore would split
  provenance away from the documents it describes.

## D4. No ANN index in v1 — exact search

- **Decision**: Store vectors in an unindexed `vector(768)` column and rely on exact sequential
  scan. Revisit only if measured latency justifies it.
- **Rationale**: pgvector's own small-dataset guidance is to raise
  `max_parallel_workers_per_gather` rather than build an index; exact scan over 10k–100k vectors
  at these dimensions is commonly sub-50 ms. The spec sizes the system at tens of thousands of
  documents (SC-002: 10,000 addresses per run). An HNSW index would add build cost, memory, and
  recall-tuning parameters to buy nothing measurable, and IVFFlat would additionally need
  representative data loaded *before* `CREATE INDEX` — an awkward ordering constraint for
  checked-in migrations.
- **Alternatives considered**: **HNSW** (`m=16`, `ef_construction=64`, query `ef_search=40`) —
  the right default *if* an index were needed, since it needs no training step and holds recall
  without query-time tuning; deferred, not rejected. **IVFFlat** (`lists ≈ sqrt(rows)`,
  `probes ≈ sqrt(lists)`) — rejected; default `probes=1` is far too low for production and the
  training-data ordering fights migrations.
- **Hard limit to respect regardless**: an **indexed** `vector` column caps at **2,000
  dimensions** (unindexed: 16,000; `halfvec`: 4,000 indexed). This constrains D5.

### D4 amendment — measured 2026-10-11, decision unchanged but conditional

The research claim "exact scan over 10k–100k vectors is commonly sub-10-50 ms" is **optimistic
by about an order of magnitude**. Measured against pgvector 0.8.7 with 100,000 random 768-dim
vectors (405 MB table), `ORDER BY embedding <=> $1 LIMIT 10`:

| Configuration | Execution time (3 runs) |
|---------------|-------------------------|
| Postgres defaults | **503 ms / 348 ms / 327 ms** |
| `max_parallel_workers_per_gather = 4`, `min_parallel_table_scan_size = 0` | **94 ms / 91 ms / 93 ms** |

So the no-index decision **holds, but is conditional on the parallel-worker setting**, which
pgvector's own small-dataset guidance recommends. Unparallelized, exact scan misses the 200 ms
goal in [plan.md](./plan.md#technical-context) by a wide margin; with four workers it meets it
with room to spare.

Two consequences worth stating plainly:

- The parallel-worker configuration is **not a nice-to-have tuning knob**; it is load-bearing
  for the latency goal and belongs in deployment configuration, not in a runbook footnote.
- The margin at 100k rows is roughly 2x, not 20x. If the corpus grows well beyond the
  "tens of thousands" the spec scopes, HNSW stops being deferrable. The index decision should be
  revisited at around 250k rows rather than waiting for a complaint.

## D5. Embeddings: `IEmbeddingGenerator` over Ollama, `nomic-embed-text` (768-dim)

- **Decision**: Code against `IEmbeddingGenerator<string, Embedding<float>>` from
  `Microsoft.Extensions.AI` (GA, 10.9.0). Default provider: local **Ollama** via **OllamaSharp**,
  model `nomic-embed-text` (768 dimensions). Wrap it in a `DelegatingEmbeddingGenerator` that
  stamps model id + digest onto every result.
- **Rationale**: The abstraction makes the provider a DI registration, so swapping to a hosted
  model later is not a rewrite. Ollama is genuinely self-hostable with no metering, so
  re-embedding after an extractor fix (FR-017, SC-010) costs nothing and hits no rate limit.
  768 dimensions sits well under the 2,000-dim indexed cap from D4, leaving the index option
  open. The delegating wrapper is the natural choke point for FR-014's method+version stamping —
  it cannot be forgotten at a call site.
- **Note**: the official `Microsoft.Extensions.AI.Ollama` package is **deprecated**; OllamaSharp
  is the replacement and is what Microsoft's own `IEmbeddingGenerator` documentation
  demonstrates. `OllamaApiClient` implements the interface directly.
- **Alternatives considered**: **OpenAI `text-embedding-3-small`** (1536-dim, $0.01/M tokens
  batched) via `Microsoft.Extensions.AI.OpenAI` — the runner-up and genuinely lower-effort; cost
  for the whole corpus is **$0.25–$0.75** (25–75M tokens for 50k listings), so cost is not a
  factor either way. Rejected as the *default* only because it sends listing text to a third
  party; it stays a one-line registration swap. **Raw ONNX Runtime +
  `Microsoft.ML.Tokenizers`** — rejected: the available .NET wrapper packages
  (`AllMiniLmL6V2Sharp`, `SentenceTransformersCSharp`, `Kjarni.Extensions.AI`) are
  single-maintainer with unverified maintenance health, which is a poor bet for a service meant
  to run for years. **Anthropic** — offers no embeddings endpoint; its docs recommend Voyage.

## D6. One embedding per document, over templated structured fields

- **Decision**: No chunking. One vector per car document, computed over a **templated
  serialization of the allowlisted structured fields**, e.g.
  `"2018 Volkswagen Golf, 45,000 miles, GBP 12,000, diesel, manual, hatchback, blue"`.
- **Rationale**: A car listing is already one coherent semantic unit of a few hundred words,
  well inside every candidate model's context window — chunking would add bookkeeping and can
  *hurt* retrieval by fragmenting a unit meant to be matched whole. Embedding the structured
  fields rather than free text alone is essential: a bare seller blurb ("runs great, one owner")
  is semantically blind to year, make, mileage, price and fuel type, which is exactly what a
  query like *"cheap diesel wagon under 50k miles"* turns on.
- **Consequence**: the template is built **from the allowlisted projection**, so the PII screen
  (D8) runs strictly before template construction. Otherwise a phone number in free text would
  be baked into the vector, where it cannot be redacted afterwards.
- **Alternatives considered**: **Free text only** — rejected per above. **Structured + free
  text** — the best-quality option and standard practice, but it imports the free-text PII
  problem; see D8 for why v1 defers it. **Chunking with overlap** — reserved for genuine
  outliers (a multi-page history report), not the typical listing.

## D7. Schema: embeddings in their own table, keyed by model identity

- **Decision**: `car_document` holds the allowlisted fields plus provenance.
  `car_document_embedding` holds `vector(768)` with `model_id`, `model_version`, and a unique
  constraint on `(car_document_id, model_id, model_version)`. **Every similarity query must
  filter `model_id` + `model_version`.** Adopting a model with a different dimension requires a
  new migration adding a new table, not a column widening.
- **Rationale**: Satisfies FR-014/FR-029 structurally. pgvector columns are fixed-dimension and
  its indexes are per-column, so a single shared column cannot hold a 384-dim and a 768-dim
  vector without zero-padding — a hack that would silently corrupt distances. A separate table
  keeps each model's lifecycle an independent, reversible add/backfill/cutover/drop sequence,
  which is the shape the constitution's migration rule wants.
- **Alternatives considered**: **Column per model** — clean for indexing but causes schema churn
  and leaves dead columns after cutover. **Discriminator column with a filtered index**
  (`.HasFilter("model_version = '…'")`) — attractive for many short-lived versions, but only
  works while every row in the column shares one dimension, which breaks at the first model
  upgrade that changes dimensionality.

## D8. PII: allowlist projection, and **no free-text description in v1**

- **Decision**: Extraction maps named source properties field-by-field into a strict record
  containing **only** allowlisted non-personal fields, with **no catch-all
  `Dictionary<string, object>` member** so the compiler itself prevents unmapped data from
  propagating. **v1 stores no free-text seller description at all.** Including one is a
  per-host opt-in requiring a documented PII review, and is out of scope here.
- **Rationale**: This resolves gate I-4, the one live design risk. Car listings routinely
  display seller name, phone, and email, and descriptions embed them inline ("call John at…").
  FR-010 forbids store-then-filter, and regex scrubbing of free text has false negatives that
  **cannot be proven zero** — personal names in particular are not reliably detectable. Dropping
  the field makes the guarantee structural rather than probabilistic: there is no field for
  personal data to occupy. The structured template from D6 still carries the attributes queries
  actually turn on, so the retrieval cost is real but modest.
- **Raw pages are never persisted.** FR-017 requires retaining content for re-embedding without
  re-fetching; what is retained is the **screened projection**, never the raw page bytes.
- **Logging is in scope for this rule.** Logging a raw response body or a full parsed object on
  error would recreate the leak in the log sink; only strict-record fields may be logged.
- **How it is asserted** (not merely intended): fixtures carry seller name, phone, and email in
  several DOM positions, and tests assert the serialized stored record contains **none of those
  literal strings**. A CI golden list of PII patterns must never appear in a persisted snapshot,
  which guards against a future change widening the record.
- **Alternatives considered**: **Store description, scrub with regex** (E.164-ish phone,
  RFC 5322-ish email, plus obfuscations like "five five five") — better retrieval quality;
  rejected for v1 because an unprovable screen cannot satisfy a non-waivable principle.
  **Store description, embed only the structured template** — rejected: FR-010 bars storing
  personal data at all, not merely embedding it.

## D9. Extraction: JSON-LD first via AngleSharp, fail loud, no silent fallback

- **Decision**: **AngleSharp** 1.8.4 locates `script[type='application/ld+json']` nodes;
  `System.Text.Json` parses each into strict records; select the node by `@type`
  (`Car`/`Vehicle`/`Product`), not "the first block". A missing or wrong-typed mandatory field
  raises a typed schema-drift failure (FR-011). Per-host CSS-selector extractors are an
  explicitly declared fallback, never an automatic one.
- **Rationale**: JSON-LD is a machine-facing contract generated from the site's backend model,
  so it survives rebrands and component refactors that break CSS selectors. It yields typed
  named fields (`mileageFromOdometer`, `vehicleIdentificationNumber`) rather than "the third
  `<span>` in `.price-block`", which maps directly onto both the allowlist requirement (D8) and
  fail-loud (FR-011). AngleSharp over HtmlAgilityPack for native CSS selectors, a spec-compliant
  DOM, and first-class async; the microseconds-per-parse difference is irrelevant beside network
  I/O.
- **Failure modes designed against**: absence (fail loud, do not quietly switch to CSS);
  staleness between JSON-LD and the visible page (cross-validate price against a DOM anchor and
  flag mismatch); multiple blocks per page (select by `@type`); malformed JSON (a strict parser
  throwing is *desired*, but through a controlled logged path); schema abuse (sanity-range
  validate year, mileage, price).
- **Alternatives considered**: **CSS selectors as primary** — rejected, brittle and it silently
  matches wrong elements, which is the failure FR-011 exists to prevent. **SmartReader**
  (Readability port, 189 stars, Apache) — rejected: built to extract article bodies, not
  discrete fields, and its byline/author metadata is a PII vector.

## D10. robots.txt: in-house RFC 9309 evaluator

- **Decision**: Implement a small in-house RFC 9309 evaluator, using the MIT-licensed
  `Robots.Txt.Parser` source as a reference. See
  [Complexity Tracking](./plan.md#complexity-tracking).
- **Rationale**: Gate I-1 is non-waivable and non-overridable, and the .NET ecosystem has no
  mature option — the most credible package (`Robots.Txt.Parser` 1.0.0, MIT, explicitly RFC
  9309-targeted, `net10.0`) has **1 GitHub star**, and the better-known
  `RobotsExclusionTools` has been stale since 2024-09-03 and predates RFC 9309's publication,
  so its precedence rules may not match the final spec. Taking a supply-chain dependency with
  near-zero adoption on the single most important constitutional gate is the worse risk; the
  evaluator is roughly 150 lines and fully unit-testable against RFC 9309 vectors.
- **Must handle**: most-specific `User-agent` group (exact beats `*`); **longest-match wins,
  `Allow` beats `Disallow` on equal length**; `*` and `$` matching; case-sensitive paths with
  RFC 3986 percent-encoding normalized before comparison; unknown directives ignored;
  `#` comments; BOM stripped; a 500 KiB parse cap; cache up to 24h with last-known-good
  fallback; **4xx ⇒ unrestricted, 5xx or unreachable ⇒ assume full disallow** (fail-safe);
  `Crawl-delay` and `Sitemap` honoured though non-standard.
- **Alternatives considered**: **Vendor `Robots.Txt.Parser`'s source** — a close second and
  still open if the in-house evaluator proves tedious; MIT permits it. **Depend on it as a
  package** — rejected on adoption risk. **`RobotsExclusionTools`** — rejected as stale and
  pre-RFC.

## D11. Politeness: partitioned token bucket + Polly v8 on the one egress client

- **Decision**: `System.Threading.RateLimiting` 10.0.12 `PartitionedRateLimiter` keyed by host
  using a **token bucket** (capacity 1, replenishment period = effective crawl-delay), plus
  `Microsoft.Extensions.Http.Resilience` 10.10.0 (Polly v8) for exponential backoff **with
  jitter**, timeout, and 429/transport handling — all attached to a single named
  `IHttpClientFactory` client that is the only egress path in the service.
- **Rationale**: Token bucket expresses "one request per crawl-delay, refilling" directly, which
  is what FR-006 and robots.txt `Crawl-delay` mean. **Fixed window is wrong** — it permits a
  burst at each window boundary, precisely what per-host politeness must prevent. Attaching
  everything to one named client satisfies gate T-3 and makes the robots check (D10)
  unbypassable: there is no second way out of the process.
- **Implementation note**: `.AddStandardResilienceHandler()` does **not** provide per-host
  partitioning. The custom `.AddResilienceHandler()` + `PartitionedRateLimiter` combination is
  required, with `request.RequestUri.Host` stamped onto the `ResilienceContext` by a
  `DelegatingHandler` ordered *before* the resilience handler.
- **Alternatives considered**: **Sliding window** — acceptable, smoother; token bucket maps more
  directly onto crawl-delay. **Hand-rolled `SemaphoreSlim` per host** — rejected, reinvents a
  first-party primitive and gets queue ordering and replenishment subtly wrong.

## D12. JS-rendered pages: an extraction failure, not a Playwright fallback

- **Decision**: No headless browser in v1. A page whose car content materialises only after
  client-side script execution is recorded as a typed extraction failure, and such hosts are
  excluded from the source list.
- **Rationale**: `Microsoft.Playwright` 1.64.0 is first-party and viable in a container, but it
  imports `playwright install` at image-build time, 300 MB+ of browser binaries, OS deps
  (fonts, GPU libs), and tens-to-hundreds of MB of RAM per page. The spec's edge case for
  JS-only pages permits either resolution or a recorded failure, and FR-022 forbids letting one
  target threaten the rest — a hung browser does exactly that. Failing loudly is also the
  honest outcome: the operator learns the host is unsupported instead of inheriting a flaky,
  slow path.
- **Revisit if**: a large, known fraction of real sources hydrate JSON-LD client-side —
  detectable by diffing fetched against rendered HTML on a sample. Then isolate Playwright in
  its own worker/queue under the same per-host limiter.

## D13. Change detection: canonical hash of the allowlisted projection

- **Decision**: SHA-256 over a canonical serialization (stable key order, normalized whitespace
  and casing) of the allowlisted record. `ETag`/`If-None-Match` and `Last-Modified` are sent
  opportunistically as a bandwidth optimization, but the hash is the mechanism correctness
  depends on.
- **Rationale**: Hashing the projection rather than raw bytes detects *meaningful* change
  (price, mileage) while ignoring cosmetic churn (rotated banner URLs, "47 people viewed this"),
  because those fields never enter the record. It also hashes exactly the validated, committed
  fields. This delivers FR-018 and SC-008 without depending on caching headers whose real
  behaviour on listing sites could not be verified (see
  [Unresolved risks](#unresolved-risks)).
- **Alternatives considered**: **Conditional requests as the primary mechanism** — rejected,
  unverified support and commercial listing pages are commonly `no-store`. **Hashing raw HTML**
  — rejected, every ad slot and view counter would read as a change.

## D14. Migrations: checked-in EF migrations, separate elevated migrator identity

- **Decision**: `modelBuilder.HasPostgresExtension("vector")` so `dotnet ef migrations add`
  emits `CREATE EXTENSION IF NOT EXISTS "vector"`. Migrations run via `Database.Migrate()` /
  `dotnet ef database update` under a **dedicated elevated migrator connection string**,
  separate from the app's least-privilege runtime account. **`EnsureCreated()` appears nowhere.**
  The generated `Down()` for the extension is reviewed by hand before being trusted.
- **Rationale**: Resolves gate III-3. `CREATE EXTENSION` requires elevated privilege, so a
  correctly least-privileged runtime account **will fail** on `Migrate()` — splitting the
  identity is the fix, not granting the app superuser. `EnsureCreated()` does create the
  extension but is incompatible with a migrations history, which the constitution requires.
  EF's auto-generated `Down()` tends to emit `DROP EXTENSION`, which fails or cascades while
  vector columns still reference the type — so reversibility needs the column drops ordered
  first, or a deliberate no-op.
- **Deployment note**: Azure Flexible Server additionally needs `azure_pg_admin` membership and
  `vector` in the `azure.extensions` allow-list; AWS RDS/Aurora on PG13+ needs only ordinary
  `CREATE` on the database.

## D15. Testing: xUnit, synthetic fixtures, WireMock.Net, Testcontainers

- **Decision**: Three tiers. (1) Hand-rolled `HttpMessageHandler` stubs for extraction unit
  tests and the PII assertions. (2) **WireMock.Net** 2.18.0 for HTTP-pipeline integration tests
  — per-host rate limiting, 429/503 backoff, `Retry-After`, robots fetch and cache behaviour.
  (3) **Testcontainers** with `pgvector/pgvector:pg17`, started in an xUnit `IAsyncLifetime`
  fixture running the checked-in migrations, for storage tests.
- **Fixtures are synthetic HTML authored by us**, matching the real JSON-LD shape — **not
  recorded live captures.** Recording live traffic from these sites into committed fixtures is a
  ToS wrinkle and risks committing real seller PII into the repository, which would violate
  FR-010 in the one place nobody thinks to look.
- **Rationale**: Satisfies gate W-2 (no live network in tests). The container's default role has
  enough privilege for `CREATE EXTENSION`, so the D14 privilege split does not complicate tests.
- **Prerequisite, verified**: the Docker **CLI is installed (29.4.3) but the daemon is not
  running** on this machine, and no `psql` client is present. Tier 3 will fail until Docker
  Desktop is started — recorded in [quickstart.md](./quickstart.md) rather than left to surprise.
- **Alternatives considered**: **VCR cassettes** — `Scotch` is stale since 2022; `EasyVCR` is
  maintained but thinly adopted, and the live-recording model is what we are avoiding anyway.
  **A shared developer Postgres** — rejected, tests would not be isolated or reproducible.

---

## Unresolved risks

Two findings could **not** be verified, and both are load-bearing. They are recorded here rather
than assumed away, because the design's exposure to them is asymmetric.

### R1. JSON-LD presence on real target sites is unverified — HIGH

Research attempted to fetch live listing pages and was blocked by **HTTP 403 at the Cloudflare
edge before any content was served**. Reported protections on major aggregators (secondary
sources only): Autotrader — Cloudflare plus custom; CarGurus — PerimeterX/DataDome, TLS/HTTP2
fingerprinting, datacenter IPs blocked outright; Cars.com — Cloudflare plus custom.

Two consequences:

1. **D9's JSON-LD-first strategy is unvalidated against real targets.** schema.org `Car` is
   well-established and Google's rich-result guidance gives *dealer* sites a real incentive to
   emit it faithfully — but that incentive is weaker for aggregators, and a secondary claim that
   one aggregator embeds a full JSON blob could not be confirmed as schema.org-shaped rather
   than a bespoke internal payload. The design must therefore treat JSON-LD as possibly
   **absent** on a meaningful fraction of sources, not merely malformed. D9 already fails loud
   rather than silently falling back, so absence surfaces as a reported failure — but if absence
   is widespread, per-host CSS extractors become the norm rather than the exception, which
   changes the effort profile materially.
2. **Bot protection may make several named sites ineligible regardless.** FR-007 forbids
   circumventing bot defences and FR-005 forbids requesting disallowed paths. A site behind
   DataDome that fingerprints the TLS stack has, in effect, declined automated access — the
   constitutional response is to exclude that host, not to defeat the check. This may shrink the
   viable target list substantially, and it is a scoping question for the operator, not a
   technical obstacle to route around.

**Resolution path**: before building per-host extractors, validate empirically against the
*actual* source list the operator intends to use — fetch one page per host, honouring robots.txt,
and record whether a schema.org `Car`/`Vehicle` node is present. This is the first task in
Phase 2 and it gates the extractor design. **It cannot be resolved from documentation.**

### R2. Cache-header behaviour on listing pages is unverified — LOW

The only headers observed (`cache-control: private, max-age=0, no-store, no-cache,
must-revalidate`) came from the bot-challenge page, not the listing origin, so they say nothing
about real conditional-request support. Commercial, personalization-heavy listing pages commonly
send weak or absent caching headers, but this was not confirmed for any specific site.

**Why the exposure is low**: D13 already makes the canonical projection hash the mechanism
correctness depends on, and treats `ETag`/`Last-Modified` purely as an optimization. If
conditional requests turn out to be universally unsupported, the system is slower, not wrong.

### R3. Minor unverified items

- `Pgvector.EntityFrameworkCore` 0.2.2 → 0.3.0 changelog beyond the EF Core 9/10 compatibility
  claim — no changelog found.
- Testcontainers' `PostgreSqlBuilder` constructor-argument form conflicted between sources;
  `.WithImage("pgvector/pgvector:pg17")` is the corroborated API and is what D15 assumes.
- `Microsoft.ML.Tokenizers` GA-vs-preview status — irrelevant now that D5 chose Ollama over raw
  ONNX, but it would matter if that decision were revisited.
- Whether OpenAI or Voyage give any **contractual** guarantee against silently mutating a named
  model's weights. Only circumstantial practice was found (dated model names). D5's
  method+version stamping is the correct defensive posture regardless — a silent weight change
  would otherwise shift the whole corpus's vector space with **no error**, since old and new
  vectors remain dimensionally valid and pass through pgvector's `<=>` operator unflagged. This
  is precisely the failure FR-029 exists to prevent.
- `Microsoft.Playwright`'s license (MIT for the .NET binding, Apache for Playwright core) —
  moot while D12 holds.
