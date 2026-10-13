<!--
Sync Impact Report
==================
Version change: (unversioned template) -> 1.0.0
Rationale: Initial ratification. The prior file was the unfilled core scaffold with no
concrete governance, so this is the first binding version rather than an amendment.

Principle count: template provided 5 slots; project elected 3 (lightweight profile).

Modified principles:
- [PRINCIPLE_1_NAME] -> I. Ethical & Compliant Collection (NON-NEGOTIABLE)
- [PRINCIPLE_2_NAME] -> II. Resilient, Observable Collection Jobs
- [PRINCIPLE_3_NAME] -> III. Stable Contracts & Traceable Data
- [PRINCIPLE_4_NAME] -> REMOVED (unused slot, lightweight profile)
- [PRINCIPLE_5_NAME] -> REMOVED (unused slot, lightweight profile)

Added sections:
- Technology & Platform Constraints (filled [SECTION_2_NAME])
- Development Workflow & Quality Gates (filled [SECTION_3_NAME])

Removed sections:
- Principle slots 4 and 5 (see above)

Deferred items / follow-up TODOs:
- TODO(TARGET_REGISTRY): the per-host policy registry named in Principle I does not exist
  yet; it must be created with the first scrape target.
- The .NET LTS version is bound by reference ("current LTS") rather than pinned; pin it in
  Technology & Platform Constraints once the solution is scaffolded.

This report is scratch material for review of this amendment and should be deleted before
the amended constitution is committed.
-->

# Web Scrapper Constitution

## Core Principles

### I. Ethical & Compliant Collection (NON-NEGOTIABLE)

Collection is constrained before it is capable. Every request this system issues MUST satisfy
all of the following, and a feature that cannot satisfy them MUST NOT ship:

- **robots.txt is authoritative.** Fetchers MUST resolve and respect the target host's
  `robots.txt` (including `Disallow` and `Crawl-delay`) before requesting a path. The
  directives MUST be re-checked on a cadence, not cached for the life of the process. A path
  disallowed for our agent MUST NOT be requested, and MUST NOT be reachable by operator
  override in production.
- **Per-host rate limiting is mandatory.** Outbound requests MUST pass through a per-host
  limiter with a configured ceiling, and MUST apply exponential backoff with jitter on `429`,
  `503`, and transport failures. The default ceiling applies when a host has no explicit
  policy; concurrency MUST be bounded per host, never only globally.
- **Identify honestly.** Every request MUST send a descriptive `User-Agent` naming this
  service and a contact address. Rotating or spoofing identity to evade blocking, and
  circumventing authentication, paywalls, or bot-defence measures, are prohibited.
- **No personal data.** The system MUST NOT collect, persist, log, or expose personal data.
  Scraping is limited to public, non-personal content. Where a page mixes personal and
  non-personal content, extraction MUST select only the non-personal fields rather than
  storing the page and filtering later. Incidental capture of personal data is an incident:
  it MUST be purged and recorded.

*Rationale:* This project makes automated requests to systems we do not own and stores what
comes back. Those two facts carry legal and reputational exposure that no amount of later
code quality can undo, so the limits are encoded as hard gates rather than review guidance.

### II. Resilient, Observable Collection Jobs

Targets change without warning, so breakage is an expected operating condition and MUST be
survivable and visible:

- Extraction logic MUST be isolated per target. A failing or changed target MUST NOT fail the
  job run for other targets, and MUST NOT take down the API surface.
- Extractors MUST fail loudly on schema drift. When an expected field or selector is missing,
  the run MUST record a typed extraction failure; silently writing a null, empty, or partial
  record is prohibited.
- Every job run MUST emit structured logs and MUST record, at minimum: target, start and end
  time, request count, items extracted, items rejected, and terminal outcome.
- Fetch, extract, and persist MUST be separable stages. A persistence or transform failure
  MUST be retryable without re-fetching the target.
- The service MUST expose a health endpoint reporting per-target last-success time, so a
  target that has quietly stopped producing data is detectable without reading logs.

*Rationale:* A scraper that returns wrong data is worse than one that returns none, and a
long-running service hides both. Loud failure plus per-target isolation keeps one broken
selector from becoming either an outage or a silent data-quality fault.

### III. Stable Contracts & Traceable Data

Consumers depend on the API and the store, so both are treated as published contracts:

