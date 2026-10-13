# Ingestion Control API v1

Base path: `/v1`. Media type: `application/json`. Timestamps: RFC 3339, UTC, explicit `Z`.

Entity field meanings are in [../data-model.md](../data-model.md); this document specifies only
what crosses the wire.

---

## Error model

Every non-2xx response is `application/problem+json` (RFC 9457):

```json
{
  "type": "https://carringest.invalid/problems/run-already-active",
  "title": "A run is already active for this source",
  "status": 409,
  "detail": "Source 'f1e2…' has run 'a9b8…' in status 'running'.",
  "instance": "/v1/runs"
}
```

`detail` carries identifiers and status only — never page content, never a fetched response
body (Principle I-4 covers logs and responses alike).

---

## POST /v1/runs

Start an ingestion run (FR-019).

**Request**

```json
{ "sourceId": "f1e2d3c4-…", "retryOfRunId": null }
```

| Field | Type | Required | Notes |
|-------|------|----------|-------|
| `sourceId` | uuid | yes | Must match a configured address source |
| `retryOfRunId` | uuid \| null | no | When set, processes **only** that run's failed addresses (FR-021) |

**Responses**

| Status | Meaning |
|--------|---------|
| `202 Accepted` | Run queued. Body is a `RunSummary` with `status: "running"`. `Location` header points at `/v1/runs/{id}`. |
| `400` | `sourceId` absent or malformed |
| `404` | No such source, or `retryOfRunId` does not exist |
| `409` | A run is already active for this source (FR-026) |
| `422` | `retryOfRunId` names a run with no failed addresses to retry |

**Semantics**

- Returns `202`, not `200`: the run executes in the background worker. A run over 10,000
  addresses must not block a request (SC-002).
- `409` is deliberate rather than silently queueing — the operator learns their action did not
  take effect.

---

## GET /v1/runs/{id}

Fetch one run's status and totals (FR-019).

**`200 OK` — `RunSummary`**

```json
{
  "id": "a9b8c7d6-…",
  "sourceId": "f1e2d3c4-…",
  "status": "succeeded",
  "triggeredBy": "operator",
  "retryOfRunId": null,
  "startedAt": "2026-10-11T09:15:00Z",
  "completedAt": "2026-10-11T09:47:12Z",
  "filesRead": ["targets-a.txt", "targets-b.txt"],
  "totals": {
    "addressesRead": 1000,
    "stored": 812,
    "unchanged": 94,
    "skippedByPolicy": 63,
    "failed": 31,
    "requestCount": 1104
  }
}
```

`status` ∈ `running` | `succeeded` | `failed` | `interrupted`.

**Invariant a test must assert**: when `status` is terminal,
`addressesRead == stored + unchanged + skippedByPolicy + failed`. A mismatch is a defect, not a
rounding artifact.

| Status | Meaning |
|--------|---------|
| `200` | Found |
| `404` | No such run |

---

## GET /v1/runs/{id}/outcomes

Per-address outcomes (FR-020). This endpoint is what makes SC-007 checkable — the operator can
determine the outcome of **every** address, and distinguish "nothing matched" from "extraction
is broken".

**Query parameters**

| Name | Type | Default | Notes |
|------|------|---------|-------|
| `outcome` | enum | all | Filter: `stored` \| `unchanged` \| `skipped-by-policy` \| `failed` |
| `cursor` | string | — | Opaque continuation token |
| `limit` | int | 100 | 1–1000 |

Cursor pagination rather than offset: a 10,000-address run must be walkable without the
page boundaries shifting underneath the caller.

**`200 OK`**

```json
{
  "runId": "a9b8c7d6-…",
  "items": [
    {
      "url": "https://dealer.example/listing/123",
      "outcome": "stored",
      "reasonCode": null,
      "message": null,
      "attemptCount": 1,
      "recordedAt": "2026-10-11T09:15:04Z",
      "documentId": "11111111-…"
    },
    {
      "url": "https://dealer.example/listing/456",
      "outcome": "failed",
      "reasonCode": "schema-drift",
      "message": "Mandatory field 'Year' absent from schema.org Car node.",
      "attemptCount": 3,
      "recordedAt": "2026-10-11T09:15:09Z",
      "documentId": null
    }
  ],
  "nextCursor": "eyJvIjoxMDB9"
}
```

