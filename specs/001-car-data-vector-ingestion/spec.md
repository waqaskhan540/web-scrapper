# Feature Specification: Car Data Vector Ingestion

**Feature Branch**: `001-car-data-vector-ingestion`

**Created**: 2026-10-11

**Status**: Draft

**Input**: User description: "build a service which reads uRLs containing car data  from a directory and scraps the content of URL and store as vector data in posgres database"

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Ingest a batch of car pages into the semantic store (Priority: P1)

A data operator places one or more files listing car-page web addresses into a designated
source location. They start an ingestion run. The service reads every address, retrieves each
page, extracts the car-related content, converts that content into a semantic representation,
and stores it together with a record of where and when it came from. When the run finishes the
operator sees how many addresses were read, how many pages were stored, and how many were
rejected.

**Why this priority**: This is the entire value of the feature. Without it there is nothing in
the store, and every other story operates on data this story produces. It is the smallest slice
that is independently useful: a populated, attributable semantic store of car content.

**Independent Test**: Place a source file containing a handful of known car-page addresses in
the source location, trigger a run, and confirm that a stored record exists for each permitted
address, that each record carries its source address and retrieval time, and that the run
summary counts match the input.

**Acceptance Scenarios**:

1. **Given** a source file listing 10 permitted car-page addresses and an empty store, **When**
   an ingestion run is started, **Then** 10 car documents are stored, each with a semantic
   representation, its source address, and a retrieval timestamp, and the run summary reports
   10 read and 10 stored.
2. **Given** a source location holding three source files, **When** an ingestion run is
   started, **Then** addresses from all three files are processed in the single run and the
   summary reports the combined totals.
3. **Given** a source file containing the same address twice, **When** an ingestion run is
   started, **Then** the page is retrieved once and exactly one car document is stored for that
   address.
4. **Given** a source file containing a blank line and a line that is not a valid web address,
   **When** an ingestion run is started, **Then** the malformed line is rejected and reported
   individually, and every valid address in the same file is still processed.
5. **Given** an address whose host forbids automated retrieval of that path, **When** an
   ingestion run is started, **Then** the page is not retrieved, the address is recorded as
   skipped-by-policy, and the run continues with the remaining addresses.

---

### User Story 2 - See and recover from ingestion failures (Priority: P2)

An operator needs to know whether the store is complete and current. After a run they review a
per-address outcome: stored, skipped by policy, or failed, with a reason for each failure. They
can re-run only the failed addresses without re-retrieving pages that already succeeded, and
they can see at a glance when a source last produced a successful result.

**Why this priority**: Car pages change layout and disappear without warning, so partial
failure is routine rather than exceptional. Without visible, retryable outcomes the store
silently rots and nobody can tell the difference between "no cars matched" and "extraction has
been broken for a week". It depends on Story 1 having produced outcomes to report.

**Independent Test**: Run an ingestion where some addresses are unreachable and some succeed,
confirm the per-address outcomes and failure reasons are retrievable, trigger a retry of
failures only, and confirm the already-stored pages are not retrieved again.

**Acceptance Scenarios**:

1. **Given** a run in which 7 addresses stored and 3 failed, **When** the operator inspects the
   run, **Then** all 10 addresses are listed with their outcome and each failure carries a
   machine-readable reason and a human-readable message.
2. **Given** a completed run with failures, **When** the operator triggers a retry of that
   run's failures, **Then** only the previously failed addresses are retrieved and the
   successful documents are left untouched.
3. **Given** a page that is retrieved successfully but whose expected car fields are absent,
   **When** extraction runs, **Then** no partial or empty car document is stored and the
   address is recorded as an extraction failure naming the missing fields.
4. **Given** any completed run, **When** the operator checks service health, **Then** the time
   of the last successful ingestion is reported so a silently-stalled source is detectable
   without reading logs.

---

### User Story 3 - Keep stored car data current without duplicating it (Priority: P3)

The same car pages are ingested repeatedly over time. When a page's content is unchanged since
the last run, the operator does not want a second copy or a wasted retrieval. When the content
has changed, the stored semantic representation must be replaced so that retrieval reflects the
current page, while the record of when each version was seen is preserved.

**Why this priority**: Makes repeat operation sustainable and keeps the store trustworthy, but
the feature delivers value on a single pass without it. It depends on Stories 1 and 2.

**Independent Test**: Ingest a set of addresses twice with no change to the pages and confirm no
duplicate documents and no redundant retrievals; alter one page's content, re-ingest, and
confirm that document's semantic representation is replaced and its last-seen time advances.

**Acceptance Scenarios**:

1. **Given** an address already stored and whose page content is unchanged, **When** a
   subsequent run processes it, **Then** no duplicate document is created and the document's
   last-verified time is updated.