- API responses MUST be served from explicit response types owned by the API layer. Internal
  entities, scraper DTOs, and database models MUST NOT be returned directly.
- The HTTP API MUST be versioned. Removing or retyping a response field, or tightening
  request validation, is a breaking change requiring a new API version; additive optional
  fields are not.
- Persisted schema changes MUST ship as checked-in, ordered, reversible migrations. Schema
  changes applied by hand or inferred at runtime are prohibited.
- Every stored record MUST carry provenance: source URL, retrieval timestamp (UTC), and the
  extractor version that produced it. A record whose provenance cannot be established MUST
  NOT be persisted.
- Timestamps MUST be stored and served as UTC.

*Rationale:* Scraped data is a derived asset whose trustworthiness rests entirely on knowing
where and when it came from and which code shaped it. Provenance makes a bad extraction
correctable instead of permanent, and stable contracts keep consumers from breaking when it is
corrected.

## Technology & Platform Constraints

- **Platform.** The service MUST target the current .NET Long Term Support release and MUST
  NOT depend on preview runtimes or preview language features in shipped code.
- **Language settings.** Projects MUST enable nullable reference types and MUST treat
  compiler warnings as errors. Suppressions MUST be narrowly scoped and carry a justifying
  comment.
- **HTTP.** Outbound calls MUST use `IHttpClientFactory`-managed clients. Direct `new
  HttpClient()` instantiation in request or job paths is prohibited. Retry, timeout, and
  circuit-breaking policies MUST be attached at the client level, not written per call site.
- **Async and cancellation.** I/O-bound work MUST be async end to end and MUST accept and
  honour a `CancellationToken`. Blocking on async code (`.Result`, `.Wait()`,
  `GetAwaiter().GetResult()`) is prohibited outside program entry points.
- **Configuration and secrets.** Per-host policies, schedules, and rate ceilings MUST live in
  configuration, not in code. Secrets and credentials MUST NOT be committed; they are
  supplied via environment or a secret store.
- **Dependencies.** Each added third-party package MUST be justified in the feature plan.
  Choices not fixed above (scheduler, persistence provider, HTML parser, headless browser)
  are deferred to the feature plan that first needs them, and once chosen MUST be reused
  rather than duplicated by a second library serving the same role.

## Development Workflow & Quality Gates

- Work MUST flow through the Spec Kit sequence — specify, plan, tasks, implement — so that
  intent is recorded before code. Changes that bypass it MUST be retrofitted with a spec.
- Extractors MUST be tested against checked-in recorded fixtures of real target responses.
  Tests MUST NOT make live network calls; a test suite that only passes online is a failing
  suite.
- Every new scrape target MUST be accompanied, in the same change, by its per-host policy
  entry (rate ceiling, robots evaluation, declared non-personal fields) and at least one
  extraction fixture test.
- The build MUST be clean — compile with no warnings and all tests passing — before a change
  is considered complete.
- Reviews MUST verify Principle I compliance explicitly. A change that touches fetching,
  logging, or persistence is reviewed for robots handling, rate limiting, and personal-data
  exposure regardless of its stated purpose.

## Governance

This constitution supersedes conflicting practices, conventions, and prior decisions. Where a
feature plan, task list, or review comment conflicts with a principle here, this document
wins and the conflicting artifact MUST be corrected.

**Amendment procedure.** Amendments MUST be made by updating this file through the
constitution workflow, MUST state the rationale for the change, and MUST note any migration
required of existing code. An amendment that invalidates shipped behaviour MUST identify the
affected areas and the remediation path before it is adopted.

**Versioning policy.** This document is versioned semantically:

- **MAJOR** — a principle is removed or redefined in a way that permits what it previously
  forbade, or forbids what it previously permitted.
- **MINOR** — a principle or governed section is added, or existing guidance is materially
  expanded.
- **PATCH** — clarification, wording, or typo fixes that do not change what is required.

**Compliance review.** Compliance is verified at review time against the gates in Development
Workflow & Quality Gates. Complexity and added dependencies MUST be justified against the
lightweight principle set rather than assumed acceptable. Principle I is non-negotiable: it is
not subject to waiver, deadline pressure, or operator override, and a violation found in
production is handled as an incident ahead of feature work.

**Version**: 1.0.0 | **Ratified**: 2026-10-11 | **Last Amended**: 2026-10-11
