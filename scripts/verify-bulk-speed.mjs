// End-to-end check of the bulk-copy speed work against a REAL Oracle source and a REAL PostgreSQL
// destination, through the API and a running Worker:
//   - tables are cut into batches by size, not a fixed 16;
//   - bytes are counted, MB/s is real, each batch logs where its time went;
//   - LOBs above and below the inline fetch size arrive byte for byte;
//   - a batch whose destination connection is killed is retried by itself, promptly, and a batch that
//     stops moving is stopped by the stall watchdog and retried - and no row is written twice.
//
// The Worker must run with small batches and a 1-minute stall limit so this finishes in minutes:
//   Copying__RowsPerBatch=1000 Copying__RowsPerLobBatch=100 Copying__StallMinutes=1
//
// SAFETY: creates and drops tables in the Oracle account and PostgreSQL schemas it is given. Throwaway
// databases only; it refuses the usual ports.
//
// Usage: O2P_API=http://127.0.0.1:5050 O2P_ADMIN_PW=... [O2P_WORKER_LOG=path] node scripts/verify-bulk-speed.mjs
//   Oracle defaults: container o2p-oracle-test, o2ptrack/Track2026, 127.0.0.1:15210/FREEPDB1
//   PostgreSQL defaults: container dataflow-metadata-pg-1, 127.0.0.1:7936/nonoraclemigrationdb (also the metadata DB)
import { execFileSync, spawn } from 'node:child_process';
import { readFileSync, existsSync } from 'node:fs';

const API = process.env.O2P_API;
const ADMIN_PW = process.env.O2P_ADMIN_PW;
const WORKER_LOG = process.env.O2P_WORKER_LOG;
const ORA_CONTAINER = process.env.O2P_ORA_CONTAINER || 'o2p-oracle-test';
const ORA_USER = process.env.O2P_ORA_USER || 'o2ptrack';
const ORA_PASSWORD = process.env.O2P_ORA_PASSWORD || 'Track2026';
const ORA_HOST = process.env.O2P_ORA_HOST || '127.0.0.1';
const ORA_PORT = Number(process.env.O2P_ORA_PORT || 15210);
const ORA_SERVICE = process.env.O2P_ORA_SERVICE || 'FREEPDB1';
const PG_CONTAINER = process.env.O2P_PG_CONTAINER || 'dataflow-metadata-pg-1';
const PG_USER = process.env.O2P_PG_USER || 'nonoraclemigrationdb';
const PG_DB = process.env.O2P_PG_DB || 'nonoraclemigrationdb';
const PG_HOST = process.env.O2P_PG_HOST || '127.0.0.1';
const PG_PORT = Number(process.env.O2P_PG_PORT || 7936);
const PG_PASSWORD = process.env.O2P_PG_PASSWORD || 'change-me-local-only';

if (!API || !ADMIN_PW) {
  console.log('Set O2P_API and O2P_ADMIN_PW - this script will not guess a target.');
  process.exit(2);
}
if (['5000', '5151', '3051', '3052'].includes(new URL(API).port) || PG_PORT === 5432) {
  console.log('Refusing to run against a normal dev/deploy port or port 5432.');
  process.exit(2);
}

let failures = 0;
const ok = (m) => console.log('  ✓ ' + m);
const bad = (m) => { failures++; console.log('  ✗ ' + m); };
const check = (c, m) => (c ? ok(m) : bad(m));
const section = (m) => console.log('\n[' + m + ']');
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

let token = null;
async function api(method, url, body) {
  const headers = { 'Content-Type': 'application/json' };
  if (token) headers.Authorization = `Bearer ${token}`;
  const res = await fetch(`${API}${url}`, { method, headers, body: body === undefined ? undefined : JSON.stringify(body) });
  const text = await res.text();
  let data = null;
  if (text) { try { data = JSON.parse(text); } catch { data = text; } }
  return { status: res.status, data };
}

