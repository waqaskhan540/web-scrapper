# Implementation Plan: Car Data Vector Ingestion

**Branch**: `001-car-data-vector-ingestion` | **Date**: 2026-10-11 | **Spec**: [spec.md](./spec.md)

**Input**: Feature specification from `/specs/001-car-data-vector-ingestion/spec.md`

> This project is not a git repository, so `001-car-data-vector-ingestion` is the feature
> directory name rather than an actual branch. Downstream commands locate it via
> `.specify/feature.json`.

## Summary

Ingest car-listing pages whose addresses are supplied in plain-text files in a configured
folder, extract only allowlisted non-personal vehicle fields from each page, embed a templated
serialization of those fields, and store the vector in Postgres/pgvector alongside complete
provenance — with an operator-only query to prove the stored vectors are retrievable by meaning.

Technical approach: a single ASP.NET Core 10 host exposes a versioned control API and runs
ingestion in an in-process `BackgroundService` fed by a bounded `Channel`. All egress flows
through one named `IHttpClientFactory` client guarded by an in-house RFC 9309 robots evaluator
and a per-host token-bucket limiter, which makes the politeness checks structurally
unbypassable. Extraction reads schema.org JSON-LD via AngleSharp into strict records that have
no catch-all member, so personal data has no field to occupy. Embeddings go through
`IEmbeddingGenerator` (Ollama / `nomic-embed-text`, 768-dim) wrapped to stamp model identity on
every vector. Vectors live in their own table keyed by model id and version, unindexed — exact
search is ample at this scale.

Full decision record with alternatives: [research.md](./research.md).

## Technical Context

**Language/Version**: C# 14 on **.NET 10** (`net10.0`, current LTS — SDK 10.0.102 verified
present locally). Nullable reference types enabled; warnings as errors.

**Primary Dependencies**:

