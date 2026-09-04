# O2P Production Hardening and Revamp Plan

## Summary

- Rebuild the current prototype into a production v1 Oracle-to-PostgreSQL migration platform for 4-5 TB, multi-project, test/live environments.
- Lock production baseline to .NET 8 LTS, React/Vite, PostgreSQL metadata DB, Oracle Managed Data Access, and Npgsql COPY.
- Strategy: controlled revamp. Preserve useful domain/API/UI structure, replace unsafe security, worker, chunking, COPY, and metadata-state logic.
- Default posture: strict live gates, multi-node-safe worker design, rowcount plus checksum validation, selectable item-level migration.

## Key Architecture Decisions

- Runtime: downgrade all project targets and package versions from net10.0 to .NET 8 LTS-compatible versions.
- Deployment: support Docker Compose for dev/single VM and Kubernetes for production.
- Scaling: worker must be safe with one or many replicas via atomic chunk leasing, lease renewal, target-side chunk fence, and global source throttling.
- Frontend: consolidate to one React app. Recommended: keep root `web` because Docker Compose already uses it, then port missing job/settings screens into it.
- Migration unit: chunk-level transactional execution. Each chunk is independently claimable, restartable, measurable, and validated.
- Hot path: no ORM. Oracle ADO.NET streaming reader to bounded channels/batches to Npgsql binary COPY writer.
- Metadata path: EF Core allowed only for metadata, identity, job state, manifests, metrics, validation, and audit.

## Critical Bug Fixes

### Credential Storage

- Replace raw `Encoding.UTF8.GetBytes(password)` with ASP.NET Data Protection encryption.
- Create `ISecretProtector` service with `Protect(string)` and `Unprotect(byte[])`.
- Mask secrets in every DTO and log path.
- Add migration/backfill path for existing plaintext-like `SecretCiphertext`: detect old rows, decrypt as legacy UTF-8 once, re-protect, audit conversion.

### Authorization

- Re-enable `[Authorize]` globally.
- Implement roles: `Admin`, `Operator`, `Viewer`.
- Enforce role permissions:
  - Admin: users, settings, connections, live target configuration.
  - Operator: manifests, discovery, jobs, commands.
  - Viewer: dashboards, logs, validation reports only.
- Remove mocked UI authentication and wire JWT storage/refresh/logout.

### SQL and Identifier Safety

- Add central PostgreSQL/Oracle identifier quoting utilities.
- Quote schema/table/column/index identifiers everywhere.
- Parameterize values; never interpolate user-supplied filters except through validated manifest filter syntax.
- Validate manifest `WHERE` clauses with restricted read-only grammar or DBA-approved raw mode.

### Target Name Allocation

- Replace current non-persisted `_mgN` allocator with `target_name_allocations`.
- Enforce unique `(target_pg_connection_id, schema, base_name, suffix_n)`.
- Allocate inside serializable transaction or single `INSERT ... ON CONFLICT` loop.
- Track allocation status: `reserved`, `created`, `abandoned`.

### Chunk Claiming

- Replace `FirstOrDefaultAsync(status='Pending')` with atomic claim:
  - `SELECT ... FOR UPDATE SKIP LOCKED`
  - set `Status=Running`, `WorkerId`, `LeaseExpiresAt`, `AttemptCount++`
  - commit before executing the chunk.
- Add lease renewal heartbeat.
- Add sweeper to return expired `Running` chunks to `Pending` when no target fence exists.

### COPY Correctness

- Generate explicit `COPY schema.table (col1, col2, ...) FROM STDIN (FORMAT BINARY)`.
- Use manifest column order, not `SELECT *`.
- Add per-column Oracle-to-Postgres converters.
- Support binary COPY primary path, text COPY fallback per table when required.
- Handle `NULL`, Oracle empty-string-as-null, NUL bytes, invalid UTF-8, NaN/Infinity, high-precision `NUMBER`.

### Resume and Idempotency

- Add target-side `_o2p_chunk_log` table per target schema or per job.
- Insert `(job_id, table_run_id, chunk_seq)` at start of the same target transaction as COPY.
- If marker exists on retry, reconcile metadata as `Done`.
- Prevent duplicate rows when worker, API, metadata DB, or target PostgreSQL fails mid-run.

### Background Execution

- Remove API fire-and-forget `Task.Run` for job preparation.
- API only creates commands/state.
- Worker owns DDL generation, target creation, chunk planning, load, post-load, validation.
- Use durable `job_commands` plus `LISTEN/NOTIFY` with polling fallback.

## Production v1 Features

### Project and Application Model

- Application groups migration work for one system.
- Each app binds four slots: `Oracle-Test`, `Oracle-Live`, `PG-Test`, `PG-Live`.
- Per-app defaults:
  - max tables in parallel
  - max chunk workers per table
  - global Oracle sessions
  - chunk target size
  - fetch size
  - throttle MB/s and rows/s
  - off-peak window
  - identifier policy
  - collision policy
  - validation depth
  - reject-row policy

