# Interface Contracts: Car Data Vector Ingestion

**Date**: 2026-10-11 | **Plan**: [../plan.md](../plan.md)

This service exposes one external interface: a versioned HTTP control API used by the operator
to start ingestion runs, inspect outcomes, check health, and run the validation query.

| File | What it is |
|------|------------|
| [ingestion-api.v1.md](./ingestion-api.v1.md) | Narrative contract — endpoints, semantics, error model, the rules a test must assert |
| [openapi.v1.yaml](./openapi.v1.yaml) | Machine-readable OpenAPI 3.1 description of the same surface |

## Contract rules carried from the constitution

These are not style preferences; they are gates from `.specify/memory/constitution.md` v1.0.0.

1. **Versioned** (gate III-2). Every route sits under `/v1/`. Removing a response field,
   retyping one, or tightening request validation requires `/v2/`. Adding an optional field
   does not.
2. **API-owned response types** (gate III-1). Responses are declared in
   `src/CarIngest.Api/Contracts/` and are never EF entities, scraper DTOs, or database models.
   The storage entities in [../data-model.md](../data-model.md) do not appear on the wire.
3. **No personal data on the wire** (Principle I-4). The API cannot expose what is never
   stored — but response shapes are reviewed against this anyway, including error messages.
   `message` fields carry field names and status, never page content.
4. **UTC** (gate III-5). All timestamps are RFC 3339 with an explicit `Z` offset.
5. **Operator-only.** There is no authentication system in this project yet, so the API assumes
   a trusted network position (recorded as an assumption in [../spec.md](../spec.md)). It is not
   a public search product — see the scope note on the query endpoint.

## Why there is no consumer search endpoint

`POST /v1/query` exists to **prove the store works** (FR-028), not to serve a search product.
Relevance tuning, structured filtering, pagination, and facets are explicitly out of scope and
deferred to a later feature. Treating the validation query as a public contract now would commit
us to versioning obligations for a surface whose shape we have not yet designed.