2. **Given** an address already stored whose page content has materially changed, **When** a
   subsequent run processes it, **Then** the stored semantic representation is replaced with one
   derived from the new content and the change is recorded.
3. **Given** an address already stored whose page now returns "not found", **When** a subsequent
   run processes it, **Then** the existing document is marked unavailable rather than deleted
   outright, and the outcome is reported.

---

### User Story 4 - Prove the semantic store actually works (Priority: P4)

An operator needs to confirm that what was stored is genuinely retrievable by meaning, not
merely present. They submit a free-text description of a car they know was ingested — "cheap
reliable estate with low mileage" — and see the stored documents ranked by closeness of
meaning, each identified by its source address. This is a validation capability for operators
and tests, not a consumer-facing search product.

**Why this priority**: Without it, "the vectors are stored" is an unverified claim — a silent
failure in the conversion step would look identical to success. It is sequenced last because
it reads data the first three stories produce, and it is deliberately scoped to validation:
public search, ranking quality, and filtering are a separate later feature.

**Independent Test**: Ingest a known set of car pages, submit a free-text description matching
one of them, and confirm that document ranks above the unrelated ones and carries its source
address.

**Acceptance Scenarios**:

1. **Given** a populated store, **When** an operator submits a free-text car description,
   **Then** stored documents are returned in descending order of semantic similarity, each with
   its source address and retrieval time.
2. **Given** a populated store, **When** an operator submits a description matching nothing
   stored, **Then** an empty result is returned rather than an error or an arbitrary document.
3. **Given** documents produced by two different semantic-representation methods, **When** a
   validation query runs, **Then** results are confined to a single method and version rather
   than ranking incomparable representations against each other.

---

### Edge Cases

- **Source location is empty or absent**: the run completes reporting zero addresses read, and
  is distinguishable from a run that failed to start.
- **Source file is unreadable or in an unrecognised format**: the file is rejected and named in
  the run report; other files in the same location are still processed.
- **Same address appears in two different source files**: retrieved once, stored once.
- **Address redirects to a different host**: the destination host's own retrieval policy is
  evaluated before the redirect is followed.
- **Page requires sign-in, payment, or defeating a bot challenge**: not retrieved; recorded as
  skipped by policy.
- **Page is reachable but is not a car page** (a category index, an error page served with a
  success status, a parked domain): rejected as a non-match rather than stored as an empty car.
- **Page content renders only after in-browser script execution**: either retrieved by a method
  that resolves the content, or recorded as an extraction failure — never stored as an empty or
  skeletal document.
- **Page carries seller personal data** (name, phone number, address, email, photograph of a
  person or a legible licence plate): the personal fields are excluded from what is stored,
  including from the semantic representation.
- **Host responds with a rate-limit or temporary-unavailable signal**: retrieval backs off and
  retries within the run's limits; persistent refusal is a reported failure, not a crash.
- **Host becomes unreachable mid-run**: remaining addresses for that host fail or defer without
  affecting addresses on other hosts.
- **Page is very large** (far beyond a typical listing): handled within a bounded size limit;
  oversize content is rejected with a stated reason rather than exhausting the service.
- **Content is in an unexpected language**: stored with its language recorded; retrieval quality
  across languages is out of scope for this feature.
- **Semantic representation cannot be produced** (the conversion step is unavailable): the
  retrieved content is retained for retry so the page need not be fetched again.
- **Two runs are started at once**: the second is rejected or queued; the same address is never
  processed concurrently by two runs.
- **Run is cancelled or the service restarts mid-run**: completed addresses stay stored and
  attributable; the run is resumable or cleanly reported as interrupted.

## Requirements *(mandatory)*

### Functional Requirements

**Reading the address source**

- **FR-001**: System MUST read car-page web addresses from a configured filesystem folder
  holding one or more plain-text address-list files. System MUST NOT discover additional
  addresses by following links out of retrieved pages — it ingests only the addresses it is
  given. Crawling an online listing directory is explicitly deferred to a later feature.
- **FR-002**: System MUST process every address-list file found in the source location in a
  single run, and MUST report the files it read.
- **FR-003**: System MUST reject and individually report each malformed or non-address entry
  without aborting the run or skipping the remaining valid entries in the same file.
- **FR-004**: System MUST de-duplicate addresses within and across source files so that each
  distinct address is retrieved at most once per run.

**Retrieval policy — binding, per Constitution Principle I**

- **FR-005**: System MUST evaluate the target host's automated-access policy before requesting a
  path, MUST NOT request a path that policy forbids, and MUST record such addresses as skipped
  by policy. This MUST NOT be overridable by operator configuration in production.
