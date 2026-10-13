# Quickstart & Validation Guide: Car Data Vector Ingestion

**Date**: 2026-10-11 | **Plan**: [plan.md](./plan.md) | **Spec**: [spec.md](./spec.md)

How to stand the service up and prove the feature works end to end. Each scenario maps to a
user story and its success criteria, and is runnable without touching a real listing site.

Implementation details — migrations, service bodies, full test suites — belong to
`/speckit-tasks` and the implementation phase, not here.

---

## Prerequisites

Verified on this machine 2026-10-11:

| Requirement | Status here | Notes |
|-------------|-------------|-------|
| .NET SDK 10 | ✅ **10.0.102 installed** | Also 9.0.102, 8.0.203, 6.0.419 present |
| Container runtime | ✅ **podman 5.3.2, machine running** | Docker's CLI (29.4.3) is installed but its **daemon is not running**; podman serves the Docker API instead — see below |
| pgvector image | ✅ **pulled, verified 0.8.7** | `pgvector/pgvector:pg17` |
| `psql` client | ❌ **not installed** | Not required — exec into the container or use `dotnet ef` |
| Ollama | not verified | Needed only for real embedding; unit tests use a fake generator |

### Pointing Testcontainers at podman

Testcontainers talks to the Docker API. Podman exposes a compatible socket, so it works without
Docker Desktop — but the socket path has to be supplied, because Testcontainers otherwise looks
for Docker's socket and fails with a connection error that looks like a bug rather than a
missing environment variable.

```bash
export DOCKER_HOST="unix://$(podman machine inspect \
  --format '{{.ConnectionInfo.PodmanSocket.Path}}')"

# Ryuk (the Testcontainers reaper) needs a privileged container; simplest to disable it
# under podman and let the per-test fixture dispose its own containers.
export TESTCONTAINERS_RYUK_DISABLED=true

# Verify the API answers before running any test
docker info --format '{{.ServerVersion}}'   # reports the podman version, e.g. 5.3.2
```

Verified working on this machine: the socket answers as `serverVersion=5.3.2`, and
`pgvector/pgvector:pg17` starts and reports `vector 0.8.7`.

---

## Setup

### 1. Postgres with pgvector

```bash
# Works with either runtime; substitute `docker` for `podman` if the Docker daemon is running.
podman run -d --name carringest-db \
  -e POSTGRES_PASSWORD=postgres \
  -e POSTGRES_DB=carringest \
  -p 5432:5432 \
  pgvector/pgvector:pg17
```