function ora(sql) {
  const script = `SET HEADING OFF FEEDBACK OFF PAGESIZE 0 LINESIZE 32767 TRIMSPOOL ON\nWHENEVER SQLERROR EXIT FAILURE\n${sql}\nEXIT\n`;
  return execFileSync('docker', ['exec', '-i', ORA_CONTAINER, 'sqlplus', '-s', `${ORA_USER}/${ORA_PASSWORD}@localhost:1521/${ORA_SERVICE}`],
    { input: script, encoding: 'utf8' }).trim();
}
function pg(sql) {
  return execFileSync('docker', ['exec', '-i', PG_CONTAINER, 'psql', '-U', PG_USER, '-d', PG_DB, '-A', '-t', '-c', sql], { encoding: 'utf8' }).trim();
}
/** A PostgreSQL session that runs the SQL (e.g. takes a lock and sleeps) in the background. */
function pgBackground(sql) {
  const child = spawn('docker', ['exec', '-i', PG_CONTAINER, 'psql', '-U', PG_USER, '-d', PG_DB, '-q'], { stdio: ['pipe', 'ignore', 'ignore'] });
  child.stdin.end(sql);
  return new Promise((resolve) => child.on('exit', resolve));
}

async function waitForRun(id, label, seconds = 300) {
  for (let i = 0; i < seconds / 2; i++) {
    await sleep(2000);
    const r = await api('GET', `/api/v1/jobs/${id}`);
    if (['Completed', 'CompletedWithErrors', 'Failed', 'Cancelled'].includes(r.data?.status)) return r.data;
  }
  bad(`${label}: finished in time`);
  return null;
}

const stamp = Date.now().toString().slice(-6);
let pgId;

/** A migration over the given Oracle tables, and a bulk copy of it into the schema, launched. */
async function launchCopy(label, tables, schema) {
  // Its own source connection, so its scan - and the migration generated from it - holds only these tables.
  let r = await api('POST', '/api/v1/connections', { name: `bs-ora-${label}-${stamp}`, kind: 'oracle', host: ORA_HOST, port: ORA_PORT, serviceOrDb: ORA_SERVICE, username: ORA_USER, password: ORA_PASSWORD });
  const oraId = r.data?.id;
  r = await api('POST', '/api/v1/applications', { name: `Bulk speed ${label} ${stamp}`, description: 'temporary' });
  const appId = r.data?.id;
  await api('PUT', `/api/v1/applications/${appId}`, {
    id: appId, name: `Bulk speed ${label} ${stamp}`, description: 'temporary',
    connections: [
      { applicationId: appId, slot: 'oracle_test', connectionId: oraId },
      { applicationId: appId, slot: 'pg_test', connectionId: pgId },
    ],
  });
  r = await api('POST', `/api/v1/connections/${oraId}/discovery/refresh?owner=${ORA_USER.toUpperCase()}`, { tableNames: tables });
  check(r.status === 200, `${label}: scanned ${tables.join(', ')} (${r.status})`);
  r = await api('POST', `/api/v1/applications/${appId}/manifests/generate?connectionId=${oraId}&owner=${ORA_USER.toUpperCase()}&version=1.0`, {});
  const manifestId = r.data?.id;
  const inManifest = (r.data?.tables ?? []).map((t) => `${t.tableName} (~${t.estRows ?? '?'} rows)`);
  r = await api('POST', '/api/v1/jobs', { applicationId: appId, manifestId, sourceSlot: 'oracle_test', targetSlot: 'pg_test', targetSchema: schema });
  const jobId = r.data?.id;
  r = await api('POST', `/api/v1/jobs/${jobId}/launch`, {});
  check(r.status === 200, `${label}: bulk copy launched (${r.status} ${JSON.stringify(r.data?.errors ?? '')}; ${inManifest.join(', ')})`);
  return jobId;
}

const chunks = (jobId, table) => pg(`
  SELECT c."Status" || '|' || c."AttemptCount" || '|' || c."RowsMigrated" || '|' || c."BytesMigrated" || '|' || coalesce(c."RetryAfter"::text, '') || '|' || regexp_replace(coalesce(c."ErrorMessage", ''), '\\s+', ' ', 'g')
  FROM o2p.chunk_logs c JOIN o2p.table_runs t ON t."Id" = c."TableRunId" JOIN o2p.manifest_tables m ON m."Id" = t."ManifestTableId"
  WHERE t."JobRunId" = ${jobId} AND m."TableName" = '${table}' ORDER BY c."ChunkIndex";`)
  .split('\n').filter(Boolean).map((line) => {
    const [status, attempts, rows, bytes, retryAfter, ...error] = line.split('|');
    return { status, attempts: Number(attempts), rows: Number(rows), bytes: Number(bytes), retryAfter, error: error.join('|') };
  });