- **FR-006**: System MUST apply a per-host request-rate ceiling and bounded per-host
  concurrency, and MUST back off and retry on rate-limit and temporary-failure responses.
- **FR-007**: System MUST identify itself on every request with a descriptive agent name and a
  contact address, and MUST NOT rotate or disguise its identity to evade blocking, nor
  circumvent sign-in, payment, or bot-protection barriers.
- **FR-008**: Each new ingestion host MUST have a recorded access policy entry (rate ceiling,
  access-policy evaluation, and the non-personal fields permitted for extraction) before the
  system retrieves from it.

**Extraction and personal data**

- **FR-009**: System MUST extract only car-describing content from each retrieved page.
- **FR-010**: System MUST NOT store, log, or expose personal data. Where a page mixes personal
  and non-personal content, extraction MUST select only the permitted non-personal fields
  rather than storing the page and filtering afterwards. Personal data MUST NOT reach the
  semantic representation.
- **FR-011**: System MUST reject a page whose expected car content is absent or unrecognisable,
  recording a typed extraction failure naming what was missing. Storing a partial, empty, or
  null-filled car document is prohibited.
- **FR-012**: System MUST enforce a bounded maximum retrieved-content size and reject oversize
  content with a stated reason.

**Storing semantic representations**

- **FR-013**: System MUST convert each accepted car document's content into a semantic
  representation suitable for meaning-based similarity comparison, and MUST store it durably
  alongside the extracted content.
- **FR-014**: System MUST record, for every stored document, the semantic-representation method
  and version used to produce it, so that representations produced by different methods are
  never compared as if equivalent.
- **FR-015**: System MUST store each document with full provenance: source address, retrieval
  timestamp, the version of the extraction logic that produced it, and how the address was
  obtained. A document whose provenance cannot be established MUST NOT be stored.
- **FR-015a**: The recorded means of obtaining an address MUST be expressed as an open set that
  names the supplying address-list file for operator-supplied addresses, and MUST be able to
  express a link-discovered origin without altering stored records. This keeps a future
  crawling feature additive rather than a migration.
- **FR-016**: System MUST store and report all timestamps in UTC.
- **FR-017**: System MUST retain each accepted document's extracted content, not only its
  semantic representation, so that representations can be regenerated without re-retrieving the
  page.
- **FR-018**: On re-ingestion of an already-stored address, System MUST update rather than
  duplicate: unchanged content updates the last-verified time, materially changed content
  replaces the stored semantic representation and records the change.

**Operating the service**

- **FR-019**: Operators MUST be able to start an ingestion run and retrieve that run's status
  and summary totals: addresses read, stored, skipped by policy, and failed.
- **FR-020**: System MUST record a per-address outcome for every address in a run, with a
  machine-readable reason for each non-success.
- **FR-021**: Operators MUST be able to retry only the failed addresses of a prior run without
  re-retrieving successfully stored documents.
- **FR-022**: System MUST isolate extraction per host and per address: a failing address or a
  changed site layout MUST NOT fail other addresses in the run, and MUST NOT interrupt the
  service's request-handling surface.
- **FR-023**: System MUST emit structured logs recording, per run: target source, start and end
  time, request count, documents stored, documents rejected, and terminal outcome.
- **FR-024**: System MUST expose a health report including the last successful ingestion time
  per source.
- **FR-025**: System MUST separate retrieval, extraction, and storage such that a storage or
  conversion failure is retryable without re-retrieving the page.
- **FR-026**: System MUST prevent the same address from being processed concurrently by two
  runs.
- **FR-027**: System MUST version its externally consumable interface, and MUST serve responses
  from explicitly declared response shapes rather than exposing internal storage structures.
- **FR-028**: Operators MUST be able to submit a free-text description and receive stored car
  documents ranked by semantic similarity, each with its source address and retrieval
  timestamp, in order to verify that stored representations are retrievable by meaning. This
  capability is scoped to operator and automated-test validation; it is NOT a consumer search
  contract, and relevance tuning, filtering, pagination, and facets are out of scope.
- **FR-029**: A similarity comparison MUST be confined to representations produced by a single
  method and version (per FR-014). Ranking representations from differing methods against one
  another is prohibited.

### Key Entities *(include if feature involves data)*

- **Address Source**: A configured filesystem folder holding address-list files. Attributes:
  location, files discovered, last read time.
- **Ingestion Target**: One distinct car-page web address drawn from a source. Attributes:
  address, host, means of discovery (an open set — currently the supplying address-list file,
  extensible to a link-discovered origin per FR-015a), first seen, last attempted.
- **Host Access Policy**: The recorded rules governing retrieval from one host. Attributes:
  host, request-rate ceiling, access-policy evaluation and when it was checked, permitted
  non-personal fields.
