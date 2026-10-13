# CarIngest

Ingests car-listing pages whose addresses are supplied in plain-text files, extracts only
allowlisted non-personal vehicle fields, embeds them, and stores the vectors in
Postgres/pgvector with full provenance.

Built against the project constitution in `.specify/memory/constitution.md`. Specification,
plan, and task breakdown live in `specs/001-car-data-vector-ingestion/`.

## Three things to know before changing anything

**1. robots.txt handling is non-overridable.** `CarIngest.Robots` is deliberately its own
assembly with no bypass, override, or force parameter anywhere on its surface. If a task seems
to need one, the correct move is to exclude the host, not to add the flag. An architecture test
fails the build if such a member appears.

**2. There is no free-text seller description, anywhere.** `VehicleFields` has no description
member and no catch-all dictionary, so personal data has no field to occupy. This is structural
rather than probabilistic on purpose: regex scrubbing of free text has false negatives that
cannot be proven zero, and personal names in particular are not reliably detectable. Four
layers enforce it — the record shape, log scrubbing, literal-string fixture assertions, and a
CI golden-list guard.

**3. Migrations and runtime use different database identities.** `CREATE EXTENSION vector`
requires superuser. A correctly least-privileged runtime account **will fail** on `Migrate()`,
and that is the intended behaviour — the fix is the migrator identity, never granting the app
superuser.

## Prerequisites

| Requirement | Notes |
|-------------|-------|
| .NET SDK 10 | `net10.0`, the current LTS. Warnings are errors. |
| A container runtime | Docker **or podman**. Needed for Postgres and for the storage tests. |
| Ollama (optional) | Only for real embeddings; tests use a deterministic fake. |

### Running the tests under podman

Testcontainers talks to the Docker API. Podman exposes a compatible socket, but the path has to
be supplied or Testcontainers looks for Docker's and fails with a connection error that reads
like a bug rather than a missing variable:

```bash
export DOCKER_HOST="unix://$(podman machine inspect \
  --format '{{.ConnectionInfo.PodmanSocket.Path}}')"
export TESTCONTAINERS_RYUK_DISABLED=true
```

## Build and test

```bash
dotnet build                  # must be warning-free (constitution gate T-2)
dotnet test                   # storage tests need the container runtime up

# These three need no container:
dotnet test tests/CarIngest.Robots.Tests
dotnet test tests/CarIngest.Core.Tests
dotnet test tests/CarIngest.Api.Tests
```

No test makes a live network call. Fixtures are synthetic HTML authored in `fixtures/` —
recording live captures is prohibited (see `fixtures/README.md`).

## Running locally

```bash
podman compose -f docker-compose.dev.yml up -d        # or docker compose

export CarIngest__ConnectionStrings__Migrator='Host=localhost;Database=carringest;Username=postgres;Password=postgres'
dotnet ef database update --project src/CarIngest.Infrastructure \
                          --startup-project src/CarIngest.Infrastructure

ollama pull nomic-embed-text
dotnet run --project src/CarIngest.Api
```

Then see `specs/001-car-data-vector-ingestion/quickstart.md` for the four end-to-end validation
scenarios.

## Layout

```text
src/
├── CarIngest.Core/            domain + abstractions; no I/O dependencies, enforced by test
├── CarIngest.Robots/          in-house RFC 9309 parser and evaluator
├── CarIngest.Infrastructure/  fetching, extraction, embedding, storage, query
└── CarIngest.Api/             /v1 endpoints, API-owned response types, background worker

tests/                         one project per production assembly
fixtures/                      synthetic listing HTML, never recorded captures
```

## API

All routes sit under `/v1`. Full contract: `specs/001-car-data-vector-ingestion/contracts/`.

| Route | Purpose |
|-------|---------|
| `POST /v1/runs` | Start a run (202). `409` if one is already active for that source. |
| `GET /v1/runs/{id}` | Status and totals. |
| `GET /v1/runs/{id}/outcomes` | Per-address outcome with a machine-readable reason code. |
| `GET /v1/documents/{id}` | A stored document with provenance. Never returns the vector. |
| `POST /v1/query` | Validation query. `modelId` and `modelVersion` are **required**. |
| `GET /v1/health` | Health plus per-source `lastSuccessfulRunAt`. |

`POST /v1/query` requires model identity rather than defaulting it because pgvector will compare
vectors from two different models and return a plausible number with no error. Defaulting the
discriminator would make the dangerous call the easy one.

## Configured ingestion scope

Currently **one host, one path**:

```
https://webscraper.io/test-sites/load-more
```

That page is a classic-car listing (Mercedes-Benz W123, BMW E24, Jaguar XJ6, Ferrari F40…) and
was chosen because webscraper.io is purpose-built for scraping practice and carries no personal
data. Validated against the live site on 2026-10-11 — see
`specs/001-car-data-vector-ingestion/r1-findings.md`.

Three things about this host shape the code:

1. **No JSON-LD.** It publishes `schema.org/Product` microdata instead, so the host policy
   selects `MicrodataCarExtractor`. Selection is by configuration — absent JSON-LD never
   silently falls back to selector scraping.
2. **One page, many cars.** Six cards on one address, so a document is keyed by
   `(address, CardKey)` rather than by address alone.
3. **Detail pages are robots-disallowed.** `/test-sites/product/<slug>` is forbidden, so every
   stored field comes from the listing page. The detail URL is recorded but never requested, and
   a test asserts the absence of that request.

Also disallowed and therefore never fetched: `/test-sites/load-more/` (the directory and its
AJAX pagination), `/test-sites/pagination*`, and `/test-sites/scroll/`. Note that
`/test-sites/load-more` **without** a trailing slash is permitted — RFC 9309 matches `Disallow`
values as prefixes, and the rule is longer than that path.

The e-commerce test sites on the same host (`/test-sites/e-commerce/*`) are allowed but contain
laptops and phones, not cars, so they are not ingested.
