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

## 4. Preflight Validation

Always run the preflight check before starting a massive migration:
`POST /api/v1/jobs/{id}/preflight`
This will automatically verify network connectivity, PostgreSQL version compatibility, and DDL permissions.
