# Data Model: Car Data Vector Ingestion

**Date**: 2026-10-11 | **Plan**: [plan.md](./plan.md) | **Spec**: [spec.md](./spec.md)

Entities derived from [spec.md](./spec.md#key-entities-include-if-feature-involves-data), with
validation rules traced to functional requirements and the storage decisions from
[research.md](./research.md) (D7, D8, D13, D14).

All timestamps are `timestamptz` and UTC (FR-016, gate III-5). All tables evolve only through
checked-in, ordered, reversible EF Core migrations (constitution gate III-3).

---

## Entity overview

```text
AddressSource (1) ──< IngestionTarget (1) ──< AddressOutcome >── (1) IngestionRun
                                │                                        │
                                │ 0..1                                   │ 1
                                ▼                                        ▼
                          CarDocument (1) ──< CarDocumentEmbedding   AddressSource
                                │
                                └── owns ── Provenance (value object, inlined columns)

HostAccessPolicy  — keyed by host, referenced by IngestionTarget.Host (no FK; config-sourced)
```

`CarDocument` is the aggregate root for stored data. `IngestionRun` is the aggregate root for
operations. They meet only through `AddressOutcome`, which keeps a run's bookkeeping from
contaminating the document's provenance.

---

## 1. AddressSource

The configured folder holding address-list files (FR-001).

| Field | Type | Constraints |
|-------|------|-------------|
| `Id` | `uuid` | PK |
| `Location` | `text` | NOT NULL, UNIQUE. Absolute folder path. |
| `LastReadAt` | `timestamptz?` | NULL until first run. |
| `LastSuccessfulRunAt` | `timestamptz?` | Feeds the health report (FR-024, gate II-5). |

**Validation**: `Location` must resolve to an existing readable directory at run start. An
absent or empty folder is a **successful run reporting zero addresses**, distinguishable from a
run that failed to start (spec edge case).

---

## 2. IngestionTarget

One distinct car-page address (FR-004 — de-duplicated within and across files).

| Field | Type | Constraints |
|-------|------|-------------|
| `Id` | `uuid` | PK |
| `AddressSourceId` | `uuid` | FK → AddressSource, NOT NULL |
| `Url` | `text` | NOT NULL, UNIQUE. Normalized absolute http/https URL. |
| `Host` | `text` | NOT NULL, indexed. Derived from `Url`; the rate-limiter and policy key. |
| `DiscoveryMethod` | `text` | NOT NULL. **Open set** (FR-015a): `operator-file` in v1; a future crawl adds `link-discovered` without altering stored rows. |
| `DiscoverySourceFile` | `text?` | The supplying address-list file when `DiscoveryMethod = operator-file`. |
| `FirstSeenAt` | `timestamptz` | NOT NULL |
| `LastAttemptedAt` | `timestamptz?` | |

**Validation rules**

- `Url` MUST parse as absolute `http`/`https`. Malformed entries are rejected and reported
  individually without aborting the run (FR-003) — they never become rows here.
- `Url` normalization before uniqueness: lowercase scheme and host, strip the fragment, preserve
  path case (RFC 9309 path matching is case-sensitive), strip a trailing `?`. Query strings are
  **preserved** — many listing URLs carry the vehicle id there.
- `DiscoveryMethod` is a string, not a CLR enum mapped to an int, precisely so adding a value is
  additive (FR-015a).

---

## 3. HostAccessPolicy

The recorded rules for one host (FR-008). **Config-sourced, not operator-writable at runtime** —
a new host cannot be ingested until its policy exists in configuration (gate T-5).

| Field | Type | Constraints |
|-------|------|-------------|
| `Host` | `text` | Key |
| `RequestsPerPeriod` | `int` | > 0. Token-bucket capacity. |
| `ReplenishmentPeriod` | `TimeSpan` | > 0. Overridden upward by robots `Crawl-delay` when longer. |
| `MaxConcurrency` | `int` | > 0, bounded per host, never only globally (FR-006). |
| `PermittedFields` | `string[]` | NOT EMPTY. The allowlist for this host (FR-008, FR-010). |
| `RobotsEvaluatedAt` | `timestamptz` | When the host's access policy was last checked. |
| `ExtractorKind` | `text` | `json-ld` (default) or `css:<profile>` — never an automatic fallback (D9). |

**Validation**: `PermittedFields` MUST be a subset of the allowlisted field set in §4. A policy
naming a field outside it fails startup validation rather than at first fetch — a configuration
error should not surface as a mid-run extraction failure.

**Note**: the effective crawl delay is `max(ReplenishmentPeriod, robots Crawl-delay)`. Robots
can only slow us down, never speed us up.

---

## 4. CarDocument

One accepted car from one retrieved page. The allowlisted projection — **this record is the PII
boundary** (FR-010, gate I-4).

| Field | Type | Constraints |
|-------|------|-------------|
| `Id` | `uuid` | PK |
| `IngestionTargetId` | `uuid` | FK → IngestionTarget, NOT NULL |
| `CardKey` | `text` | NOT NULL, max 256. Stable identity **within** the page. UNIQUE with `IngestionTargetId`. |
| `DetailUrl` | `text?` | The car's own page when linked. **Recorded, never fetched** — see note below. |
| **Allowlisted vehicle fields** | | |
| `Make` | `text` | NOT NULL |
| `Model` | `text` | NOT NULL |
| `Year` | `int` | NOT NULL, 1900 ≤ year ≤ (current year + 2) |
| `Trim` | `text?` | |
| `MileageValue` | `int?` | ≥ 0, ≤ 1,000,000 |
| `MileageUnit` | `text?` | `mi` or `km`; NOT NULL when `MileageValue` is present |
| `PriceAmount` | `numeric(12,2)?` | ≥ 0, ≤ 10,000,000 |
| `PriceCurrency` | `char(3)?` | ISO 4217; NOT NULL when `PriceAmount` is present |
| `FuelType` | `text?` | |
| `Transmission` | `text?` | |
| `BodyStyle` | `text?` | |
| `Colour` | `text?` | |
| `Vin` | `text?` | 17 chars when present; see note below |
| **State** | | |
| `Availability` | `text` | NOT NULL: `available` \| `unavailable` |
| `DetectedLanguage` | `text?` | BCP 47 |
| `ContentHash` | `bytea` | NOT NULL. SHA-256 of the canonical projection (D13, FR-018) |
| **Provenance — all NOT NULL (FR-015, gate III-4)** | | |
| `SourceUrl` | `text` | NOT NULL |
| `RetrievedAt` | `timestamptz` | NOT NULL, UTC |
| `ExtractorVersion` | `text` | NOT NULL |
| `DiscoveryMethod` | `text` | NOT NULL — denormalized from the target so a document is self-describing |
| `LastVerifiedAt` | `timestamptz` | NOT NULL (FR-018) |

### What this entity deliberately does **not** have

- **No free-text seller description.** v1 stores none at all (D8). Sellers embed phone numbers
  and names inline ("call John at…"), and regex scrubbing has false negatives that cannot be
  proven zero. There is no field for personal data to occupy — the guarantee is structural, not
  probabilistic.
- **No catch-all member.** No `Dictionary<string, object>`, no `JsonDocument` of the source, no
  `ExtraFields`. The CLR type itself prevents unmapped source data from propagating (D8).
- **No raw page bytes.** FR-017's "retain content so vectors can be regenerated without
  re-fetching" is satisfied by this projection, which is what the embedding text is built from.
- **No seller identity, contact, or location fields** of any kind.

**On `Vin`**: a VIN identifies a vehicle, not a person, so it is not personal data on its own.
It is nonetheless nullable and allowlist-gated per host, because in a small enough market a VIN
plus a location could narrow to an individual. Hosts whose policy omits it simply do not store
it.

### Amended 2026-10-11: one address can describe several cars

The original model assumed one document per address (`UNIQUE(IngestionTargetId)`). The
operator's configured host disproves that: `webscraper.io/test-sites/load-more` is a single
listing page describing **six** cars, and its per-car detail pages are robots-disallowed, so the
listing page is the only lawful source for all of them.

The key is therefore `(IngestionTargetId, CardKey)`. `CardKey` is derived from the detail-page
slug (`mercedes-benz-w123-280e-1955-c001`) and falls back to a slugified name. Positional
indexing was rejected: reordering the cards would re-key every document and read as six changes.

`DetailUrl` is stored but never requested. On this host those URLs sit under
`Disallow: /test-sites/product/`, and keeping the value lets a reviewer confirm from the data
that we did not fetch it. `SourceUrl` remains the **listing** page, because that is genuinely
where the content came from — recording the detail page would assert we read a page we
deliberately did not.

**Validation rules**

- Mandatory fields are `Make`, `Model`, `Year`. Absence of any of them is a typed schema-drift
  failure — the document is **not stored** (FR-011). No partial, empty, or null-filled rows.
- Every nullable field must still be *present in the host's allowlist* to be populated. A value
  found on the page but absent from `PermittedFields` is discarded at projection time.
- Range checks above are sanity guards against JSON-LD schema abuse (D9), not business rules.
  A violation is a schema-drift failure, not a silent clamp.
- `ContentHash` is computed over a canonical serialization (stable key order, normalized
  whitespace and casing) of the allowlisted fields **only** — never over raw HTML, so cosmetic
  churn (rotated banner URLs, view counters) does not read as change (D13, SC-008).

### State transitions

```text
                  extraction succeeds
    (none) ─────────────────────────────────> available
                                                │  ▲
            re-ingest, hash unchanged           │  │
            → LastVerifiedAt advances only ─────┘  │
                                                   │
            re-ingest, hash changed                │
            → fields replaced, embeddings          │
              invalidated and regenerated ─────────┤
                                                   │
            re-ingest returns 404/410              │
            → unavailable  ─────────────────────────┘
                 (row RETAINED, never deleted — spec edge case)
```

A document is never hard-deleted by ingestion. `unavailable` is terminal only until a later run
finds the page again.

---

## 5. CarDocumentEmbedding

The semantic representation. Separate table keyed by model identity (D7).

| Field | Type | Constraints |
|-------|------|-------------|
| `Id` | `uuid` | PK |
| `CarDocumentId` | `uuid` | FK → CarDocument, NOT NULL, ON DELETE CASCADE |
| `Embedding` | `vector(768)` | NOT NULL. **Unindexed** — exact scan (D4) |
| `ModelId` | `text` | NOT NULL, e.g. `nomic-embed-text` |
| `ModelVersion` | `text` | NOT NULL. The pulled **digest/tag**, not the family name (FR-014) |
| `EmbeddedAt` | `timestamptz` | NOT NULL, UTC |
| `SourceTextHash` | `bytea` | NOT NULL. SHA-256 of the exact templated text embedded |

**Constraints**

- UNIQUE `(CarDocumentId, ModelId, ModelVersion)` — one vector per document per model identity.
- Index on `(ModelId, ModelVersion)` to make the mandatory query filter cheap.

**Validation rules**

- **Every similarity query MUST filter `ModelId` AND `ModelVersion`** (FR-029). This is the one
  rule that cannot be enforced by the schema: pgvector's `<=>` operator will happily compute a
  distance between vectors from two different models and return a plausible-looking number with
  **no error**. Enforcement therefore lives in a single repository method that takes model
  identity as a required parameter, with a test asserting no other code path issues a distance
  query.
- `ModelVersion` records the resolved digest, not the mutable tag. A provider silently updating
  weights behind a stable name would otherwise shift the entire corpus's vector space
  undetectably (research R3).
- `SourceTextHash` makes re-embedding idempotent and lets a changed template be distinguished
  from changed page content.
- Dimension is fixed at 768 by the column type. Adopting a model of different dimensionality
  requires a **new table via migration**, not a column change — pgvector columns are
  fixed-dimension and zero-padding would silently corrupt distances (D7).

### The embedded text

Built by templating the allowlisted fields (D6) — e.g.
`"2018 Volkswagen Golf, 45,000 miles, GBP 12,000, diesel, manual, hatchback, blue"`.

One vector per document; no chunking (a listing is already one coherent semantic unit).
Critically, the template is constructed **from this record**, which is already the screened
projection — so personal data cannot reach the vector, where it would be unredactable.

---

## 6. IngestionRun

One execution over a source (FR-019).

| Field | Type | Constraints |
|-------|------|-------------|
| `Id` | `uuid` | PK |
| `AddressSourceId` | `uuid` | FK → AddressSource, NOT NULL |
| `StartedAt` | `timestamptz` | NOT NULL, UTC |
| `CompletedAt` | `timestamptz?` | NULL while running |
| `Status` | `text` | NOT NULL: `running` \| `succeeded` \| `failed` \| `interrupted` |
| `RetryOfRunId` | `uuid?` | FK → IngestionRun. Set when this run retries another's failures (FR-021) |
| `FilesRead` | `string[]` | NOT NULL (FR-002) |
| `AddressesRead` | `int` | NOT NULL, default 0 |
| `StoredCount` | `int` | NOT NULL, default 0 |
| `SkippedByPolicyCount` | `int` | NOT NULL, default 0 |
| `FailedCount` | `int` | NOT NULL, default 0 |
| `RequestCount` | `int` | NOT NULL, default 0 (gate II-3) |
| `TriggeredBy` | `text` | NOT NULL: `operator` \| `retry` |

**Validation rules**

- At most one run per `AddressSourceId` in `running` status — a partial unique index. A second
  concurrent start is rejected, not queued silently (FR-026, spec edge case).
- `AddressesRead = StoredCount + SkippedByPolicyCount + FailedCount` at completion. A mismatch
  is a defect, and is asserted in tests rather than trusted.
- A service restart mid-run leaves `running` rows; startup reconciliation marks them
  `interrupted`. Documents already stored stay stored and attributable (spec edge case).

---

## 7. AddressOutcome

The result for one target within one run (FR-020). The run/document join.

| Field | Type | Constraints |
|-------|------|-------------|
| `Id` | `uuid` | PK |
| `IngestionRunId` | `uuid` | FK → IngestionRun, NOT NULL |
| `IngestionTargetId` | `uuid` | FK → IngestionTarget, NOT NULL |
| `Outcome` | `text` | NOT NULL: `stored` \| `skipped-by-policy` \| `failed` \| `unchanged` |
| `ReasonCode` | `text?` | NOT NULL when `Outcome != stored`. Machine-readable (FR-020) |
| `Message` | `text?` | Human-readable. **Must contain no page content** — only field names and status (D8) |
| `AttemptCount` | `int` | NOT NULL, default 1 |
| `RecordedAt` | `timestamptz` | NOT NULL, UTC |

**Constraints**: UNIQUE `(IngestionRunId, IngestionTargetId)` — exactly one outcome per address
per run, which is what makes "the operator can determine the outcome of every single address"
(SC-007) checkable rather than aspirational.

### Reason codes

A closed set, because FR-020 requires machine-readable reasons and SC-007 requires
distinguishing "nothing matched" from "extraction is broken".

| Code | Outcome | Meaning |
|------|---------|---------|
| `robots-disallowed` | skipped-by-policy | Path forbidden by the host's robots.txt (FR-005) |
| `robots-unavailable` | skipped-by-policy | robots.txt returned 5xx or unreachable ⇒ fail-safe full disallow (D10) |
| `no-host-policy` | skipped-by-policy | Host has no `HostAccessPolicy` entry (FR-008) |
| `auth-required` | skipped-by-policy | Sign-in, paywall, or bot challenge — never circumvented (FR-007) |
| `malformed-address` | failed | Source line was not a valid absolute URL (FR-003) |
| `fetch-timeout` | failed | Request exceeded the timeout |
| `fetch-http-error` | failed | Non-success status after retries |
| `rate-limited-persistently` | failed | 429/503 survived backoff (FR-006) |
| `content-too-large` | failed | Exceeded the bounded size limit (FR-012) |
| `jsonld-absent` | failed | No schema.org node found and no declared CSS profile (D9, risk R1) |
| `jsonld-malformed` | failed | Structured data present but unparseable (D9) |
| `schema-drift` | failed | Mandatory field missing or wrong type (FR-011) |
| `validation-range` | failed | Value outside sanity range — possible schema abuse (D9) |
| `js-render-required` | failed | Content materializes only after script execution (D12) |
| `not-a-car-page` | failed | Reachable but not a listing (spec edge case) |
| `embedding-unavailable` | failed | Generator unreachable; **projection retained for retry** (FR-017) |
| `page-gone` | failed | 404/410 ⇒ existing document marked `unavailable`, not deleted |

`unchanged` carries no reason code: the content hash matched and only `LastVerifiedAt` advanced
(FR-018).

---

## Requirement traceability

| Requirement | Where satisfied |
|-------------|-----------------|
| FR-001, FR-002 | AddressSource; IngestionRun.FilesRead |
| FR-003 | `malformed-address` outcome; no IngestionTarget row created |
| FR-004 | IngestionTarget.Url UNIQUE + normalization |
| FR-005–FR-007 | HostAccessPolicy; `robots-*`, `auth-required` reason codes |
| FR-008 | HostAccessPolicy required before fetch; `no-host-policy` |
| FR-009, FR-010 | CarDocument allowlist; no description field; no catch-all member |
| FR-011 | `schema-drift`; mandatory Make/Model/Year |
| FR-012 | `content-too-large` |
| FR-013, FR-014 | CarDocumentEmbedding + ModelId/ModelVersion |
| FR-015, FR-015a | CarDocument provenance columns; DiscoveryMethod open set |
| FR-016 | `timestamptz` throughout |
| FR-017 | Projection persisted before embedding; `embedding-unavailable` retryable |
| FR-018 | ContentHash; `unchanged`; LastVerifiedAt |
| FR-019, FR-020 | IngestionRun counters; AddressOutcome |
| FR-021 | IngestionRun.RetryOfRunId |
| FR-022 | Per-address AddressOutcome rows; failures scoped to one address |
| FR-023 | IngestionRun.RequestCount and counters |
| FR-024 | AddressSource.LastSuccessfulRunAt |
| FR-025 | Projection persisted before embedding |
| FR-026 | Partial unique index on `running` runs per source |
| FR-027 | API response types — see [contracts/](./contracts/) |
| FR-028, FR-029 | CarDocumentEmbedding; mandatory model-identity filter |
