# ROLE
You are a senior .NET data-platform engineer designing a production-grade, high-throughput
Oracle → PostgreSQL bulk data migration application.

# PHASE GATE — CRITICAL
Do NOT write application code in this phase. Your only deliverable now is a detailed
technical plan (specified at the bottom). I will review it. Code generation begins only
after I reply exactly: "APPROVED — START DEVELOPMENT".
If any requirement is ambiguous, ask clarifying questions before finalizing the plan.

# PROJECT CONTEXT
- Purpose: migrate table data (schema + rows) from Oracle to PostgreSQL.
- Scale: individual tables up to 300 GB; total source database up to 4 TB; hundreds of tables per run.
- Source Oracle databases are shared/production systems — read-impact must be controllable.

# PROPOSED TECH STACK (deviate only with written justification)
- Backend: .NET 8 LTS, Clean Architecture (REST API + background Worker Service)
- Data path: raw ADO.NET — Oracle.ManagedDataAccess.Core (reads), Npgsql (writes). No ORM in the hot path.
- PG write mechanism: COPY binary protocol (NpgsqlBinaryImporter) as the primary path; batched multi-row INSERT as fallback only.
- Concurrency: System.Threading.Channels producer→transform→consumer pipelines with bounded backpressure.
- Metadata store: PostgreSQL (EF Core acceptable here only).
- UI: React (Vite) or Blazor Server; real-time updates via SignalR.
- Logging: Serilog structured logging. Containerized (Docker), Kubernetes-ready.

# FUNCTIONAL REQUIREMENTS

## FR-1 — Connection Management
- CRUD page for connection profiles (Oracle and PostgreSQL).
- Fields: host, port, service/SID or dbname, credentials, options (SSL, pool size, fetch size).
- "Test Connection" button: returns latency, server version, and privilege check
  (SELECT + catalog access on Oracle; CREATE/INSERT on PG).
- Credentials encrypted at rest (AES-256 / ASP.NET Data Protection); masked in UI and logs.

## FR-2 — Application Setup
- Entity "Application" groups all migrations for one system.
- Each Application binds four connection slots: Oracle-Test, Oracle-Live, PG-Test, PG-Live.
- A migration run selects a source/target environment pair (e.g., Oracle-Live → PG-Test).
- Runs targeting a Live PG environment require a typed confirmation phrase.
- Per-application defaults: parallelism limits, chunk size, type-mapping overrides, identifier naming policy.

## FR-3 — Table Selection (Migration Manifest)
- Auto-discover tables from Oracle (ALL_TABLES) with row counts and segment size (ALL/DBA_SEGMENTS).
- Selection UX: multi-select grid, paste list, wildcard/regex include-exclude, CSV upload.
- Optional per-table: WHERE filter, column exclusions.
- Persist selections as reusable, versioned "Manifests".

## FR-4 — Per-Table Workflow
1. Pre-flight: read Oracle column metadata (types, precision/scale, nullability, PK).
2. Generate PostgreSQL DDL via the type-mapping engine (FR-6).
3. Name-collision rule: if target table exists, create `<table>_mg1`, `_mg2`, `_mg3`...
   Suffix allocation must be atomic via the metadata store (no race under parallelism).
   Also offer configurable alternatives: truncate-and-load | fail | skip.
4. Create table WITHOUT indexes/constraints.
5. Bulk load data.
6. Post-load (configurable): create PK/indexes, ANALYZE, run validation (FR-8).
7. Record final metrics and status.

## FR-5 — Performance & Parallelism (highest priority)
- Two-level parallelism: N tables concurrently × M parallel chunk-readers per large table;
  both configurable with a global connection/session cap.
- Intra-table chunking: ROWID-range splitting (DBMS_PARALLEL_EXECUTE-style) as primary strategy;
  numeric-PK range / NTILE fallback; partition-wise reads for partitioned tables.
- Oracle read tuning: byte-based FetchSize, InitialLOBFetchSize, forward-only streaming —
  never materialize a full table in memory. Provide memory-budget math for a 300 GB table.
- LOB lane: stream BLOB/CLOB above a size threshold in chunks; route LOB-heavy tables to a
  dedicated worker lane so they don't starve narrow tables.
