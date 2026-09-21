# O2P Operations Runbook

This guide covers operational procedures for the O2P migration engine.

## 1. Pausing and Resuming Jobs

If a migration is placing too much load on the source DB, or you need to perform maintenance:
1. Hit the `/api/v1/jobs/{id}/command` endpoint with `{"command": "pause"}`.
2. The GlobalGovernor will stop dispatching new chunks. Active chunks will finish.
3. To resume, send `{"command": "resume"}`. The worker will pick up exactly where it left off.

## 2. Global Governor Tuning

The GlobalGovernor controls cluster-wide concurrency.
Currently, it limits max Oracle sessions to 16 and chunk workers to 16 per node.
- To increase concurrency, modify `GlobalGovernor` registration in `Program.cs`.
- Ensure your `DB_MAX_CONNECTIONS` parameter on the Oracle database supports `maxOracleSessions * num_workers`.

## 3. Monitoring K8s Deployments

Use standard K8s tooling:
```bash
kubectl logs -l app=o2p-worker -f
kubectl get pods
```
Metrics are emitted to the `MetricSamples` table every 2 seconds, providing:
- Rows Migrated Per Second
- Active Chunk Workers

## 4. Worker Health

The Worker is the only process that copies data; while it is down, new runs stay `Queued`.
- Each Worker writes a heartbeat row to `o2p.worker_heartbeats` every 10 seconds and deletes it on a clean stop.
- `GET /api/v1/workers/status` (any signed-in user) reports `running`, `count`, `multiple` and each Worker's host, process id and last-seen time. A Worker not seen for 45 seconds counts as gone.
- The Runs page shows the same status as a chip and a banner. `count` above 1 is a fault: run exactly one Worker.
- A `Queued` run can be cancelled with no Worker running; the API applies the cancel immediately.
- A run whose table selection has nothing ticked is refused at start (HTTP 400). Any such run created earlier is marked `Failed` by the Worker with the event `job.failed_no_tables`.
- Starting the Worker: `dotnet run --project src/O2P.Worker` for development, `scripts/run-worker.ps1` on a server.

## 5. Change Tracking ("Copy changes")

Copies what changed in Oracle since a table's last copy, reading the redo log with LogMiner. Details and the DBA
setup: User Guide, *Keep the destination up to date*.
- State lives in `o2p.tracked_tables`, one row per destination table, deliberately with no foreign keys (saving a
  table selection deletes and re-adds its rows). `LastScn` is the point everything before which is already in the
  destination; it only moves when a table fully succeeds.
- Change runs are `job_runs` with `Kind = 'changes'`. The Worker claims them in their own loop
  (`FOR UPDATE SKIP LOCKED`, a 5-minute lease renewed every minute). If a Worker dies, the lease lapses and
  another Worker restarts the run from the beginning — safe, because applying is idempotent.
- A change run never goes through bulk preparation (which empties tables): retry is refused, and the prepare,
  retry and cancel paths all check `Kind`. A tracked table claimed by a change run (`ActiveJobRunId`) blocks a bulk
  copy of the same table, and a bulk copy marks the table `needs_bulk_copy` before emptying it.
- **Archived-log retention is the operational limit.** A tracker whose history has been purged fails with
  "no longer on the source server" and needs a bulk copy. Keep archived logs longer than the longest gap between
  presses; the readiness check reports how far back the history reaches.
- Long-open or in-doubt Oracle transactions hold every tracker's resume point back (by design: their rows are
  re-read once they commit). The UI shows who owns them.
- Supported sources: non-multitenant Oracle, and 21c+ multitenant mining in the PDB. 19c multitenant needs a
  root-container connection, not yet supported.
- Opt-in integration tests against a real Oracle and PostgreSQL: `tests/O2P.Integration.Tests` (not part of the
  solution; see its project file). End to end: `scripts/verify-change-tracking.mjs` and
  `scripts/verify-change-tracking-ui.mjs`.

## 6. Preflight Validation

Always run the preflight check before starting a massive migration:
`POST /api/v1/jobs/{id}/preflight`
This will automatically verify network connectivity, PostgreSQL version compatibility, and DDL permissions.