console.log('\n=== BULK COPY SPEED (real Oracle -> real PostgreSQL) ===');

// ---- seed ---------------------------------------------------------------------------------------
section('seed');
let r = await api('POST', '/api/v1/auth/login', { username: 'admin', password: ADMIN_PW });
if (r.status !== 200) { console.log(`login failed: ${r.status} ${JSON.stringify(r.data)}`); process.exit(1); }
token = r.data.token;
ok('logged in once');

ora(`
BEGIN
  FOR t IN (SELECT table_name FROM user_tables WHERE table_name IN ('BS_PLAIN', 'BS_LOB', 'BS_RETRY')) LOOP
    EXECUTE IMMEDIATE 'DROP TABLE ' || t.table_name || ' PURGE';
  END LOOP;
END;
/
CREATE TABLE bs_plain (id NUMBER(10) PRIMARY KEY, name VARCHAR2(100), amount NUMBER(12,2), created DATE);
INSERT INTO bs_plain SELECT LEVEL, 'row ' || LEVEL, LEVEL * 1.25, DATE '2026-01-01' + MOD(LEVEL, 365) FROM dual CONNECT BY LEVEL <= 10000;
CREATE TABLE bs_lob (id NUMBER(10) PRIMARY KEY, label VARCHAR2(40), doc BLOB);
CREATE TABLE bs_retry (id NUMBER(10) PRIMARY KEY, v VARCHAR2(50));
INSERT INTO bs_retry SELECT LEVEL, 'value ' || LEVEL FROM dual CONNECT BY LEVEL <= 3000;
COMMIT;
-- 300 documents of 200 KB (arrive with their row) and every 30th one 600 KB (fetched separately), each
-- starting with its id so a swapped or truncated document shows.
DECLARE
  b BLOB;
  piece RAW(2000);
BEGIN
  FOR i IN 1 .. 300 LOOP
    INSERT INTO bs_lob VALUES (i, 'doc ' || i, EMPTY_BLOB()) RETURNING doc INTO b;
    piece := UTL_RAW.CAST_TO_RAW(RPAD(TO_CHAR(i, 'FM00000'), 2000, CHR(65 + MOD(i, 26))));
    FOR k IN 1 .. CASE WHEN MOD(i, 30) = 0 THEN 300 ELSE 100 END LOOP
      DBMS_LOB.WRITEAPPEND(b, 2000, piece);
    END LOOP;
  END LOOP;
  COMMIT;
END;
/
BEGIN
  DBMS_STATS.GATHER_TABLE_STATS(USER, 'BS_PLAIN');
  DBMS_STATS.GATHER_TABLE_STATS(USER, 'BS_LOB');
  DBMS_STATS.GATHER_TABLE_STATS(USER, 'BS_RETRY');
END;
/`);
await sleep(7000); // Oracle cannot read a table AS OF an SCN within seconds of creating it (ORA-01466)
pg(`DROP SCHEMA IF EXISTS bs_test CASCADE; CREATE SCHEMA bs_test; DROP SCHEMA IF EXISTS bs_retry CASCADE; CREATE SCHEMA bs_retry;`);
const oraLobBytes = Number(ora('SELECT SUM(DBMS_LOB.GETLENGTH(doc)) FROM bs_lob;'));
ok(`Oracle: 10,000 plain rows; 300 documents (${(oraLobBytes / 1048576).toFixed(1)} MB); 3,000 rows for the retry test; statistics gathered`);

r = await api('POST', '/api/v1/connections', { name: `bs-pg-${stamp}`, kind: 'postgres', host: PG_HOST, port: PG_PORT, serviceOrDb: PG_DB, username: PG_USER, password: PG_PASSWORD });
pgId = r.data?.id;
check(!!pgId, 'a connection to the real destination');

// ---- sizing, bytes, LOBs -----------------------------------------------------------------------
section('batches sized by the data; bytes and LOBs');
const sizeJob = await launchCopy('sizing', ['BS_PLAIN', 'BS_LOB'], 'bs_test');