- PG load tuning options: synchronous_commit=off per session, maintenance_work_mem for index
  builds, max_wal_size guidance, optional UNLOGGED-load → SET LOGGED strategy (document trade-offs).
- Throttling: max Oracle sessions, rows/sec or MB/s cap, optional off-peak scheduling window
  (protect production source).
- State a target sustained throughput (e.g., ≥100–200 MB/s per node) with assumptions.

## FR-6 — Type-Mapping Engine (defaults; per-app and per-column overrides)
- NUMBER(p,0): p≤4→smallint, p≤9→integer, p≤18→bigint, else numeric(p)
- NUMBER(p,s)→numeric(p,s); NUMBER (no precision)→numeric
- BINARY_FLOAT→real; BINARY_DOUBLE→double precision; FLOAT(b)→double precision
- VARCHAR2/NVARCHAR2→varchar(n)/text (resolve BYTE vs CHAR length semantics); CHAR/NCHAR→char(n)
- DATE→timestamp(0)  ← Oracle DATE carries a time component; never default to pg `date`
- TIMESTAMP→timestamp; TIMESTAMP WITH (LOCAL) TIME ZONE→timestamptz (define session TZ policy)
- CLOB/NCLOB→text; BLOB/RAW/LONG RAW→bytea; LONG→text
- ROWID/UROWID→varchar (configurable/skip); XMLTYPE→xml; INTERVAL→interval
Edge-case policies (mandatory in plan):
- NUL (0x00) bytes inside VARCHAR2/CLOB — PostgreSQL rejects them; define strip/replace/fail policy.
- Oracle '' ≡ NULL semantics — define target behavior.
- NaN / ±Infinity in BINARY_DOUBLE columns.
- Charset AL32UTF8 → UTF8; invalid byte sequence handling.
- Identifier policy: lowercase snake_case | quoted-preserve; reserved-word escaping;
  63-byte PG name truncation with dedupe.

## FR-7 — Reliability, Resume, Error Handling
- Chunk-level state machine persisted in metadata DB (Pending/Running/Done/Failed).
- Idempotent resume: restart processes only failed/pending chunks — no duplicate rows.
- Retry with exponential backoff for transient errors.
- Per-table error policy: fail-fast | skip-bad-rows to a rejected-rows table (row payload +
  reason) with a configurable abort threshold.
- Pause / resume / cancel at job and table level.

## FR-8 — Validation
- Mandatory: source vs target row-count match (honoring WHERE filters).
- Optional deep checks: per-column null counts, min/max, numeric column sums, sampled hash comparison.
- Exportable validation report (CSV/JSON).

## FR-9 — Progress & Observability
- Real-time dashboard (SignalR):
  - Job: tables done/total, GB moved / estimated total, aggregate MB/s, ETA.
  - Table: % complete (rows + bytes), rows/sec, chunk map, status, error count.
- Run history with duration and throughput charts.
- Structured Serilog logs, downloadable per run; audit trail (user, env pair, timestamp).

## FR-10 — Safety & Ops
- Dry-run mode: emit full DDL + mapping report + size estimates, move zero rows.
- Pre-flight validator: privileges, PG free disk vs estimated size, version compatibility,
  network throughput probe.
- Manifest + mapping config export/import (JSON).

# OUT OF SCOPE (v1 — confirm in plan)
CDC/ongoing sync, PL/SQL conversion, views/triggers/procedures/grants, FK dependency ordering.
List these as v2 candidates.

# REQUIRED PLAN DELIVERABLE (single document, in this order)
1. Architecture: component + deployment diagrams, scaling model.
2. Metadata DB schema (applications, connections, manifests, jobs, table_runs, chunks, errors, metrics).
3. Data-path pipeline design: threading model, channel bounds, memory budget for a 300 GB table.
4. Chunking algorithm detail, including ROWID-split SQL.
5. Complete default type-mapping table + all edge-case policies.
6. DDL generation rules + atomic `_mgN` collision algorithm.
7. API endpoint list + UI screen inventory with brief wireframe descriptions.
8. Failure/resume state-machine diagram.
9. Performance test plan + tuning parameter matrix (FetchSize, COPY buffer, DOP levels).
10. Risk register: top 10 risks with mitigations.
11. Phased milestone plan with acceptance criteria per phase.
12. Open questions for me.

Remember: NO CODE until "APPROVED — START DEVELOPMENT".