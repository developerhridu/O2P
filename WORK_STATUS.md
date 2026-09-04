# O2P — Test & Fix Status (Handoff)

_Last updated: 2026-07-11. Author: Claude (Opus 4.8) session._

This document captures the state of a "run the project, find & fix bugs, test everything twice" pass so the work can be resumed later.

> **Session 2 addendum (2026-07-11): FR-5 chunk planner implemented.** See the "FR-5 chunk planner" section below. Backend + frontend build clean; the e2e API suite passed **52/52 twice** on fresh isolated DBs. **Caveat:** the e2e suite runs entirely in *mock mode*, so the new real-Oracle planner/reader SQL is build-verified and reviewed by inspection but **not exercised against a live Oracle** in this environment.

---

## TL;DR

- Backend (`dotnet build src/O2P.slnx`) and frontend (`web`: `tsc -b && vite build`) both **build clean** (0 errors).
- Found and fixed **5 real backend correctness bugs** (details below).
- A comprehensive **API end-to-end suite (52 assertions)** was written and **passed twice, 52/52** on fresh databases — covering every feature plus a full mock migration.
- All testing was done against an **isolated local Docker Postgres** (port 7937). The production metadata DB (`application.naasbd.com`) was **never touched**.

---

## Bugs found & fixed

All fixes are backend-only. All are real correctness defects (not cosmetic).

| # | Bug | Root cause | Fix | File(s) |
|---|-----|-----------|-----|---------|
| A | `TableRun.RowsMigrated` **lost-update race** — migration progress undercounted (test showed 1000 rows instead of 8000) | Concurrent chunks of the same table each did a tracked read-modify-write (`TableRun.RowsMigrated += n`) in separate DbContext scopes, clobbering each other | Atomic DB-side increment via `ExecuteUpdateAsync(SetProperty(t => t.RowsMigrated, t => t.RowsMigrated + rowsWritten))` | `src/O2P.Worker/Worker.cs` |
| B | **Jobs never reached a terminal state** — nothing ever set `JobRun.Status` to Completed, so jobs showed "Running" forever | No job-completion logic existed | Added `CheckAndCompleteJobAsync` — when all table runs are terminal, atomically flips `Running -> Completed/CompletedWithErrors` and stamps `CompletedAt`. Leaves Paused/Cancelled jobs alone | `src/O2P.Worker/Worker.cs` |
| C | **Table stuck in "Loading" forever** if any chunk permanently Failed | Completion check treated only `Status == "Done"` as terminal, so a `Failed` chunk kept the table "in progress" indefinitely | Completion check now considers a table done when no chunk is `Pending`/`Running`; if any chunk `Failed`, the table is atomically marked `Failed` (instead of hanging) | `src/O2P.Worker/Worker.cs` |
| D | **Validation double-trigger race** — concurrent final-chunk completions could both run validation → duplicate `ValidationResult` rows / redundant work | Non-atomic status check-then-set | Atomic compare-and-set `Loading -> Validating` via `ExecuteUpdateAsync`; only the caller that wins the transition runs validation | `src/O2P.Worker/Worker.cs` |
| E | **Validation/preflight SQL didn't quote identifiers** — target tables are created with quoted, case-preserving names, so `SELECT COUNT(*) FROM public.CUSTOMERS` folds to lowercase and fails in real (non-mock) mode | Count/probe SQL built with raw `{schema}.{table}` interpolation | Quote identifiers using `SqlIdentifier.QuotePostgresQualified` / `SqlIdentifier.OracleQualified` | `src/O2P.Infrastructure.Postgres/Validation/PostgresCountExecutor.cs`, `src/O2P.Infrastructure.Oracle/Validation/OracleCountExecutor.cs`, `src/O2P.Infrastructure.Postgres/Validation/PostgresPreflightExecutor.cs` |

Bug A was directly caught by the automated test; B–E were found by code review and are covered by new assertions in the test suite.

---

## New files added (test tooling — safe to keep or remove)

- `scripts/e2e-api-test.mjs` — Node (no external deps) end-to-end API test. Exercises auth, users/roles + permission boundaries, connections (+ masking, duplicate-name conflict, mock test), applications + 4-slot binding, discovery (full + targeted), manifest generation, type-mapping assertions, job create/launch, a **full mock migration to completion**, validation, metrics, `_mgN` collision naming, and the LIVE confirmation phrase.

Nothing in `src/` was added — only edits to the files listed above.

---

## How to run the tests (reproduce the two clean passes)

Prereqs: .NET SDK (10.x installed here builds the net8.0 target fine), Node, Docker Desktop running.

```bash
# 1. Isolated metadata Postgres (NEVER use the production DB for this)
docker run -d --name o2p-test-pg \
  -e POSTGRES_DB=o2ptest -e POSTGRES_USER=o2ptest -e POSTGRES_PASSWORD=o2ptestpw \
  -p 7937:5432 postgres:15-alpine

# 2. Build
dotnet build src/O2P.slnx
( cd web && npm install && npm run build )

# 3. Run API (background) — env overrides point it at the isolated DB
#    ConnectionStrings__MetadataDb=Host=127.0.0.1;Port=7937;Database=o2ptest;Username=o2ptest;Password=o2ptestpw
#    ASPNETCORE_ENVIRONMENT=Development
#    JwtSettings__Secret=<32+ char secret>
#    BootstrapAdmin__Password=TestAdmin2026!Aa
#    then:  dotnet run --project src/O2P.Api --no-build --urls http://127.0.0.1:5050

# 4. Run Worker (background) — same ConnectionStrings__MetadataDb + ASPNETCORE_ENVIRONMENT=Development
#    dotnet run --project src/O2P.Worker --no-build

# 5. Run the end-to-end suite (expects a FRESH DB each run for a deterministic admin state)
O2P_API=http://127.0.0.1:5050 O2P_ADMIN_PW='TestAdmin2026!Aa' O2P_NEW_PW='TestAdmin2026!Bb' \
  node scripts/e2e-api-test.mjs
# Expected: PASS: 52   FAIL: 0

# Optional bonus: real-browser UI test (Playwright + Chrome). Build the UI to a TEMP dir
# (VITE_API_BASE_URL=http://127.0.0.1:5050/api/v1) so web/dist stays untouched, serve it on
# :3051, then run scripts/qa-ui.mjs with a FRESH admin (O2P_ADMIN_PASSWORD must be forced-change).
```

