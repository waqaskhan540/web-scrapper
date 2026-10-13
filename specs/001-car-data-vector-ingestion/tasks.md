---
description: "Task list for Car Data Vector Ingestion"
---

# Tasks: Car Data Vector Ingestion

**Input**: Design documents from `/specs/001-car-data-vector-ingestion/`

**Prerequisites**: [plan.md](./plan.md), [spec.md](./spec.md), [research.md](./research.md),
[data-model.md](./data-model.md), [contracts/](./contracts/), [quickstart.md](./quickstart.md)

**Tests**: **REQUIRED, not optional.** The project constitution makes them gates, not a
preference — W-2 (extractors tested against checked-in fixtures, no live network), W-3 (every
new target ships with its host policy entry and ≥1 fixture test), W-4 (clean build, all tests
passing). Test tasks below are therefore ordinary required work.

**Organization**: Grouped by user story so each is independently implementable and testable.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Can run in parallel (different files, no dependencies)
- **[Story]**: Which user story the task belongs to (US1–US4)
- Exact file paths are included in every task

## Path Conventions

Four production projects and four test projects under `src/` and `tests/`, with shared
extraction fixtures at the repository root — per
[plan.md](./plan.md#source-code-repository-root).

---

## Phase 1: Setup (Shared Infrastructure)

**Purpose**: Solution skeleton and pinned toolchain.

- [X] T001 Create `CarIngest.sln` and the eight project files — `src/CarIngest.Core`, `src/CarIngest.Robots`, `src/CarIngest.Infrastructure`, `src/CarIngest.Api`, `tests/CarIngest.Core.Tests`, `tests/CarIngest.Robots.Tests`, `tests/CarIngest.Infrastructure.Tests`, `tests/CarIngest.Api.Tests` — with project references wired so `CarIngest.Core` references nothing (no I/O dependencies, enforcing the Principle II-4 stage separation)
- [X] T002 Create `Directory.Build.props` at repository root setting `<TargetFramework>net10.0`, `<Nullable>enable`, `<TreatWarningsAsErrors>true`, `<EnforceCodeStyleInBuild>true`, and `<LangVersion>latest` with no preview features (constitution gates T-1, T-2)
- [X] T003 [P] Create `Directory.Packages.props` with central package management pinning exactly the versions in [plan.md](./plan.md#technical-context): `Npgsql.EntityFrameworkCore.PostgreSQL` 9.0.1+, `Pgvector` 0.3.2, `Pgvector.EntityFrameworkCore` 0.3.0, `Microsoft.Extensions.AI` 10.9.0, `OllamaSharp`, `AngleSharp` 1.8.4, `System.Threading.RateLimiting` 10.0.12, `Microsoft.Extensions.Http.Resilience` 10.10.0, `xunit`, `WireMock.Net` 2.18.0, `Testcontainers.PostgreSql`
- [X] T004 [P] Create `.editorconfig` and `.gitignore` at repository root, excluding `bin/`, `obj/`, `appsettings.*.local.json`, and any `*.env` so secrets cannot be committed (gate T-6)
- [X] T005 [P] Create `docker-compose.dev.yml` at repository root with a `pgvector/pgvector:pg17` service and an `ollama` service, matching [quickstart.md](./quickstart.md#setup)
- [X] T006 [P] Create `fixtures/README.md` stating that fixtures are synthetic HTML authored in-repo and that recording live captures is prohibited — it risks committing real seller PII and breaches target-site terms (research D15)

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: Cross-cutting infrastructure and the shared entity model. All four stories read and
write these entities, so they live here rather than in the earliest story.

**⚠️ CRITICAL**: No user story work can begin until this phase is complete.

### Risk R1 — resolve before designing extractors

- [X] T007 Resolve research risk R1: for each host the operator intends to ingest, fetch exactly one listing page while honouring that host's `robots.txt`, and record whether a schema.org `Car`/`Vehicle`/`Product` JSON-LD node is present, which fields it carries, and whether the host is reachable without defeating bot protection. Write findings to `specs/001-car-data-vector-ingestion/r1-findings.md`. **This gates T030–T033**: if JSON-LD is widely absent, per-host CSS extractors become the norm rather than the exception. Hosts that fingerprint and block have declined automated access — record them as excluded, never circumvent them (FR-007, research R1)

### Configuration and core domain

- [X] T008 [P] Create `src/CarIngest.Core/Configuration/CarIngestOptions.cs` with `Sources`, `UserAgent`, `HostPolicies`, `Embedding`, and `MaxContentBytes`; all per-host policy and rate values come from configuration, never code (gate T-5)
- [X] T009 [P] Create `src/CarIngest.Core/Policies/HostAccessPolicy.cs` with `Host`, `RequestsPerPeriod` (> 0), `ReplenishmentPeriod` (> 0), `MaxConcurrency` (> 0), `PermittedFields` (NOT EMPTY), `RobotsEvaluatedAt`, and `ExtractorKind` (`json-ld` or `css:<profile>`)
- [X] T010 Create `src/CarIngest.Core/Configuration/CarIngestOptionsValidator.cs` implementing `IValidateOptions<CarIngestOptions>` that fails **startup** when any `PermittedFields` entry is outside the allowlisted field set, so a configuration error surfaces at boot rather than as a mid-run extraction failure (data-model §3)
- [X] T011 [P] Create `src/CarIngest.Core/Documents/VehicleFields.cs` as the allowlisted projection record: `Make` (NOT NULL), `Model` (NOT NULL), `Year` (NOT NULL, 1900 ≤ year ≤ current year + 2), `Trim?`, `MileageValue?` (≥ 0, ≤ 1,000,000), `MileageUnit?` (`mi`|`km`, NOT NULL when `MileageValue` present), `PriceAmount?` (decimal, ≥ 0, ≤ 10,000,000), `PriceCurrency?` (ISO 4217, NOT NULL when `PriceAmount` present), `FuelType?`, `Transmission?`, `BodyStyle?`, `Colour?`, `Vin?` (exactly 17 chars when present). **No free-text description field and no `Dictionary<string, object>` / `JsonDocument` catch-all member** — the CLR type itself must make unmapped source data unrepresentable (research D8, gate I-4)
- [X] T012 [P] Create `src/CarIngest.Core/Documents/CanonicalHash.cs` computing SHA-256 over a canonical serialization of `VehicleFields` with stable key order and normalized whitespace and casing — never over raw HTML, so cosmetic churn such as rotated banner URLs or view counters does not read as content change (research D13, SC-008)
- [X] T013 [P] Create `src/CarIngest.Core/Runs/ReasonCode.cs` as the closed set of 17 codes with their outcome mapping exactly as listed in [data-model.md](./data-model.md#reason-codes): `robots-disallowed`, `robots-unavailable`, `no-host-policy`, `auth-required`, `malformed-address`, `fetch-timeout`, `fetch-http-error`, `rate-limited-persistently`, `content-too-large`, `jsonld-absent`, `jsonld-malformed`, `schema-drift`, `validation-range`, `js-render-required`, `not-a-car-page`, `embedding-unavailable`, `page-gone`
- [X] T014 [P] Create `src/CarIngest.Core/Abstractions/` with `IPageFetcher`, `ICarExtractor`, `IRobotsEvaluator`, `IDocumentStore`, `IRunStore`, and `IEmbeddingTextBuilder`; every method is async and accepts a `CancellationToken` (gate T-4)

### robots.txt — gate I-1, non-waivable

- [X] T015 [P] Create `tests/CarIngest.Robots.Tests/Rfc9309PrecedenceTests.cs` asserting published RFC 9309 vectors: most-specific `User-agent` group wins and an exact match beats `*`; **longest matched path wins, and `Allow` beats `Disallow` on equal length**; `*` and `$` wildcard matching; case-sensitive path comparison after RFC 3986 percent-encoding normalization
- [X] T016 [P] Create `tests/CarIngest.Robots.Tests/FailSafeTests.cs` asserting that a 4xx on `robots.txt` means unrestricted, while **5xx or unreachable means assume full disallow**, and that a malformed or over-500 KiB file does not throw out of the evaluator
- [X] T017 Create `src/CarIngest.Robots/RobotsFileParser.cs` parsing line-based UTF-8 with `#` comments, BOM stripped, unknown directives ignored for forward compatibility, a 500 KiB parse cap, and `Crawl-delay` plus `Sitemap` captured though non-standard (research D10)
- [X] T018 Create `src/CarIngest.Robots/RobotsEvaluator.cs` implementing the precedence rules from T015 and the fail-safe behaviour from T016, exposing `IsAllowed(userAgent, url)` and `CrawlDelay(userAgent)`. **No bypass parameter, override flag, or configuration switch may exist on this type** — Principle I-1 is non-overridable in production
- [X] T019 Create `src/CarIngest.Robots/RobotsCache.cs` caching per-host results for up to 24 hours with last-known-good fallback when a refresh fetch fails, so directives are re-checked on a cadence rather than cached for process lifetime (gate I-1)

### Persistence — gate III-3

- [X] T020 [P] Create `src/CarIngest.Infrastructure/Storage/Entities/` with `AddressSource`, `IngestionTarget`, `CarDocument`, `CarDocumentEmbedding`, `IngestionRun`, and `AddressOutcome` exactly as specified in [data-model.md](./data-model.md), with every provenance column (`SourceUrl`, `RetrievedAt`, `ExtractorVersion`, `DiscoveryMethod`, `LastVerifiedAt`) declared **non-nullable** so a row lacking provenance cannot be written (FR-015, gate III-4)
- [X] T021 Create `src/CarIngest.Infrastructure/Storage/CarIngestDbContext.cs` with entity configuration: `IngestionTarget.Url` UNIQUE; `CarDocument.IngestionTargetId` UNIQUE; `CarDocumentEmbedding` UNIQUE on `(CarDocumentId, ModelId, ModelVersion)` plus a non-unique index on `(ModelId, ModelVersion)`; a **partial unique index allowing at most one `running` IngestionRun per source** (FR-026); `AddressOutcome` UNIQUE on `(IngestionRunId, IngestionTargetId)`; all timestamps `timestamptz` (gate III-5); `DiscoveryMethod` mapped as `text` not an int-backed enum so adding a value stays additive (FR-015a)
- [X] T022 Configure pgvector in `src/CarIngest.Infrastructure/Storage/CarIngestDbContext.cs` and `src/CarIngest.Api/Program.cs`: call `modelBuilder.HasPostgresExtension("vector")`, map `CarDocumentEmbedding.Embedding` as `vector(768)` **unindexed** (exact scan is ample at this scale — research D4), and register **both** `dataSourceBuilder.UseVector()` and `UseNpgsql(cs, o => o.UseVector())` since one configures Npgsql type mapping and the other the EF provider (research D3)
- [X] T023 Generate the initial migration into `src/CarIngest.Infrastructure/Storage/Migrations/` via `dotnet ef migrations add InitialCreate`, then **hand-review the generated `Down()`**: EF emits `DROP EXTENSION "vector"`, which fails or cascades while vector columns still reference the type, so order the column drops first or make the extension drop a deliberate no-op (research D14, gate III-3)
- [X] T024 Wire two separate connection strings in `src/CarIngest.Api/Program.cs` — `Migrator` (elevated, deploy-time only, the sole identity that may `CREATE EXTENSION`) and `Runtime` (least-privilege, used by the service). **Assert `EnsureCreated()` appears nowhere in the solution**; a least-privileged runtime account failing on `Migrate()` is correct behaviour, and the fix is the migrator identity, not granting the app superuser (research D14)

### Egress — gates I-2, I-3, T-3

- [X] T025 Create `src/CarIngest.Infrastructure/Fetching/HostStampingHandler.cs`, a `DelegatingHandler` that writes `request.RequestUri.Host` onto the `ResilienceContext` and must be ordered **before** the resilience handler so the limiter can partition on it (research D11)
- [X] T026 Create `src/CarIngest.Infrastructure/Fetching/PerHostRateLimiter.cs` using `PartitionedRateLimiter.Create<ResilienceContext, string>` with `RateLimitPartition.GetTokenBucketLimiter` keyed by host. Token bucket, **not fixed window** — fixed window permits a burst at each window boundary, exactly what per-host politeness must prevent. Effective delay is `max(configured ReplenishmentPeriod, robots Crawl-delay)` so robots can only slow us down (FR-006, research D11)
- [X] T027 Register the single named `HttpClient` in `src/CarIngest.Api/Program.cs` with the configured descriptive `User-Agent` including a contact address, `.AddHttpMessageHandler<HostStampingHandler>()`, and `.AddResilienceHandler(...)` composing the T026 limiter, `AddRetry` with `DelayBackoffType.Exponential` and `UseJitter = true` handling 429/503/transport failures, and `AddTimeout`. Note `.AddStandardResilienceHandler()` does **not** provide per-host partitioning. **This client is the only egress path in the service**, which is what makes the robots check unbypassable (gates I-2, I-3, T-3)
- [X] T028 Create `src/CarIngest.Infrastructure/Fetching/PageFetcher.cs` implementing `IPageFetcher`: consult `IRobotsEvaluator` **before** issuing any request, enforce the `MaxContentBytes` cap returning `content-too-large`, detect sign-in/paywall/bot-challenge responses as `auth-required` without attempting circumvention, and never retain raw bytes beyond the extraction call (FR-005, FR-007, FR-012)

### Observability, API shell, test harnesses

- [X] T029 [P] Create `src/CarIngest.Api/Observability/LoggingSetup.cs` configuring structured logging scoped by run id, and a logging policy test in `tests/CarIngest.Infrastructure.Tests/LogScrubbingTests.cs` asserting that **no log sink ever receives a raw response body or a full parsed source object** — only `VehicleFields` members and status. Logging raw content on error is the commonest way to recreate a PII leak in the log sink (research D8, gate I-4)
- [X] T030 [P] Create `src/CarIngest.Api/Program.cs` endpoint routing under an explicit `/v1` prefix with RFC 9457 `application/problem+json` error handling whose `detail` carries identifiers and status only, never page content (gate III-2, contracts/README)
- [X] T031 [P] Create `tests/CarIngest.Infrastructure.Tests/Harness/PostgresFixture.cs` as an xUnit `IAsyncLifetime` fixture starting `new PostgreSqlBuilder().WithImage("pgvector/pgvector:pg17")`, running the checked-in migrations via `Database.Migrate()`, and exposing the connection string through `IClassFixture<T>` (research D15)
- [X] T032 [P] Create `tests/CarIngest.Infrastructure.Tests/Harness/StubHttpHandler.cs`, an `HttpMessageHandler` returning canned responses from a URL→fixture map, and `tests/CarIngest.Infrastructure.Tests/Harness/WireMockHarness.cs` wrapping WireMock.Net for pipeline tests. **No test may make a live network call** (gate W-2)
- [X] T033 [P] Author synthetic fixtures under `fixtures/dealer.example/`: `valid-jsonld.html`, `missing-required-field.html` (no `Year`), `jsonld-absent.html`, `jsonld-malformed.html`, `multiple-jsonld-blocks.html` (breadcrumb + organization + the real `Car` node), `js-render-required.html`, `seller-pii-present.html` (seller name, phone, and email in several DOM positions), and `robots.txt`. Hand-authored to match the real schema.org shape — **never recorded live captures** (research D15)

**Checkpoint**: Foundation ready. Entities, robots evaluation, the single egress path, migrations,
and test harnesses all exist. User story work can begin.

---

## Phase 3: User Story 1 — Ingest a batch of car pages (Priority: P1) 🎯 MVP

**Goal**: An operator drops a file of car-page addresses into the source folder, starts a run,
and ends up with attributable car documents carrying semantic vectors in Postgres.

**Independent Test**: Place a source file with a handful of known addresses, trigger a run, and
confirm a stored record exists for each permitted address with its source address and retrieval
time, and that the run summary counts match the input.

### Tests for User Story 1

- [X] T034 [P] [US1] Create `tests/CarIngest.Infrastructure.Tests/Extraction/PiiContainmentTests.cs` — the decisive test for gate I-4. Feed `fixtures/dealer.example/seller-pii-present.html`, serialize the resulting stored record, and assert the serialized output **contains none of the seller name, phone, or email literal strings**. Add a property test over randomized seller-block variants asserting the same invariant
- [X] T035 [P] [US1] Create `tests/CarIngest.Infrastructure.Tests/Extraction/JsonLdExtractionTests.cs` asserting: `valid-jsonld.html` yields a complete `VehicleFields`; `multiple-jsonld-blocks.html` selects the node by `@type` rather than taking the first `script[type='application/ld+json']`; `missing-required-field.html` raises `schema-drift` naming `Year` and stores nothing; `jsonld-absent.html` yields `jsonld-absent` and does **not** silently fall back to CSS selectors
- [X] T036 [P] [US1] Create `tests/CarIngest.Core.Tests/AddressListParsingTests.cs` asserting blank and `#` comment lines are ignored, a malformed line yields `malformed-address` reported individually without aborting the file, and the same address appearing twice within and across files is retrieved once (FR-003, FR-004)
- [X] T037 [P] [US1] Create `tests/CarIngest.Infrastructure.Tests/Fetching/RobotsEnforcementTests.cs` against WireMock asserting that a `robots.txt` returning 5xx produces `robots-unavailable` and that **no request was ever issued to the target path** — assert the absence of the request, not merely the recorded outcome
- [X] T038 [P] [US1] Create `tests/CarIngest.Infrastructure.Tests/Fetching/PolitenessTests.cs` against WireMock asserting consecutive requests to one host are separated by at least the effective crawl delay, that `max(configured, robots Crawl-delay)` wins, and that per-host concurrency is bounded rather than only globally (FR-006)
- [X] T039 [P] [US1] Create `tests/CarIngest.Api.Tests/RunEndpointContractTests.cs` validating `POST /v1/runs` and `GET /v1/runs/{id}` against [contracts/openapi.v1.yaml](./contracts/openapi.v1.yaml), including that a `202` carries a `Location` header and that at terminal status `addressesRead == stored + unchanged + skippedByPolicy + failed`
- [X] T040 [P] [US1] Create `tests/CarIngest.Infrastructure.Tests/Storage/ProvenanceTests.cs` asserting against the Testcontainers fixture that inserting a `CarDocument` with any provenance column null **fails at the database**, not merely in application validation (FR-015, SC-004)

### Implementation for User Story 1

- [X] T041 [P] [US1] Create `src/CarIngest.Core/Sources/AddressListReader.cs` streaming addresses from every `*.txt` file in the source folder, ignoring blank and `#` lines, and recording the supplying filename. Stream rather than materialize — a 10,000-address run must not exhaust memory (SC-002)
- [X] T042 [P] [US1] Create `src/CarIngest.Core/Sources/UrlNormalizer.cs` requiring an absolute `http`/`https` URL, lowercasing scheme and host, stripping the fragment and any trailing `?`, **preserving path case** (RFC 9309 path matching is case-sensitive) and **preserving the query string** since listing URLs commonly carry the vehicle id there (data-model §2)
- [X] T043 [US1] Create `src/CarIngest.Infrastructure/Extraction/JsonLdCarExtractor.cs` implementing `ICarExtractor`: AngleSharp locates `script[type='application/ld+json']` nodes, `System.Text.Json` parses each, the node is selected by `@type` ∈ {`Car`, `Vehicle`, `Product`}, and values are read **property by property into `VehicleFields`** via a mapping function whose return type has no catch-all member. A value found on the page but absent from the host's `PermittedFields` is discarded at projection time (research D8, D9)
- [X] T044 [US1] Add mandatory-field and sanity-range validation to `src/CarIngest.Infrastructure/Extraction/JsonLdCarExtractor.cs`: absence of `Make`, `Model`, or `Year` raises `schema-drift` naming the missing field; a value outside the T011 ranges raises `validation-range` rather than being silently clamped, since out-of-range values indicate schema abuse (research D9, FR-011)
- [X] T045 [US1] Create `src/CarIngest.Infrastructure/Extraction/JsonLdStalenessCheck.cs` cross-validating `PriceAmount` against a visible DOM anchor and flagging a mismatch, because JSON-LD can be generated by a different code path than the rendered page and drift out of sync (research D9)
- [X] T046 [US1] Create `src/CarIngest.Infrastructure/Extraction/JsRenderDetector.cs` returning `js-render-required` when car content materializes only after script execution. No headless browser is used — such hosts are excluded rather than supported (research D12)
- [X] T047 [P] [US1] Create `src/CarIngest.Core/Documents/EmbeddingTextBuilder.cs` templating `VehicleFields` into natural language, e.g. `"2018 Volkswagen Golf, 45,000 miles, GBP 12,000, diesel, manual, hatchback, blue"`. One text per document, **no chunking** — a listing is already one coherent semantic unit. The template is built from the already-screened projection, so personal data cannot reach the vector where it would be unredactable (research D6, D8)
- [X] T048 [P] [US1] Create `src/CarIngest.Infrastructure/Embedding/ModelStampingEmbeddingGenerator.cs` as a `DelegatingEmbeddingGenerator` that stamps `ModelId` and the resolved **digest** as `ModelVersion` onto every result. Recording the digest rather than the mutable tag is what makes a silent provider weight change detectable instead of invisibly shifting the whole corpus's vector space (FR-014, research D5, R3)
- [X] T049 [US1] Register the embedding provider in `src/CarIngest.Api/Program.cs` as `IEmbeddingGenerator<string, Embedding<float>>` via OllamaSharp's `OllamaApiClient` wrapped in T048, configurable to OpenAI by registration alone. Note the official `Microsoft.Extensions.AI.Ollama` package is **deprecated** — OllamaSharp replaces it (research D5)
- [X] T050 [US1] Create `src/CarIngest.Infrastructure/Storage/DocumentStore.cs` implementing `IDocumentStore`, persisting the screened projection **before** the embedding is generated so an embedding failure retries without re-fetching the page, recording `embedding-unavailable` when the generator is unreachable (FR-017, FR-025, SC-010)
- [X] T051 [US1] Create `src/CarIngest.Core/Runs/IngestionPipeline.cs` sequencing fetch → extract → persist → embed per address, catching every extractor and fetcher fault **at the address boundary** so one failing address cannot fail the run, and writing exactly one `AddressOutcome` per address (FR-020, FR-022)
- [X] T052 [US1] Create `src/CarIngest.Api/Ingestion/IngestionWorker.cs` as a `BackgroundService` consuming a bounded `Channel<RunRequest>`, honouring the host `CancellationToken`, so a long run executes off the request path and cannot fault the API surface (FR-022, research D2)
- [X] T053 [US1] Create `src/CarIngest.Api/Endpoints/RunEndpoints.cs` with `POST /v1/runs` returning `202 Accepted` with a `Location` header, and `409` when a run is already active for that source rather than silently queueing, so the operator learns their action did not take effect (FR-026)
- [X] T054 [US1] Add `GET /v1/runs/{id}` to `src/CarIngest.Api/Endpoints/RunEndpoints.cs` returning the `RunSummary` response record from `src/CarIngest.Api/Contracts/RunSummary.cs` — an API-owned type, never the EF entity (gate III-1)
- [X] T055 [US1] Create `src/CarIngest.Api/Endpoints/DocumentEndpoints.cs` with `GET /v1/documents/{id}` returning `src/CarIngest.Api/Contracts/CarDocumentResponse.cs`, whose `provenance` member is fully required and which **does not expose the vector** — serving 768 floats would commit the dimension as a public contract (contracts/ingestion-api.v1.md)
- [X] T056 [US1] Add `src/CarIngest.Api/Contracts/Price.cs` serializing `amount` as a **string**, not a JSON number, to avoid float rounding on currency (contracts/openapi.v1.yaml)

**Checkpoint**: US1 complete. A folder of addresses becomes attributable, embedded car documents.
This is a deployable MVP.

---

## Phase 4: User Story 2 — See and recover from ingestion failures (Priority: P2)

**Goal**: Every address's outcome is visible with a machine-readable reason, failures are
retryable without re-fetching successes, and a stalled source is detectable.

**Independent Test**: Run an ingestion where some addresses are unreachable and some succeed,
confirm per-address outcomes and reasons are retrievable, retry only the failures, and confirm
already-stored documents were not re-fetched.

### Tests for User Story 2

- [X] T057 [P] [US2] Create `tests/CarIngest.Api.Tests/OutcomeEndpointContractTests.cs` asserting `GET /v1/runs/{id}/outcomes` returns one item per address, that every non-`stored` item carries a `reasonCode` from the closed set, and that cursor paging walks a 10,000-outcome run without page boundaries shifting (FR-020, SC-007)
- [X] T058 [P] [US2] Create `tests/CarIngest.Infrastructure.Tests/Runs/RetryScopeTests.cs` asserting a retry run processes exactly the prior run's failed addresses, and that already-stored documents' `RetrievedAt` values are **unchanged** — proving no redundant re-fetch (FR-021)
- [X] T059 [P] [US2] Create `tests/CarIngest.Api.Tests/HealthEndpointTests.cs` asserting `GET /v1/health` reports `lastSuccessfulRunAt` per source and returns `503` only when unhealthy, so a quietly stalled source is detectable by monitoring without reading logs (FR-024, gate II-5)
- [X] T060 [P] [US2] Create `tests/CarIngest.Infrastructure.Tests/Runs/IsolationTests.cs` asserting a run containing a host whose extractor throws still stores documents for every other host, and that the API continues serving requests throughout (FR-022, SC-009)

### Implementation for User Story 2

- [X] T061 [P] [US2] Create `src/CarIngest.Infrastructure/Storage/OutcomeStore.cs` writing one `AddressOutcome` per address per run with its `ReasonCode`, and a `Message` constrained to field names and status — **never page content** (data-model §7, gate I-4)
- [X] T062 [US2] Create `src/CarIngest.Api/Endpoints/OutcomeEndpoints.cs` with `GET /v1/runs/{id}/outcomes` supporting `outcome`, `cursor`, and `limit` (1–1000, default 100). Cursor paging rather than offset so a large run is walkable stably (contracts/ingestion-api.v1.md)
- [X] T063 [US2] Add retry support to `src/CarIngest.Core/Runs/IngestionPipeline.cs` and `src/CarIngest.Api/Endpoints/RunEndpoints.cs`: `retryOfRunId` selects only the referenced run's failed addresses, sets `TriggeredBy = "retry"`, and returns `422` when that run has no failures to retry (FR-021)
- [X] T064 [P] [US2] Add run counter maintenance to `src/CarIngest.Infrastructure/Storage/RunStore.cs` tracking `AddressesRead`, `StoredCount`, `SkippedByPolicyCount`, `FailedCount`, and `RequestCount`, and assert at completion that `AddressesRead` equals the sum of the outcome counts (gate II-3, data-model §6)
- [X] T065 [P] [US2] Create `src/CarIngest.Api/Endpoints/HealthEndpoints.cs` with `GET /v1/health` returning `src/CarIngest.Api/Contracts/HealthResponse.cs` including `sources[].lastSuccessfulRunAt` and `activeRunId` (FR-024)
- [X] T066 [US2] Create `src/CarIngest.Api/Ingestion/RunReconciler.cs` running at startup to mark orphaned `running` runs as `interrupted`, leaving already-stored documents stored and attributable after a mid-run restart (data-model §6, spec edge case)

**Checkpoint**: US1 and US2 both work independently. Failures are visible, attributable, and
recoverable.

---

## Phase 5: User Story 3 — Keep data current without duplicating it (Priority: P3)

**Goal**: Re-ingesting an unchanged page creates no duplicate and costs no redundant work;
changed content replaces the stored representation; a vanished page is marked, not deleted.

**Independent Test**: Ingest twice with no page change and confirm no duplicates and no
redundant retrievals; alter one page, re-ingest, and confirm that document's vector is
regenerated and its last-verified time advances.

### Tests for User Story 3

- [X] T067 [P] [US3] Create `tests/CarIngest.Core.Tests/ContentHashStabilityTests.cs` asserting the canonical hash is unchanged by key reordering, whitespace, and casing differences, and **unchanged by cosmetic page churn** that does not touch allowlisted fields (research D13, SC-008)
- [X] T068 [P] [US3] Create `tests/CarIngest.Infrastructure.Tests/Storage/ReIngestTests.cs` asserting: unchanged content yields `unchanged`, advances `LastVerifiedAt`, leaves `RetrievedAt` alone, and creates no duplicate row; changed content replaces `VehicleFields` and regenerates the embedding; a 404/410 sets `Availability = "unavailable"` and **retains the row** (FR-018, spec edge case)
- [X] T069 [P] [US3] Create `tests/CarIngest.Infrastructure.Tests/Fetching/ConditionalRequestTests.cs` asserting `ETag`/`If-None-Match` is sent when a prior value exists and a `304` short-circuits parsing, while confirming correctness does **not** depend on it — real cache-header support on listing sites is unverified (research R2)

### Implementation for User Story 3

- [X] T070 [US3] Add change detection to `src/CarIngest.Infrastructure/Storage/DocumentStore.cs` comparing the freshly computed canonical hash against the stored `ContentHash`, taking the `unchanged`, replace, or `page-gone` path accordingly (FR-018)
- [X] T071 [US3] Add embedding invalidation to `src/CarIngest.Infrastructure/Storage/DocumentStore.cs` so a changed `ContentHash` regenerates the vector, and a changed `SourceTextHash` alone distinguishes a template change from a page-content change (data-model §5)
- [X] T072 [P] [US3] Add `ETag` and `Last-Modified` conditional-request support to `src/CarIngest.Infrastructure/Fetching/PageFetcher.cs`, storing and replaying validators as an opportunistic bandwidth optimization only — the hash remains the mechanism correctness depends on (research D13)
- [X] T073 [P] [US3] Add an `unchanged` outcome path to `src/CarIngest.Core/Runs/IngestionPipeline.cs` and the `RunTotals` contract, with no reason code attached since nothing failed (data-model §7)

**Checkpoint**: All three operational stories work. Repeat runs are cheap and non-duplicating.

---

## Phase 6: User Story 4 — Prove the semantic store works (Priority: P4)

**Goal**: An operator can confirm stored vectors are retrievable by meaning, so a silent failure
in semantic conversion cannot pass as success.

**Independent Test**: Ingest a known set, submit a free-text description matching one of them,
and confirm that document ranks above the unrelated ones and carries its source address.

### Tests for User Story 4

- [X] T074 [P] [US4] Create `tests/CarIngest.Infrastructure.Tests/Query/ModelIsolationTests.cs` storing embeddings from two model identities for the same document and asserting **no query path ever returns them mixed**. This is the test that matters most here: pgvector's `<=>` operator will compare vectors across models and return a plausible number with no error (FR-029)
- [X] T075 [P] [US4] Create `tests/CarIngest.Api.Tests/QueryEndpointContractTests.cs` asserting `POST /v1/query` returns `400` when `modelId` or `modelVersion` is omitted, `422` when no embeddings exist for the requested identity, `200` with an **empty array** when nothing is similar, and results ordered by ascending `distance` (FR-028, contracts/ingestion-api.v1.md)
- [X] T076 [P] [US4] Create `tests/CarIngest.Infrastructure.Tests/Query/RelevanceSmokeTest.cs` asserting that for a known ingested car, a free-text description of that car ranks its document above unrelated documents — the assertion that makes SC-011 enforceable rather than aspirational

### Implementation for User Story 4

- [X] T077 [US4] Create `src/CarIngest.Infrastructure/Query/SimilarityRepository.cs` as the **single** code path issuing a distance query, taking `modelId` and `modelVersion` as **required parameters** so the schema-unenforceable FR-029 rule is enforced structurally in one auditable place (data-model §5)
- [X] T078 [US4] Implement the top-K query in `src/CarIngest.Infrastructure/Query/SimilarityRepository.cs` using `OrderBy(x => x.Embedding.CosineDistance(queryVector)).Take(limit)`, passing the query vector as a `Pgvector.Vector` **parameter** rather than a raw array to avoid literal inlining and plan-cache bloat, and projecting the distance explicitly since `OrderBy` alone discards it (research D3)
- [X] T079 [US4] Create `src/CarIngest.Api/Endpoints/QueryEndpoints.cs` with `POST /v1/query` validating `text` (1–2000 chars) and `limit` (1–100, default 10), with `modelId` and `modelVersion` **required, not defaulted** — defaulting them would make the dangerous call the easy one (contracts/ingestion-api.v1.md)
- [X] T080 [US4] Add `503` handling to `src/CarIngest.Api/Endpoints/QueryEndpoints.cs` for an unreachable embedding generator, since the query text itself must be embedded before any comparison is possible
- [X] T081 [P] [US4] Create `src/CarIngest.Api/Contracts/QueryResponse.cs` returning the raw `distance` rather than a normalized "score" — a score would imply a calibration this feature has not established (contracts/ingestion-api.v1.md)

**Checkpoint**: All four user stories independently functional.

---

## Phase 7: Polish & Cross-Cutting Concerns

- [X] T082 [P] Add a CI golden-list PII regression guard in `tests/CarIngest.Infrastructure.Tests/PiiGoldenListTests.cs` asserting that a snapshot of persisted records contains none of a known set of test phone numbers, emails, and name tokens — this is what fails the build if a future change widens the stored record (research D8)
- [X] T083 [P] Add an architecture test in `tests/CarIngest.Core.Tests/ArchitectureTests.cs` asserting `CarIngest.Core` references no I/O package, that no type in the solution exposes a robots bypass, and that no EF entity type appears in any `src/CarIngest.Api/Contracts/` response (gates I-1, III-1)
- [X] T084 [P] Add a blocking-call test in `tests/CarIngest.Core.Tests/AsyncDisciplineTests.cs` asserting no `.Result`, `.Wait()`, or `GetAwaiter().GetResult()` appears outside program entry points (gate T-4)
- [X] T085 [P] Create `fixtures/dealer.example/css-profile/` fixtures and a per-host CSS extractor in `src/CarIngest.Infrastructure/Extraction/CssCarExtractor.cs`, selected only by explicit `ExtractorKind` configuration — **never as an automatic fallback from absent JSON-LD**. Scope depends on the T007 findings (research D9, R1)
- [X] T086 [P] Add `src/CarIngest.Api/appsettings.json` and `appsettings.Development.json` matching the configuration shape in [quickstart.md](./quickstart.md#4-configure-a-source-and-a-host-policy), with no secrets committed (gate T-6)
- [X] T087 [P] Create `README.md` at repository root covering setup, the two-connection-string model, and the Docker prerequisite — **the Docker daemon is not currently running on this machine**, and Testcontainers tests fail with what looks like a connection error until it is started
- [X] T088 Tune Postgres `max_parallel_workers_per_gather` for exact vector scan rather than adding an ANN index, and record measured top-10 latency over a 100k-row corpus against the 200 ms goal in [plan.md](./plan.md#technical-context). Only if that goal is missed, revisit HNSW with `m=16`, `ef_construction=64` (research D4)
- [X] T089 Run the full `quickstart.md` validation — all four scenarios end to end — and record any divergence between documented and actual behaviour
- [X] T090 Verify `dotnet build -warnaserror` is clean and `dotnet test` passes in full with the Docker daemon running (gate W-4)

---

## Dependencies & Execution Order

### Phase Dependencies

- **Setup (Phase 1)**: No dependencies — start immediately.
- **Foundational (Phase 2)**: Depends on Setup. **Blocks all user stories.** T007 (risk R1)
  additionally gates T030–T033 and T085, because extractor strategy depends on its findings.
- **User Stories (Phases 3–6)**: All depend on Foundational. US1 is independent; US2–US4 read
  data US1 produces, so they are testable independently only once US1 has run at least once.
- **Polish (Phase 7)**: Depends on the stories you intend to ship.

### User Story Dependencies

- **US1 (P1)**: Depends only on Foundational. Fully independent — the MVP.
- **US2 (P2)**: Reports on outcomes US1 produces. Its own tests can seed outcomes directly, so
  it is independently *testable*, but it delivers no operator value before US1.
- **US3 (P3)**: Needs stored documents to compare against. Depends on US1 in practice.
- **US4 (P4)**: Needs stored embeddings. Depends on US1 in practice.

### Within Each User Story

- Tests are written first and must fail before implementation (constitution W-2).
- Core records → extractors → stores → pipeline → endpoints.
- `VehicleFields` (T011) precedes everything that projects into it.
- The robots evaluator (T017–T019) precedes any fetching task.

### Parallel Opportunities

- **Setup**: T003–T006 in parallel after T001–T002.
- **Foundational**: T008, T009, T011, T012, T013, T014 in parallel; T015 and T016 in parallel
  before T017; T020, T029, T030, T031, T032, T033 in parallel.
- **US1 tests**: T034–T040 all parallel — seven independent files.
- **US2 tests**: T057–T060 all parallel. **US3 tests**: T067–T069. **US4 tests**: T074–T076.
- **Polish**: T082–T087 in parallel.
- Once Foundational completes, US2–US4 can be staffed alongside US1 if their tests seed data
  directly rather than waiting on a real US1 run.

---

## Parallel Example: User Story 1 Tests

```bash
# Launch all seven US1 test files together — different files, no shared state:
Task: "PII containment tests in tests/CarIngest.Infrastructure.Tests/Extraction/PiiContainmentTests.cs"
Task: "JSON-LD extraction tests in tests/CarIngest.Infrastructure.Tests/Extraction/JsonLdExtractionTests.cs"
Task: "Address list parsing tests in tests/CarIngest.Core.Tests/AddressListParsingTests.cs"
Task: "Robots enforcement tests in tests/CarIngest.Infrastructure.Tests/Fetching/RobotsEnforcementTests.cs"
Task: "Politeness tests in tests/CarIngest.Infrastructure.Tests/Fetching/PolitenessTests.cs"
Task: "Run endpoint contract tests in tests/CarIngest.Api.Tests/RunEndpointContractTests.cs"
Task: "Provenance tests in tests/CarIngest.Infrastructure.Tests/Storage/ProvenanceTests.cs"
```

---

## Implementation Strategy

### MVP First (User Story 1 Only)

1. Phase 1: Setup (T001–T006).
2. Phase 2: Foundational (T007–T033) — **start with T007**, which may change extractor scope.
3. Phase 3: User Story 1 (T034–T056).
4. **STOP and VALIDATE**: run quickstart Scenario 1. A folder of addresses becomes attributable,
   embedded car documents.
5. Deploy or demo.

### Incremental Delivery

1. Setup + Foundational → foundation ready.
2. US1 → validate → **MVP**.
3. US2 → failures visible and retryable → the point at which this is safe to run unattended.
4. US3 → repeat runs cheap and non-duplicating.
5. US4 → the stored vectors are proven useful, not merely present.

US2 is worth prioritising over its P2 label suggests if the service will run unattended: without
it, a broken extractor is indistinguishable from a quiet period.

### Parallel Team Strategy

1. Everyone on Setup + Foundational; T007 first since it gates extractor design.
2. Then: Developer A on US1 (the critical path); Developer B on US2's outcome and health
   surfaces seeded with synthetic data; Developer C on the robots evaluator and its RFC vectors
   (T015–T019), which is self-contained and the highest-stakes component.

---

## Notes

- `[P]` tasks touch different files and have no incomplete dependencies.
- **Gate I-4 is the one to watch.** T011 (no catch-all member, no description field), T029 (no
  raw content in logs), T034 (literal-string assertions), and T082 (CI golden list) are four
  layers of the same guarantee. Weakening any one of them re-opens a PII path that the design
  closed structurally rather than probabilistically.
- **Gate I-1 admits no override.** If a task seems to need a robots bypass to proceed, the
  correct move is to exclude the host, not to add the flag.
- T007 is deliberately first in Phase 2 and may reshape T030–T033, T043–T046, and T085. Treat its
  findings as the real input to extractor design; documentation cannot settle it.
- Commit after each task or logical group. Stop at any checkpoint to validate a story in
  isolation.