`reasonCode` is drawn from the closed set in
[../data-model.md](../data-model.md#reason-codes). It is machine-readable and stable; `message`
is advisory and may change without a version bump.

---

## GET /v1/documents/{id}

Fetch one stored document with its provenance.

**`200 OK` — `CarDocumentResponse`**

```json
{
  "id": "11111111-…",
  "vehicle": {
    "make": "Volkswagen",
    "model": "Golf",
    "year": 2018,
    "trim": "GT",
    "mileage": { "value": 45000, "unit": "mi" },
    "price": { "amount": "12000.00", "currency": "GBP" },
    "fuelType": "diesel",
    "transmission": "manual",
    "bodyStyle": "hatchback",
    "colour": "blue",
    "vin": null
  },
  "availability": "available",
  "detectedLanguage": "en-GB",
  "provenance": {
    "sourceUrl": "https://dealer.example/listing/123",
    "retrievedAt": "2026-10-11T09:15:04Z",
    "extractorVersion": "jsonld-car/1.0.0",
    "discoveryMethod": "operator-file",
    "lastVerifiedAt": "2026-10-11T09:15:04Z"
  },
  "embeddings": [
    {
      "modelId": "nomic-embed-text",
      "modelVersion": "sha256:9f3a…",
      "embeddedAt": "2026-10-11T09:15:05Z"
    }
  ]
}
```

**Contract rules**

- `provenance` is non-nullable in full. A document cannot exist without it (FR-015), so the
  response type declares it required — there is no "provenance unknown" representation to serve.
- The vector itself is **not** returned. It is an implementation detail of similarity search;
  exposing 768 floats would commit us to the dimension as a public contract.
- `embeddings` lists model identity only, so a caller can tell *which* representations exist.
- `price.amount` is a **string**, not a JSON number, to avoid float rounding on currency.

| Status | Meaning |
|--------|---------|
| `200` | Found |
| `404` | No such document |

---

## POST /v1/query

**Operator and automated-test validation only** (FR-028). Not a consumer search contract:
relevance tuning, structured filters, pagination, and facets are out of scope.

**Request**

```json
{
  "text": "cheap reliable estate with low mileage",
  "modelId": "nomic-embed-text",
  "modelVersion": "sha256:9f3a…",
  "limit": 10
}
```

| Field | Type | Required | Notes |
|-------|------|----------|-------|
| `text` | string | yes | 1–2000 chars |
| `modelId` | string | **yes** | See below |
| `modelVersion` | string | **yes** | See below |
| `limit` | int | no | Default 10, max 100 |

**`modelId` and `modelVersion` are required, not optional.** This is the wire-level expression
of FR-029. pgvector's distance operators will happily compare vectors produced by two different
models and return a plausible number with **no error**, so the contract refuses to let a caller
omit the discriminator and get silently meaningless rankings. Defaulting them would make the
dangerous call the easy one.

**`200 OK`**

```json
{
  "modelId": "nomic-embed-text",
  "modelVersion": "sha256:9f3a…",
  "results": [
    {
      "documentId": "11111111-…",
      "sourceUrl": "https://dealer.example/listing/123",
      "retrievedAt": "2026-10-11T09:15:04Z",
      "distance": 0.2143
    }
  ]
}
```

- Ordered by ascending `distance` (nearest first), as FR-028 requires.
- Results are **confined to the requested model identity** — never mixed (FR-029).
- An empty `results` array is a `200`, not a `404` and not an error: "nothing is similar" is a
  valid answer (spec acceptance scenario US4-2).
- `distance` is the raw metric, not a normalized "score". A similarity score would imply a
  calibration this feature has not established.

| Status | Meaning |
|--------|---------|
| `200` | Query ran (possibly zero results) |
| `400` | `text` empty/oversized, or `limit` out of range |
| `422` | No embeddings exist for the requested model identity — distinguishes "wrong model asked for" from "nothing matched" |
| `503` | Embedding generator unreachable, so the query text cannot be embedded |

---

## GET /v1/health

Health plus per-source freshness (FR-024, gate II-5).

**`200 OK`**

```json
{
  "status": "healthy",
  "checks": {
    "database": "healthy",
    "embeddingGenerator": "healthy"
  },
  "sources": [
    {
      "sourceId": "f1e2d3c4-…",
      "location": "/var/lib/carringest/targets",
      "lastSuccessfulRunAt": "2026-10-11T09:47:12Z",
      "activeRunId": null
    }
  ]
}
```

`status` ∈ `healthy` | `degraded` | `unhealthy`. Returns `200` when healthy or degraded, `503`
when unhealthy.

**Why `sources[].lastSuccessfulRunAt` is here**: a source that has quietly stopped producing
results is the failure mode gate II-5 exists to catch. Surfacing it in the health payload makes
it detectable by monitoring, without reading logs.

---

## What this API deliberately does not expose

- **No robots-override parameter.** Principle I-1 is non-overridable in production; offering the
  knob at all would be a contract that contradicts the constitution.
- **No raw page content, no HTML, no seller fields.** Nothing stores them (D8), so nothing can
  serve them.
- **No document deletion.** Ingestion marks pages `unavailable` rather than deleting
  (spec edge case); operator-driven removal tooling is out of scope.
- **No vector read/write.** Vectors are produced by the service, never supplied by a caller —
  accepting externally-supplied vectors would break the provenance guarantee in FR-015.