// While it runs, the speed graph should see bytes, not a hard-coded 0.
let sawMb = false;
for (let i = 0; i < 90 && !sawMb; i++) {
  await sleep(1000);
  sawMb = Number(pg(`SELECT coalesce(max("MbPerSecond"), 0) FROM o2p."MetricSamples" WHERE "JobRunId" = ${sizeJob}`)) > 0;
  const s = (await api('GET', `/api/v1/jobs/${sizeJob}`)).data?.status;
  if (['Completed', 'CompletedWithErrors', 'Failed'].includes(s)) break;
}
let run = await waitForRun(sizeJob, 'sizing copy');
check(run?.status === 'Completed', `the copy completed (${run?.status}: ${run?.tableRuns?.map((t) => `${t.targetTableName}=${t.status} ${t.errorMessage ?? ''}`).join('; ')})`);

const plainChunks = chunks(sizeJob, 'BS_PLAIN');
const lobChunks = chunks(sizeJob, 'BS_LOB');
check(plainChunks.length >= 5 && plainChunks.length <= 12, `10,000 rows at 1,000 per batch -> ${plainChunks.length} batches (not the old fixed 16)`);
check(lobChunks.length >= 2 && lobChunks.length <= 4, `300 LOB rows at 100 per LOB batch -> ${lobChunks.length} batches`);
check(pg('SELECT count(*) FROM bs_test.bs_plain') === '10000' && pg('SELECT count(DISTINCT id) FROM bs_test.bs_plain') === '10000', 'all 10,000 plain rows arrived once');
check([...plainChunks, ...lobChunks].every((c) => c.status === 'Done' && c.bytes > 0), 'every batch is Done and recorded its bytes');
const tableBytes = Number(pg(`SELECT t."BytesMigrated" FROM o2p.table_runs t WHERE t."JobRunId" = ${sizeJob} AND t."TargetTableName" = 'bs_lob'`));
check(tableBytes >= oraLobBytes, `the LOB table's bytes (${(tableBytes / 1048576).toFixed(1)} MB) cover its ${(oraLobBytes / 1048576).toFixed(1)} MB of documents`);
check(sawMb, 'the speed graph showed a non-zero MB/s during the copy');

check(pg('SELECT count(*) FROM bs_test.bs_lob') === '300', 'all 300 documents arrived');
const pgLobBytes = Number(pg('SELECT sum(length(doc)) FROM bs_test.bs_lob'));
check(pgLobBytes === oraLobBytes, `document bytes match exactly (${pgLobBytes} = ${oraLobBytes})`);
const oraEnds = ora(`SELECT id || ':' || RAWTOHEX(DBMS_LOB.SUBSTR(doc, 5, 1)) || ':' || RAWTOHEX(DBMS_LOB.SUBSTR(doc, 8, DBMS_LOB.GETLENGTH(doc) - 7)) FROM bs_lob WHERE id IN (1, 29, 30, 150, 300) ORDER BY id;`)
  .split('\n').map((s) => s.trim().toLowerCase()).filter(Boolean);
const pgEnds = pg(`SELECT id || ':' || encode(substring(doc from 1 for 5), 'hex') || ':' || encode(substring(doc from length(doc) - 7 for 8), 'hex') FROM bs_test.bs_lob WHERE id IN (1, 29, 30, 150, 300) ORDER BY id;`)
  .split('\n').filter(Boolean);
check(oraEnds.length === 5 && JSON.stringify(oraEnds) === JSON.stringify(pgEnds), 'the start and end of 200 KB and 600 KB documents match byte for byte');

if (WORKER_LOG && existsSync(WORKER_LOG)) {
  const log = readFileSync(WORKER_LOG, 'utf8');
  check(/MB in [\d.]+s \([\d.]+ rows\/s, [\d.]+ MB\/s\) - \d+% waiting for Oracle, \d+% waiting for PostgreSQL/.test(log),
    'each batch logs its size, speed and where its time went');
} else {
  console.log('  - (set O2P_WORKER_LOG to also check the per-batch time split in the Worker log)');
}