- **Car Document**: One accepted car described by one retrieved page. Attributes: extracted
  car-describing content, extracted non-personal field values, detected language, availability
  state, provenance (source address, retrieval timestamp in UTC, extraction logic version),
  last-verified time. Relationships: belongs to one Ingestion Target; carries one or more
  Semantic Representations.
- **Semantic Representation**: The meaning-based form of a Car Document's content used for
  similarity comparison. Attributes: the representation itself, the method and version that
  produced it, the content span it covers, when it was produced. Relationships: belongs to one
  Car Document.
- **Ingestion Run**: One execution over a source. Attributes: run identifier, source, start and
  end time in UTC, counts read / stored / skipped / failed, terminal outcome, initiating
  operator or schedule.
- **Address Outcome**: The result for one Ingestion Target within one Ingestion Run. Attributes:
  outcome (stored, skipped by policy, failed), typed reason, human-readable message, attempt
  count. Relationships: links one Ingestion Run to one Ingestion Target.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: An operator can go from "a file of car-page addresses" to "those cars present and
  attributable in the store" in a single action, with no manual step per address.
- **SC-002**: A run over 1,000 addresses completes without operator intervention, and a run over
  10,000 addresses completes without exhausting service memory or being killed.
- **SC-003**: At least 95% of addresses that are reachable and permitted result in a stored,
  fully attributable car document on the first attempt.
- **SC-004**: 100% of stored documents carry a source address, a UTC retrieval timestamp, and
  the extraction version that produced them. A stored document with incomplete provenance is a
  defect.
- **SC-005**: Zero stored documents, log entries, or interface responses contain personal data,
  verified by audit of a sample of ingested pages known to display seller contact details.
- **SC-006**: Zero requests are issued to paths the target host's access policy forbids,
  verified against recorded request logs.
- **SC-007**: After any run, an operator can determine the outcome of every single address, and
  can distinguish "nothing matched" from "extraction is broken" without reading raw logs.
- **SC-008**: Re-running an unchanged source creates zero duplicate documents and issues no
  redundant page retrievals.
- **SC-009**: A site layout change that breaks extraction for one host is surfaced within one
  run and leaves other hosts' ingestion unaffected.
- **SC-010**: A broken extraction can be corrected and the affected documents' semantic
  representations regenerated from retained content, without re-retrieving the source pages.
- **SC-011**: For a known ingested car, a free-text description of that car ranks its document
  above unrelated documents in the store — demonstrable by an operator in one step and asserted
  by an automated test, so a silent failure in semantic conversion cannot pass as success.

## Assumptions

Defaults chosen where the feature description did not specify. Each is a decision that can be
revisited, not a discovered fact.

- **Source format**: Address-list files are plain-text, one web address per line, with blank
  lines and comment lines ignored. Other formats are out of scope for this feature.
- **Trigger**: Ingestion is started on demand by an operator. Scheduled or continuous ingestion
  is out of scope for this feature; the service is nonetheless long-running, per the project's
  service-oriented constitution.
- **Address discovery**: The service consumes addresses it is given from the configured folder.
  It does not follow links out of a retrieved page. Crawling an online car-listing directory is
  a deferred follow-on feature, and FR-015a keeps the stored provenance shape ready for it so
  that adding it does not require reworking existing records.
- **Target scope**: The hosts to be ingested are expected to be few and known in advance, which
  is what makes a per-host access policy entry per host (FR-008) practical.
- **Permission**: The operator is responsible for having the right to retrieve from the hosts
  they supply. The service enforces published access policy and rate limits, but cannot
  establish a legal right to the data.
- **Field set**: "Car data" is taken to mean the publicly posted description of a vehicle —
  make, model, year, mileage, price, condition, specification, and free-text description.
  Seller identity and contact details are excluded by FR-010.
- **Freshness**: Documents are considered current until re-verified by a later run. No
  automatic expiry is defined.
- **Retention**: Extracted content and provenance are retained indefinitely until an operator
  removes them. Removal tooling is out of scope for this feature.
- **Access control**: The ingestion-control interface is operator-only and assumed to run in a
  trusted network position; the project has no authentication system yet to reuse.
- **Volume**: Sized for tens of thousands of documents, not millions. Multi-tenancy,
  sharding, and horizontal scale-out are out of scope.
- **Retrieval surface**: Meaning-based retrieval exists in this feature only to prove the store
  works (FR-028). A consumer-facing search interface — with relevance tuning, structured
  filters, pagination, and its own versioning obligations — is a separate later feature.
- **Correctness of the semantic store**: Retrieval quality is only as good as the extracted
  content; tuning similarity quality is out of scope for this feature.