### Connection Management

- CRUD for Oracle/PostgreSQL connections.
- Test connection returns latency, version, privilege checks, catalog access, DDL/DML ability.
- TLS/SSL options, pool settings, fetch size, service/SID mode.
- Credentials encrypted at rest and never returned to UI.

### Discovery and Item Selection

- Discover Oracle owners, tables, columns, PKs, indexes, row estimates, segment sizes, partitions, LOBs.
- Selection modes:
  - manual multi-select
  - owner/table search
  - wildcard/regex include/exclude
  - paste list
  - CSV upload
  - per-table `WHERE`
  - column exclusions
- Persist reusable versioned manifests.
- Manifest snapshot freezes selected metadata for repeatable jobs.

### Environment-Aware Job Launch

- Job selects source slot and target slot, for example `Oracle-Live -> PG-Test`.
- PG-Live target requires successful preflight, dry-run availability, typed confirmation phrase, Operator/Admin authorization, and audit event.
- Live Oracle source requires visible throttle/session limits before launch.

### Preflight

- Oracle privilege validation: table read, catalog views, rowid/chunk metadata access.
- PostgreSQL validation: schema create/usage, table create/drop, COPY probe, disk estimate.
- Version checks: Oracle 19c+, supported PostgreSQL version.
- Network throughput probe.
- Estimated target size and WAL/disk warning.
- Reject launch if mandatory checks fail unless Admin override is explicitly enabled later.

### Migration Engine

- Table workflow:
  - resolve manifest
  - allocate target name
  - generate DDL
  - create table without indexes/FKs/triggers
  - plan chunks
  - stream chunk data
  - post-load PK/indexes
  - analyze
  - validate
  - finalize metrics/report
- Collision policies:
  - default `_mgN`
  - fail
  - skip
  - truncate-and-load only if explicitly enabled and target confirmed.
- Chunk strategies:
  - ROWID range primary for heap tables.
  - partition-wise ROWID for partitioned tables.
  - numeric/date PK range fallback.
  - single chunk fallback for small or unsupported tables.
- LOB-heavy tables routed to dedicated lower-concurrency lane.

### Type Mapping

- Implement full default mapping:
  - `NUMBER(p,0)` to `smallint`, `int`, `bigint`, or `numeric`
  - `NUMBER(p,s)` to `numeric(p,s)`
  - unbounded `NUMBER` to `numeric`
  - `DATE` to `timestamp(0)`
  - `TIMESTAMP WITH TIME ZONE` to `timestamptz`
  - `CLOB/NCLOB` to `text`
  - `BLOB/RAW/LONG RAW` to `bytea`
  - `VARCHAR2/NVARCHAR2` to `varchar(n)` or `text` based on length policy
  - `ROWID/UROWID` to configurable include-as-varchar or skip
- Add per-application/table/column override rules.
- Generate mapping report before execution.

### Validation

- Default: rowcount plus checksum package.
- Checks:
  - source vs target rowcount
  - per-column null counts
  - numeric sums where safe
  - min/max for comparable columns
  - sampled hash comparison
- Export CSV/JSON validation reports.
- Validation respects manifest filters and excluded columns.

### Observability

- SignalR progress hub:
  - job progress
  - table progress
  - chunk map
  - throughput
  - ETA
  - error/reject count
- Metrics every 2 seconds during active jobs.
- Structured Serilog logs with run ID, table run ID, chunk ID.
- Downloadable job logs.
- Audit trail for connection changes, manifest changes, job launch, pause/resume/cancel, live confirmations.

### Operations

- Pause/resume/cancel at job and table level.
- Retry failed chunks/table/job.
- Live throttle update while running.
- Off-peak scheduling.
- Retention settings for metrics/logs/reject rows.
- DBA grants document updated to exact required Oracle/PostgreSQL privileges.

## Metadata and API Changes

- Add or complete metadata tables:
  - `target_name_allocations`
  - `row_rejects`
  - `run_events`
  - `run_logs`
  - `type_mapping_rules`
  - target chunk fence table DDL
- Normalize statuses:
  - Job: `Draft`, `Queued`, `PreflightFailed`, `Running`, `Paused`, `Cancelling`, `Cancelled`, `Completed`, `CompletedWithErrors`, `Failed`
  - TableRun: `Pending`, `Creating`, `Planning`, `Loading`, `PostLoad`, `Validating`, `Completed`, `CompletedWithErrors`, `Failed`, `Skipped`
  - Chunk: `Pending`, `Running`, `Done`, `Failed`, `Abandoned`