// ---- retry and stall ---------------------------------------------------------------------------
section('a killed connection and a stalled batch are retried by themselves');
// Block the destination's resume fence in the retry schema, so every batch there hangs at its first write.
pg(`CREATE TABLE bs_retry._o2p_chunk_log (job_run_id bigint NOT NULL, table_run_id bigint NOT NULL, chunk_index integer NOT NULL,
     completed_at timestamptz NOT NULL DEFAULT now(), PRIMARY KEY (job_run_id, table_run_id, chunk_index));`);
const lockHeld = pgBackground(`BEGIN; LOCK TABLE bs_retry._o2p_chunk_log IN SHARE MODE; SELECT pg_sleep(100); COMMIT;`);
await sleep(1500);
const retryJob = await launchCopy('retry', ['BS_RETRY'], 'bs_retry');

// Wait for a batch to be stuck behind the lock, then kill its connection the way a VPN drop would.
let killedAt = null;
for (let i = 0; i < 60 && !killedAt; i++) {
  await sleep(1000);
  const pid = pg(`SELECT pid FROM pg_stat_activity WHERE wait_event_type = 'Lock' AND query ILIKE '%_o2p_chunk_log%' AND query ILIKE '%INSERT%' ORDER BY backend_start LIMIT 1`);
  if (pid) { pg(`SELECT pg_terminate_backend(${pid})`); killedAt = Date.now(); }
}
check(!!killedAt, 'a batch was waiting at the destination, and its connection was killed');

let requeued = null;
for (let i = 0; i < 30 && !requeued; i++) {
  await sleep(1000);
  requeued = chunks(retryJob, 'BS_RETRY').find((c) => c.status === 'Pending' && c.retryAfter && c.attempts === 1);
}
check(!!requeued && Date.now() - killedAt < 30000, `the killed batch went back to the queue by itself within ${Math.round((Date.now() - killedAt) / 1000)} s, not after a 20-minute timer`);
check(requeued && /Attempt 1 failed: .*Trying again automatically/.test(requeued.error), `with a message saying so ("${requeued?.error?.slice(0, 110)}")`);

// The batches that were not killed make no progress behind the lock; the watchdog stops them after a minute.
let stalled = null;
for (let i = 0; i < 100 && !stalled; i++) {
  await sleep(1000);
  stalled = chunks(retryJob, 'BS_RETRY').find((c) => /No row moved for 1 minute,/.test(c.error));
}
check(!!stalled, `a batch that stopped moving was stopped by the stall watchdog and queued again ("${stalled?.error?.slice(0, 90)}")`);

await lockHeld;
run = await waitForRun(retryJob, 'retry copy', 420);
check(run?.status === 'Completed', `once the destination was free again, the copy completed with no Retry pressed (${run?.status}: ${run?.tableRuns?.map((t) => `${t.targetTableName}=${t.status} ${t.errorMessage ?? ''}`).join('; ')})`);
const retryChunks = chunks(retryJob, 'BS_RETRY');
check(retryChunks.every((c) => c.status === 'Done') && retryChunks.some((c) => c.attempts >= 2), `every batch Done; attempts ${retryChunks.map((c) => c.attempts).join(', ')}`);
check(pg('SELECT count(*) FROM bs_retry.bs_retry') === '3000' && pg('SELECT count(DISTINCT id) FROM bs_retry.bs_retry') === '3000', 'the destination holds each of the 3,000 rows exactly once');

// ---- tidy up ------------------------------------------------------------------------------------
section('tidy up');
pg('DROP SCHEMA IF EXISTS bs_test CASCADE; DROP SCHEMA IF EXISTS bs_retry CASCADE;');
ora(`BEGIN
  FOR t IN (SELECT table_name FROM user_tables WHERE table_name IN ('BS_PLAIN', 'BS_LOB', 'BS_RETRY')) LOOP
    EXECUTE IMMEDIATE 'DROP TABLE ' || t.table_name || ' PURGE';
  END LOOP;
END;
/`);
ok('test tables dropped on both sides');

console.log(failures === 0 ? '\nALL BULK-SPEED CHECKS PASSED' : `\n${failures} CHECK(S) FAILED`);
process.exit(failures === 0 ? 0 : 1);