The `pgvector/pgvector` image ships the extension preinstalled, so `CREATE EXTENSION` succeeds
under the container's default superuser role. That is a development convenience — see the
privilege note under [Migrations](#3-apply-migrations) for why production differs.

### 2. Ollama with the embedding model

```bash
# Install from https://ollama.com, then:
ollama pull nomic-embed-text
ollama list                       # note the DIGEST — it is the recorded model version
curl -s http://localhost:11434/api/embed \
  -d '{"model":"nomic-embed-text","input":"2018 Volkswagen Golf, 45000 miles"}' \
  | head -c 200
```

Record the **digest**, not just `nomic-embed-text`. The digest is what gets stored as
`ModelVersion` (FR-014) — a provider silently changing weights behind a stable tag would
otherwise shift the whole corpus's vector space with no error.

To use OpenAI instead, set the provider to `openai` in configuration and supply an API key by
environment variable; it is a DI registration swap, not a code change (D5).

### 3. Apply migrations

```bash
export CarIngest__ConnectionStrings__Migrator='Host=localhost;Database=carringest;Username=postgres;Password=postgres'
dotnet ef database update --project src/CarIngest.Infrastructure --startup-project src/CarIngest.Api
```

Two connection strings exist by design (D14):

- **`Migrator`** — elevated, used only at deploy time. `CREATE EXTENSION vector` requires
  elevated privilege.
- **`Runtime`** — least-privilege, used by the service. **It cannot create the extension, and
  that is correct.** Confirmed empirically: a non-superuser role attempting it gets
  `ERROR: permission denied to create extension "vector" / HINT: Must be superuser to create
  this extension.` The fix is to use the migrator identity, not to grant the app superuser.

`EnsureCreated()` is used nowhere. Reversibility has been exercised: `dotnet ef database update 0`
followed by a re-apply completes cleanly, and the `vector` extension **survives** the rollback.
Research D14 predicted EF would emit a `DROP EXTENSION` that cascades; in practice the EF 10
Npgsql provider models the extension as a database annotation, so `Down()` drops tables only.
No hand-editing of the generated migration was needed.

On managed Postgres, Azure Flexible Server additionally needs `azure_pg_admin` membership plus
`vector` in the `azure.extensions` allow-list; AWS RDS/Aurora on PG13+ needs only ordinary
`CREATE` on the database.

### 4. Configure a source and a host policy

```bash
mkdir -p ./targets
cat > ./targets/local.txt <<'TARGETS'
# Comment lines and blanks are ignored
http://localhost:8099/listing/golf
http://localhost:8099/listing/focus
TARGETS
```

A host cannot be fetched until it has a `HostAccessPolicy` entry (FR-008). Policies live in
configuration, not code (gate T-5):

```jsonc
// appsettings.Development.json
{
  "CarIngest": {
    "Sources": [{ "Location": "./targets" }],
    "UserAgent": "CarIngestBot/1.0 (+https://example.invalid/bot; ops@example.invalid)",
    "HostPolicies": {
      "localhost:8099": {
        "RequestsPerPeriod": 1,
        "ReplenishmentPeriod": "00:00:01",
        "MaxConcurrency": 1,
        "ExtractorKind": "json-ld",
        "PermittedFields": ["Make","Model","Year","MileageValue","MileageUnit",
                            "PriceAmount","PriceCurrency","FuelType","Transmission",
                            "BodyStyle","Colour"]
      }
    },
    "Embedding": { "Provider": "ollama", "ModelId": "nomic-embed-text" }
  }
}
```

Startup validation rejects a policy naming a field outside the allowlist, so a configuration
mistake surfaces at boot rather than as a mid-run extraction failure.

### 5. Run it

```bash
dotnet run --project src/CarIngest.Api
curl -s http://localhost:5000/v1/health | jq .
```

---

## Validation scenarios

Scenario 1 uses a local stub server so nothing touches a real listing site. Start one from the
repository fixtures:

```bash
# Serves fixtures/ over HTTP on :8099, including a permissive /robots.txt
dotnet run --project tests/CarIngest.Infrastructure.Tests --stub-server 8099
```

### Scenario 1 — Ingest a batch (US1, P1 · SC-001, SC-004)

```bash
SOURCE_ID=$(curl -s http://localhost:5000/v1/health | jq -r '.sources[0].sourceId')

RUN_ID=$(curl -s -X POST http://localhost:5000/v1/runs \
  -H 'Content-Type: application/json' \
  -d "{\"sourceId\":\"$SOURCE_ID\"}" | jq -r '.id')

# 202 Accepted — the run executes in the background worker
curl -s "http://localhost:5000/v1/runs/$RUN_ID" | jq '.status, .totals'
```

**Expected**: `status` reaches `succeeded`; `totals.addressesRead == 2`, `stored == 2`,
`failed == 0`. Then confirm full provenance on a document — SC-004 treats incomplete provenance
as a defect:

```bash
DOC_ID=$(curl -s "http://localhost:5000/v1/runs/$RUN_ID/outcomes" | jq -r '.items[0].documentId')
curl -s "http://localhost:5000/v1/documents/$DOC_ID" | jq '.provenance, .embeddings'
```

**Expected**: `sourceUrl`, `retrievedAt` (UTC `Z`), `extractorVersion`, `discoveryMethod`
(`operator-file`), `lastVerifiedAt` all present and non-null; one embedding descriptor carrying
`modelId` and a digest-shaped `modelVersion`.

### Scenario 2 — Failures are visible and retryable (US2, P2 · SC-007)

Point the stub at fixtures that fail in different ways, then:

```bash
curl -s "http://localhost:5000/v1/runs/$RUN_ID/outcomes?outcome=failed" \
  | jq '.items[] | {url, reasonCode, message}'
```

**Expected**: every failure carries a `reasonCode` from the closed set in
[data-model.md](./data-model.md#reason-codes) — `schema-drift` naming the missing field,
`jsonld-absent`, `robots-disallowed`, and so on. This is what makes "extraction is broken"
distinguishable from "nothing matched".

Retry only the failures:

```bash
curl -s -X POST http://localhost:5000/v1/runs \
  -H 'Content-Type: application/json' \
  -d "{\"sourceId\":\"$SOURCE_ID\",\"retryOfRunId\":\"$RUN_ID\"}" | jq '.id, .triggeredBy'
```

**Expected**: a new run with `triggeredBy: "retry"` whose `addressesRead` equals the **failed**
count of the original. Already-stored documents are untouched — assert their `retrievedAt` is
unchanged.

### Scenario 3 — Re-ingest creates no duplicates (US3, P3 · SC-008)

```bash
curl -s -X POST http://localhost:5000/v1/runs -H 'Content-Type: application/json' \
  -d "{\"sourceId\":\"$SOURCE_ID\"}" > /dev/null
sleep 5
curl -s "http://localhost:5000/v1/runs/$RUN_ID2" | jq '.totals'
```

**Expected**: `unchanged == 2`, `stored == 0`. Document count in the store is still 2, and each
document's `lastVerifiedAt` advanced while `retrievedAt` did not. Then edit a fixture's price,
re-run, and expect `stored == 1` with that document's embedding regenerated.

### Scenario 4 — Prove the vectors work (US4, P4 · SC-011)

```bash
curl -s -X POST http://localhost:5000/v1/query \
  -H 'Content-Type: application/json' \
  -d '{"text":"diesel hatchback with low mileage",
       "modelId":"nomic-embed-text",
       "modelVersion":"sha256:…",
       "limit":10}' | jq '.results'
```

**Expected**: the matching document ranks above unrelated ones, ascending by `distance`. A
description matching nothing returns `200` with an empty `results` array — not a `404`.

**Expected failures, which matter as much as the success**:

- Omitting `modelId` or `modelVersion` → `400`. They are required precisely because pgvector
  would otherwise compare vectors across models and return a plausible, meaningless number with
  no error (FR-029).
- Requesting a model identity with no stored embeddings → `422`, distinguishing "wrong model
  asked for" from "nothing matched".

---

## Test suite

```bash
dotnet build  -warnaserror                 # gate W-4: no warnings
dotnet test                                # all tiers; needs Docker running

dotnet test tests/CarIngest.Robots.Tests           # RFC 9309 vectors — no Docker needed
dotnet test tests/CarIngest.Core.Tests             # hashing, dedup, parsing — no Docker needed
dotnet test tests/CarIngest.Infrastructure.Tests   # fixtures, WireMock, Testcontainers
dotnet test tests/CarIngest.Api.Tests              # contract tests vs contracts/
```

No test makes a live network call (gate W-2). Fixtures are **synthetic HTML authored in-repo**,
never recorded captures — recording live traffic from real listing sites would be a ToS wrinkle
and would risk committing real seller PII into the repository, violating FR-010 in the one place
nobody thinks to look.

### The tests that matter most

**PII containment** (`fixtures/<host>/seller-pii-present.html`) — the fixture displays a seller
name, phone number, and email in several DOM positions. The test serializes the stored record
and asserts **none of those literal strings appear anywhere in it**. A CI golden list of PII
patterns is checked against a persisted snapshot, so a future change that widens the record
fails the build rather than quietly leaking.

**robots.txt fail-safe** — `robots.txt` returning 5xx or unreachable must yield
`robots-unavailable` and **no request to the target path**. Assert the request was never issued,
not merely that the outcome was recorded. 4xx means unrestricted; 5xx means assume full
disallow.

**Per-host politeness** — against WireMock, assert request timestamps for one host are separated
by at least the effective crawl delay, and that `max(configured, robots Crawl-delay)` wins so
robots can only slow us down.

**Model-identity isolation** — store embeddings from two model identities for the same document
and assert no query path returns them mixed.

---

## Known gaps before this can run against real sites

Carried from [research.md](./research.md#unresolved-risks); neither is resolvable from
documentation.

- **R1 (HIGH)** — whether real target sites emit schema.org JSON-LD is **unverified**. Research
  was blocked by HTTP 403 at the Cloudflare edge before any page content was served. The first
  Phase 2 task is to validate one page per intended host, honouring robots.txt, and record
  whether a `Car`/`Vehicle` node is present. If it is widely absent, per-host CSS extractors
  become the norm rather than the exception, which changes the effort profile materially.
- **Bot-protected hosts may be ineligible outright.** A site that fingerprints the TLS stack has
  in effect declined automated access. FR-007 forbids circumventing bot defences, so the
  response is to exclude the host — not to route around it. This may shrink the viable target
  list, which is a scoping decision for the operator.