Note: the API's seeding rotates the bootstrap admin password on first login/change, so **re-run against a freshly recreated database** (drop & recreate the container) for a deterministic pass.

---

## Testing discipline for this project (per user)

After ANY change: run the FULL project test and require it to pass **twice in a row**. If anything fails, fix and re-test from the start. Keep this file and the project-context memory updated whenever the code changes. Goal: a perfect product.

---

## FR-5 chunk planner (Session 2 — implemented 2026-07-11)

Real intra-table chunking now replaces the single `MIN`/`MAX` stub. All strategies are strictly
**read-only** against the source (no DBMS_PARALLEL_EXECUTE, no DDL/DML, no chunk-metadata tables).

Files changed:
- `src/O2P.Domain/Entities/ChunkLog.cs` — added nullable `BoundColumn` (pk_range key) + `PartitionName`.
- `src/O2P.Infrastructure.Oracle/Reader/OracleChunkPlanner.cs` — full rewrite (see strategies below).
- `src/O2P.Infrastructure.Oracle/Reader/OracleDataReader.cs` — builds the read SQL per `chunk.Strategy`:
  ROWID BETWEEN for rowid/partition_rowid, `col > :startKey AND col <= :endKey` for pk_range,
  and a `PARTITION (...)` clause for partition-scoped chunks. `BindByName = true`.
- EF migration `20260710235037_M8_ChunkPlannerBounds` (+ snapshot) — two nullable `text` columns on
  `chunk_logs`. Additive/reversible; auto-applied at API startup via `MigrateAsync`.

Strategies (with graceful fallback at each step, so planning never hard-fails):
1. **Heap → ROWID ranges.** Reads the segment's extents from `DBA_EXTENTS`/`ALL_EXTENTS` and turns
   them into start/end ROWIDs via `DBMS_ROWID.ROWID_CREATE`, coalescing adjacent extents into
   ~`estimatedChunks` contiguous, non-overlapping ranges (ordered by relative_fno, block_id → matches
   ROWID sort order). This is the read-only equivalent of DBMS_PARALLEL_EXECUTE's ROWID chunking.
2. **Partitioned → partition-wise, sub-split by ROWID.** One lane per partition; each partition's
   extents are sub-chunked proportional to the whole table's size. Falls back to a whole-partition
   `partition` chunk when a partition's extents aren't visible (composite sub-partitioning / stale
   stats / denied) — still real inter-partition parallelism.
3. **IOT / fallback → numeric-PK range.** Single-column numeric PK only; boundaries come from
   `NTILE` quantiles (handles skew), yielding disjoint half-open ranges.
4. Fallbacks: extent views denied → PK range; no usable numeric PK → single whole-table chunk.

**Testability caveat (important):** the e2e suite exercises only *mock mode*, where the planner still
returns its 8 fixed mock chunks and the reader short-circuits before any real SQL. So the real
ROWID/partition/PK SQL above is **not** integration-tested here — it is build-verified and reviewed by
inspection only. Validating it needs a live Oracle (or an Oracle test container) with heap, partitioned,
and IOT tables. Known limitations to check there: composite sub-partitioned tables fall back to
whole-partition reads; `ALL_EXTENTS` only shows the caller's own segments (cross-schema needs
`DBA_EXTENTS` grants, else it degrades to PK range); NUMBER PKs wider than .NET `decimal` degrade to a
single chunk.

## Known remaining gaps (NOT yet addressed — candidates for the next session)

These are pre-existing feature gaps (not regressions), noted during review:

1. **Live-Oracle validation of the FR-5 planner** (see caveat above) — the one real follow-up for the chunking work.
2. **Post-load PK/index creation is not wired up.** `PostgresDdlGenerator.GenerateConstraintsAndIndexesDdl` exists but is never called, so PKs/indexes/`ANALYZE` are not created on the target after load (FR-4 step 6).
3. **LOB streaming lane, deep validation checks (null/min/max/sum/hash), dry-run mode** — partial vs. the spec in `Context.md`.
4. `OracleCountExecutor` appends a manifest `WHERE` clause without the read-only validation that `OracleDataReader` applies — low risk (operator-supplied config) but worth unifying.

---

## Environment left running (tear down when convenient)

- Docker container `o2p-test-pg` (throwaway metadata Postgres on :7937). Remove with `docker rm -f o2p-test-pg`.
- Background `dotnet` API (:5050) and a temp static UI server (:3051) may still be running from the session; kill stray `dotnet.exe` / `node` as needed.
- Temp UI build at `%TEMP%\o2p-ui-dist` (safe to delete). Repo `web/dist` was NOT modified by the UI test.