- Public API shape:
  - `/api/v1/auth/*`
  - `/api/v1/connections/*`
  - `/api/v1/applications/*`
  - `/api/v1/discovery/*`
  - `/api/v1/manifests/*`
  - `/api/v1/jobs/*`
  - `/api/v1/jobs/{id}/commands`
  - `/api/v1/jobs/{id}/metrics`
  - `/api/v1/jobs/{id}/validation`
  - `/api/v1/jobs/{id}/logs`
  - `/api/v1/settings/*`
  - `/hubs/progress`

## UI Revamp

- Single SPA with these screens:
  - Login/session.
  - Dashboard: active jobs, throughput, alerts.
  - Connections: CRUD, test, masked credentials.
  - Applications: app defaults and environment slot binding.
  - Discovery: owner/table search, refresh cache.
  - Manifest Builder: selection, filters, exclusions, type overrides, dry-run report.
  - New Job Wizard: app, manifest version, source/target env, schema, safety checks, live confirmation.
  - Job Detail: table list, chunk map, metrics, logs, commands.
  - Validation Report: pass/fail, export.
  - Settings/Admin: roles, retention, global defaults.
- UI must clearly separate Test vs Live resources and show destructive/high-risk actions with server-enforced confirmation.

## Test Plan

### Unit Tests

- Type mapping matrix.
- Identifier quoting/truncation/dedupe.
- Secret encryption/decryption.
- Target name allocation races.
- Job/table/chunk state transitions.
- Manifest selection rules.

### Integration Tests

- Metadata migrations on clean PostgreSQL.
- Connection test for Oracle/PostgreSQL.
- Preflight success/failure paths.
- Atomic chunk claim with parallel workers.
- Target fence duplicate prevention.
- COPY with explicit column list.
- Pause/resume/cancel semantics.
- Validation reports.

### Failure Tests

- Kill worker mid-chunk.
- Kill API during running job.
- Expire chunk lease.
- Target PostgreSQL restart during COPY.
- Metadata DB temporary outage.
- Oracle transient read failure.
- Retry after partial failure with zero duplicates.

### Performance Tests

- 10 GB table baseline.
- 100 GB table sustained run.
- Multi-table mixed narrow/LOB workload.
- Concurrent jobs across multiple applications.
- Throttled live-source simulation.
- Target throughput goal: tune for 100-200+ MB/s per worker node depending on Oracle, network, and storage.

### Security Tests

- Unauthenticated API blocked.
- Role matrix enforced.
- Secrets never appear in API responses/logs.
- Raw SQL injection attempts blocked in identifiers/filter inputs.
- Live launch cannot bypass confirmation.

## Phased Delivery

### Phase 0: Stabilize Foundation

- Move to .NET 8 LTS.
- Fix solution/build/docker consistency.
- Consolidate frontend.
- Add CI build/test pipeline.
- Replace placeholder README with real setup/run docs.

### Phase 1: Security and Metadata Correctness

- Enforce auth/roles.
- Encrypt credentials.
- Add audit trail.
- Add durable command model.
- Add target name allocation table.
- Add metadata migrations.

### Phase 2: Production Migration Engine

- Implement worker-owned job orchestration.
- Atomic chunk planning/claiming/leases.
- Target chunk fence.
- Correct DDL generator.
- Correct Oracle reader with explicit columns and filters.
- Correct Npgsql COPY writer with type converters.

### Phase 3: Selection, Mapping, and Validation

- Full discovery cache.
- Versioned manifests.
- Type override system.
- Dry-run mapping/DDL report.
- Rowcount plus checksum validation.
- Reject-row salvage path.

### Phase 4: Operations UI and Observability

- Dashboard, job detail, chunk map, metrics.
- Pause/resume/cancel/retry.
- Logs and validation export.
- Settings and retention.

### Phase 5: Scale and Production Readiness

- Multi-node worker test.
- Kubernetes manifests hardened.
- Load/performance test suite.
- DBA grants/runbook finalized.
- Production acceptance run against real Oracle/PostgreSQL.

## v2 Roadmap

- CDC/ongoing sync.
- PL/SQL/package/procedure conversion.
- View/trigger/grant migration.
- FK dependency ordering/recreation.
- Advanced Oracle object types.
- Scheduling calendar and approval workflow.
- Multi-tenant SaaS mode.
- Object-level differential re-run.
- End-to-end checksum for every row where source load allows.

## Assumptions

- Production baseline is .NET 8 LTS.
- Controlled revamp is allowed.
- Multi-node-safe worker design is required in v1.
- Strict live gates are mandatory.
- Default validation is rowcount plus checksums, not full deep compare.
- v1 focuses on schema/table data migration, not CDC or PL/SQL conversion.
- PostgreSQL metadata DB remains the control plane.
- Oracle production read impact must be controlled by sessions, throttle, chunk size, and schedule.