| Concern | Package | Version |
|---------|---------|---------|
| Web host + API | `Microsoft.AspNetCore.App` (framework reference) | 10.0.x |
| ORM + Postgres | `Npgsql.EntityFrameworkCore.PostgreSQL` | 9.0.1+ (EF Core 10) |
| Vector type | `Pgvector`, `Pgvector.EntityFrameworkCore` | 0.3.2 / 0.3.0 |
| Embedding abstraction | `Microsoft.Extensions.AI` (+ `.Abstractions`) | 10.9.0 (GA) |
| Embedding provider | `OllamaSharp` | current |
| HTML/DOM | `AngleSharp` | 1.8.4 |
| JSON-LD | `System.Text.Json` | in-box |
| Per-host rate limiting | `System.Threading.RateLimiting` | 10.0.12 |
| Retry/backoff/timeout | `Microsoft.Extensions.Http.Resilience` (Polly v8) | 10.10.0 |
| robots.txt | in-house evaluator (see [Complexity Tracking](#complexity-tracking)) | — |
| Tests | `xUnit`, `WireMock.Net` 2.18.0, `Testcontainers.PostgreSql` | current |

Deliberately **not** taken: Hangfire/Quartz (nothing schedules), `Microsoft.Playwright`
(JS-only pages fail loudly instead), SmartReader (article extractor, wrong tool, PII vector in
byline metadata), `Microsoft.Extensions.AI.Ollama` (**deprecated** — OllamaSharp replaces it).

**Storage**: PostgreSQL 17 with the `pgvector` extension (v0.8.x). Embeddings in
`car_document_embedding` as an **unindexed** `vector(768)` column; similarity queries always
filter `model_id` + `model_version`. Schema evolves only through checked-in, ordered,
reversible EF Core migrations applied by a **separate elevated migrator identity** — the app's
runtime account is least-privilege and cannot `CREATE EXTENSION`. `EnsureCreated()` appears
nowhere.

**Testing**: xUnit. Three tiers — `HttpMessageHandler` stubs for extraction and PII assertions;
WireMock.Net for the HTTP pipeline (rate limiting, 429/503 backoff, `Retry-After`, robots cache
behaviour); Testcontainers `pgvector/pgvector:pg17` running the real migrations for storage.
Fixtures are **synthetic HTML authored in-repo**, never recorded live captures. No live network
in any test.

**Target Platform**: Linux container (also runs on macOS/Windows for development). Long-running
service. Requires a reachable Postgres and, by default, a reachable Ollama endpoint.

**Project Type**: Web service — versioned HTTP control API plus an in-process background worker.

**Performance Goals**: Throughput is deliberately bounded by politeness, not by compute: one
request per host per effective crawl-delay. A 1,000-address run completes unattended and a
10,000-address run completes without exhausting memory (SC-002), which requires streaming the
address list and bounding the channel rather than materializing a run in memory. Validation
query: top-10 over 100k vectors under 200 ms via exact scan.

**Constraints**:

- robots.txt is authoritative and **not overridable in production** (non-waivable).
- Per-host rate ceiling and bounded per-host concurrency; exponential backoff **with jitter**.
- **No personal data** stored, logged, or exposed — including in error logs. No free-text seller
  description in v1 at all.
- Raw page bytes are never persisted; only the screened projection is retained.
- Every stored document carries source address, UTC retrieval time, extractor version, and
  means of discovery, or it is not stored.
- Similarity comparison never crosses embedding model identity.
- Async end-to-end with `CancellationToken` honoured; no blocking on async outside entry points.

**Scale/Scope**: Tens of thousands of documents (not millions); a handful of known hosts;
single operator; single instance. Multi-tenancy, sharding, and horizontal scale-out are out of
scope. At this scale an ANN index is unnecessary — exact scan over 10k–100k vectors is
commonly sub-50 ms.

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

Gates derived from `.specify/memory/constitution.md` v1.0.0. `PASS` = the design satisfies it;
`GATE` = unresolved at that checkpoint.

### Principle I — Ethical & Compliant Collection (NON-NEGOTIABLE)

| Gate | Requirement | Design response | Pre-research | Post-design |
|------|-------------|-----------------|--------------|-------------|
| I-1 | robots.txt resolved and respected before requesting a path; re-checked on a cadence; not operator-overridable in production | In-house RFC 9309 evaluator inside the single egress path; 4xx ⇒ unrestricted, 5xx/unreachable ⇒ full disallow; 24h cache with last-known-good (D10) | PASS | **PASS** |
| I-2 | Per-host rate ceiling, bounded per-host concurrency, backoff with jitter on 429/503/transport | `PartitionedRateLimiter` token bucket keyed by host, replenishment = effective crawl-delay; Polly v8 exponential backoff with jitter (D11) | PASS | **PASS** |
| I-3 | Descriptive User-Agent with contact; no identity rotation; no circumventing auth/paywall/bot defence | One configured UA on the one egress client; no proxy or UA rotation exists in the design; hosts that fingerprint and block are excluded, not defeated (R1) | PASS | **PASS** |
| I-4 | No personal data collected, persisted, logged, or exposed; extraction selects permitted fields rather than store-then-filter | Strict record with **no catch-all member**; **no free-text description in v1**; raw bytes never persisted; only record fields loggable; asserted by literal-string tests + CI pattern list (D8) | GATE | **PASS** |

**I-4 resolved.** The pre-research risk was that regex scrubbing of free text has false
negatives that cannot be proven zero — personal names especially. Dropping the description
field makes the guarantee structural (no field exists to hold personal data) rather than
probabilistic. The retrieval cost is real and is recorded in [spec.md](./spec.md) assumptions;
re-introducing the field is a per-host opt-in requiring documented PII review.

### Principle II — Resilient, Observable Collection Jobs

| Gate | Requirement | Design response | Pre-research | Post-design |
|------|-------------|-----------------|--------------|-------------|
| II-1 | Extraction isolated per target; one failure cannot fail the run or the API surface | Per-address outcome row; extractor faults caught at the address boundary; worker isolated from request handling by the channel | PASS | **PASS** |
| II-2 | Loud failure on schema drift; no silent null/empty/partial writes | Typed schema-drift failure naming missing fields; mandatory-field validation before persist; JSON-LD absence fails loudly instead of silently falling back to CSS (D9) | PASS | **PASS** |
| II-3 | Structured logs per run: target, start/end, request count, stored, rejected, outcome | Run row is the system of record; logs carry the same fields keyed by run id | PASS | **PASS** |
| II-4 | Fetch / extract / persist separable; persist or transform failure retryable without re-fetch | Screened projection persisted **before** embedding, so an embedding failure retries without refetching (FR-017, SC-010) | PASS | **PASS** |
| II-5 | Health endpoint reporting per-target last-success time | `/health` reads last successful run per source | PASS | **PASS** |

### Principle III — Stable Contracts & Traceable Data

| Gate | Requirement | Design response | Pre-research | Post-design |
|------|-------------|-----------------|--------------|-------------|
| III-1 | Responses from explicit API-owned response types; never internal entities or DB models | Response records live in the API project; entities never cross the storage boundary (see [contracts/](./contracts/)) | PASS | **PASS** |
| III-2 | HTTP API versioned; field removal/retyping is breaking | Routes under `/v1/` from the first commit | PASS | **PASS** |
| III-3 | Schema changes as checked-in, ordered, reversible migrations; nothing by hand or inferred at runtime | `HasPostgresExtension("vector")` emits `CREATE EXTENSION`; `Database.Migrate()` under a separate elevated migrator identity; no `EnsureCreated()`; generated `Down()` hand-reviewed (D14) | GATE | **PASS** |
| III-4 | Every record carries provenance: source URL, UTC retrieval time, extractor version, means of discovery | Provenance columns non-nullable — a row without them cannot be written | PASS | **PASS** |
| III-5 | Timestamps stored and served as UTC | `timestamptz` throughout; UTC serialization | PASS | **PASS** |

**III-3 resolved.** Two traps were confirmed and designed around: `CREATE EXTENSION` requires
elevated privilege, so a correctly least-privileged runtime account **would fail** on
`Migrate()` — hence the separate migrator identity rather than granting the app superuser. And
EF's auto-generated `Down()` emits `DROP EXTENSION`, which fails or cascades while vector
columns still reference the type, so reversibility requires the column drops ordered first.

### Technology & Platform Constraints

| Gate | Requirement | Pre-research | Post-design |
|------|-------------|--------------|-------------|
| T-1 | Current .NET LTS; no preview runtimes or preview language features | GATE | **PASS** — `net10.0`, verified locally (D1) |
| T-2 | Nullable reference types on; warnings as errors; suppressions narrow and justified | PASS | **PASS** |
| T-3 | All outbound calls via `IHttpClientFactory`; policies at client level | PASS | **PASS** — one named client is the only egress path |
| T-4 | Async end-to-end; `CancellationToken` honoured; no blocking on async | PASS | **PASS** |
| T-5 | Per-host policies, schedules, rate ceilings in configuration | PASS | **PASS** |
| T-6 | Secrets not committed; environment or secret store | PASS | **PASS** — two connection strings (runtime, migrator) both external |
| T-7 | Each package justified; deferred choices fixed here and then reused | GATE | **PASS** — see Primary Dependencies, incl. what was declined |

### Development Workflow & Quality Gates

| Gate | Requirement | Pre-research | Post-design |
|------|-------------|--------------|-------------|
| W-1 | Spec Kit sequence followed | PASS — spec ratified 16/16 | **PASS** |
| W-2 | Extractors tested against checked-in fixtures; no live network in tests | PASS | **PASS** — synthetic fixtures, not live captures (D15) |
| W-3 | Every new target ships with its host policy entry and ≥1 fixture test | PASS | **PASS** — enforced by FR-008 |
| W-4 | Clean build: no warnings, all tests passing | PASS | **PASS** |
| W-5 | Reviews verify Principle I on any change touching fetch, logging, or persistence | PASS | **PASS** |

### Verdict

**Pre-research**: PASS with 4 items requiring Phase 0 resolution (I-4, III-3, T-1, T-7).

**Post-design**: **PASS — all 26 gates satisfied.** All four open items resolved by
[research.md](./research.md). One entry in [Complexity Tracking](#complexity-tracking).

Two risks remain open but are **not** gate violations — they are empirical questions that
documentation cannot settle, carried forward as the first task of Phase 2:

- **R1 (HIGH)**: JSON-LD presence on the operator's real target sites is unverified; research
  was blocked at the Cloudflare edge before any page content was served. If JSON-LD is widely
  absent, per-host CSS extractors become the norm rather than the exception. Separately, hosts
  that fingerprint and block have in effect declined automated access — the constitutional
  response under I-3 is to exclude them, which may shrink the viable target list.
- **R2 (LOW)**: real cache-header behaviour is unverified. D13 already makes the projection hash
  the mechanism correctness depends on, so unsupported conditional requests make the system
  slower, not wrong.

## Project Structure

### Documentation (this feature)

```text
specs/001-car-data-vector-ingestion/
├── plan.md              # This file (/speckit-plan command output)
├── research.md          # Phase 0 output (/speckit-plan command)
├── data-model.md        # Phase 1 output (/speckit-plan command)
├── quickstart.md        # Phase 1 output (/speckit-plan command)
├── contracts/           # Phase 1 output (/speckit-plan command)
│   ├── README.md
│   ├── ingestion-api.v1.md
│   └── openapi.v1.yaml
├── checklists/
│   └── requirements.md  # Spec quality checklist (16/16)
└── tasks.md             # Phase 2 output (/speckit-tasks command - NOT created by /speckit-plan)
```

### Source Code (repository root)

```text
CarIngest.sln

src/
├── CarIngest.Core/                   # domain + abstractions; no I/O dependencies
│   ├── Documents/                    # CarDocument, allowlisted field record, canonical hash
│   ├── Sources/                      # address-list parsing, de-duplication
│   ├── Runs/                         # IngestionRun, AddressOutcome, typed failure reasons
│   ├── Policies/                     # HostAccessPolicy, robots evaluation contract
│   └── Abstractions/                 # IPageFetcher, ICarExtractor, IDocumentStore, ...
├── CarIngest.Robots/                 # in-house RFC 9309 parser + evaluator (see Complexity)
├── CarIngest.Infrastructure/
│   ├── Fetching/                     # named HttpClient, host-stamping handler, token bucket
│   ├── Extraction/                   # AngleSharp + JSON-LD, per-host extractors, drift errors
│   ├── Embedding/                    # IEmbeddingGenerator wiring + model-stamping decorator
│   └── Storage/                      # EF Core DbContext, entity config, Migrations/
└── CarIngest.Api/                    # host: /v1 endpoints, response records, worker, health
    ├── Endpoints/
    ├── Contracts/                    # API-owned response types (never entities)
    └── Ingestion/                    # BackgroundService + bounded Channel

tests/
├── CarIngest.Core.Tests/             # hashing, dedup, address parsing, outcome transitions
├── CarIngest.Robots.Tests/           # RFC 9309 vectors: precedence, wildcards, fail-safe
├── CarIngest.Infrastructure.Tests/   # extraction fixtures, PII assertions, WireMock pipeline,
│                                     # Testcontainers storage + migrations
└── CarIngest.Api.Tests/              # endpoint contract tests against contracts/

fixtures/
└── <host>/                           # synthetic listing HTML authored in-repo, never captures
    ├── valid-jsonld.html
    ├── missing-required-field.html
    ├── seller-pii-present.html        # asserts PII never reaches the stored record
    └── jsonld-absent.html
```

**Structure Decision**: Four production projects and four test projects. `CarIngest.Core` holds
the domain and abstractions with no I/O dependencies, so the fetch/extract/persist stage
separation that Principle II-4 requires is enforced by project references rather than
convention. `CarIngest.Robots` is deliberately its own assembly: gate I-1 is non-waivable, and
isolating the evaluator keeps it auditable and independently testable against RFC 9309 vectors
without pulling in EF Core or AngleSharp. `CarIngest.Infrastructure` keeps the four
stage implementations in sibling folders rather than four assemblies — the constitution's
lightweight principle set makes assembly proliferation the thing to justify, and nothing yet
needs to deploy or version them apart. `CarIngest.Api` owns its response records so no entity
can leak into a response (III-1). `fixtures/` sits at the repository root rather than under
`tests/` because extraction fixtures are shared across the infrastructure and API test projects.

## Complexity Tracking

> **Fill ONLY if Constitution Check has violations that must be justified**

One entry. It is not a constitutional violation — it is a deliberate build-over-buy choice that
gate T-7 ("each added third-party package MUST be justified") requires recording, since here the
justification runs the other way: declining a package in favour of our own code.

| Violation | Why Needed | Simpler Alternative Rejected Because |
|-----------|------------|-------------------------------------|
| In-house robots.txt evaluator (`CarIngest.Robots`) instead of a NuGet package | Gate I-1 is explicitly non-waivable and non-overridable, so robots handling is the single most consequential component in the service. The .NET ecosystem offers no mature option: the most credible package (`Robots.Txt.Parser` 1.0.0, MIT, explicitly RFC 9309-targeted, `net10.0`) has **1 GitHub star**, and the better-known `RobotsExclusionTools` has been stale since 2024-09-03 and predates RFC 9309's publication, so its precedence rules may not match the final spec. The evaluator is ~150 lines and fully testable against published RFC vectors. | **Depend on `Robots.Txt.Parser`**: taking a near-zero-adoption supply-chain dependency on the one gate that cannot be waived is the larger risk; a silent behavioural bug there means unlawful requests, not a failed test. **Vendor its MIT source**: a genuine second choice and still open — rejected only because auditing vendored code to the standard this gate demands costs about what writing it costs, without owning the test suite. **`RobotsExclusionTools`**: stale and pre-RFC-9309. **Skip robots handling**: forbidden outright by Principle I. |
