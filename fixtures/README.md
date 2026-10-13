# Extraction fixtures

Fixtures here are **synthetic HTML authored in this repository**, hand-written to match the
shape of real schema.org `Car`/`Vehicle` JSON-LD.

## Recording live captures is prohibited

Do not add HTML captured from a real listing site. Two reasons, both load-bearing:

1. **PII.** A captured page carries real seller names, phone numbers, and email addresses.
   Committing one would store personal data in the repository — violating FR-010 in the one
   place nobody thinks to look, and it survives in git history after deletion.
2. **Terms of service.** Recording and redistributing a site's pages is a separate question
   from reading them politely, and the project has no permission to do it.

Synthetic fixtures are also better tests: they can express a failure mode (a missing `Year`, a
malformed JSON-LD block, three competing `@type` nodes) on demand, which a captured page cannot.

## Layout

Fixtures live under `fixtures/<host>/`, mirroring the host whose extractor profile they
exercise. Per constitution gate W-3, every new ingestion host ships with its host policy entry
and at least one fixture test in the same change.
