# Postgres settings this service depends on

## `max_parallel_workers_per_gather` — required, not optional

The embedding column carries **no ANN index** (research D4): at the scale this service is
designed for, exact scan is simpler and avoids HNSW build cost and recall tuning. That decision
depends on parallel sequential scan being enabled.

Measured on pgvector 0.8.7, 100,000 rows of `vector(768)` (405 MB table), top-10 by cosine
distance:

| Configuration | Execution time |
|---------------|----------------|
| Defaults | 327–503 ms |
| `max_parallel_workers_per_gather = 4` | 91–94 ms |

The 200 ms goal in the implementation plan is **missed without this setting** and met with it.

```sql
ALTER SYSTEM SET max_parallel_workers_per_gather = 4;
ALTER SYSTEM SET max_parallel_workers = 8;

-- The vector table is large enough that Postgres parallelizes it anyway, but setting this
-- low removes the guesswork if the corpus is smaller than the default threshold.
ALTER SYSTEM SET min_parallel_table_scan_size = '8MB';
SELECT pg_reload_conf();
```

## When to revisit the no-index decision

The margin at 100k rows is roughly 2x, not 20x. Add an HNSW index when the corpus approaches
**250k rows**, rather than waiting for a latency complaint:

```sql
CREATE INDEX CONCURRENTLY ix_embedding_hnsw
  ON car_document_embedding USING hnsw (embedding vector_cosine_ops)
  WITH (m = 16, ef_construction = 200);
```

HNSW rather than IVFFlat: it needs no training step, so it does not impose an
"insert data before creating the index" ordering on migrations, and it holds recall without
query-time parameter tuning.

**Dimension limit to respect:** an indexed `vector` column caps at **2,000 dimensions**. The
default model (`nomic-embed-text`, 768) is comfortably inside that, and startup validation
rejects a configured dimension above 2,000 so the option is never silently forfeited.

## Migration privileges

`CREATE EXTENSION vector` requires superuser. Confirmed: a least-privileged role attempting it
gets `ERROR: permission denied to create extension "vector"`. Migrations therefore run under the
`Migrator` connection string and the service runs under `Runtime`. Do not resolve this by
granting the application superuser.

- **Azure Database for PostgreSQL Flexible Server**: the login needs `azure_pg_admin`
  membership, and `vector` must be in the `azure.extensions` allow-list.
- **AWS RDS / Aurora (PG13+)**: ordinary `CREATE` on the database is sufficient.
