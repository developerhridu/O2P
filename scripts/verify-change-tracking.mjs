// End-to-end check of "Copy changes" against a REAL Oracle source and a REAL PostgreSQL destination,
// through the API and a running Worker: a bulk copy sets up tracking, then inserts, updates, deletes,
// a key change, a LOB write, a transaction left open across a copy, a truncate and missing history are
// each carried across (or refused) correctly.
//
// SAFETY: creates, changes and drops tables in the Oracle account and PostgreSQL schema it is given, and
// rewrites tracking rows in the metadata database. Throwaway databases only; it refuses the usual ports.
//
// Usage: O2P_API=http://127.0.0.1:5050 O2P_ADMIN_PW=... node scripts/verify-change-tracking.mjs
//   Oracle defaults: container o2p-oracle-test, o2ptrack/Track2026, 127.0.0.1:15210/FREEPDB1
//   PostgreSQL defaults: container dataflow-metadata-pg-1, 127.0.0.1:7936/nonoraclemigrationdb
import { execFileSync, spawn } from 'node:child_process';

const API = process.env.O2P_API;
const ADMIN_PW = process.env.O2P_ADMIN_PW;
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
const SCHEMA = 'ct_test';

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

// ---- the two databases ------------------------------------------------------------------------
function ora(sql) {
  const script = `SET HEADING OFF FEEDBACK OFF PAGESIZE 0 LINESIZE 32767 TRIMSPOOL ON\nWHENEVER SQLERROR EXIT FAILURE\n${sql}\nEXIT\n`;
  return execFileSync('docker', ['exec', '-i', ORA_CONTAINER, 'sqlplus', '-s', `${ORA_USER}/${ORA_PASSWORD}@localhost:1521/${ORA_SERVICE}`],
    { input: script, encoding: 'utf8' }).trim();
}
/** An Oracle session that changes a row, holds the transaction open for a while, then commits. */
function oraHeldOpen(sql, seconds) {
  const script = `WHENEVER SQLERROR EXIT FAILURE\n${sql}\nEXEC DBMS_SESSION.SLEEP(${seconds});\nCOMMIT;\nEXIT\n`;
  const child = spawn('docker', ['exec', '-i', ORA_CONTAINER, 'sqlplus', '-s', `${ORA_USER}/${ORA_PASSWORD}@localhost:1521/${ORA_SERVICE}`], { stdio: ['pipe', 'ignore', 'ignore'] });
  child.stdin.end(script);
  return new Promise((resolve) => child.on('exit', resolve));
}
function pg(sql) {
  return execFileSync('docker', ['exec', '-i', PG_CONTAINER, 'psql', '-U', PG_USER, '-d', PG_DB, '-A', '-t', '-c', sql], { encoding: 'utf8' }).trim();
}

// A row as text on both sides, so the two can be compared exactly. NULL and empty are the same thing
// in Oracle, which is also what the copy writes.
const oraCustomers = () => ora(`SELECT id || '|' || name || '|' || NVL(DBMS_LOB.GETLENGTH(notes), 0) || '|' || NVL(DBMS_LOB.SUBSTR(notes, 12, 1), '') FROM ct_customers ORDER BY id;`)
  .split('\n').map((s) => s.trim()).filter(Boolean);
const pgCustomers = () => pg(`SELECT id || '|' || coalesce(name, '') || '|' || coalesce(length(notes), 0) || '|' || coalesce(substr(notes, 1, 12), '') FROM ${SCHEMA}.ct_customers ORDER BY id;`)
  .split('\n').filter(Boolean);

async function waitForRun(id, label, seconds = 300) {
  for (let i = 0; i < seconds / 2; i++) {
    await sleep(2000);
    const r = await api('GET', `/api/v1/jobs/${id}`);
    if (['Completed', 'CompletedWithErrors', 'Failed', 'Cancelled'].includes(r.data?.status)) return r.data;
  }
  bad(`${label}: finished in time`);
  return null;
}

const tracked = async (appId) => (await api('GET', `/api/v1/applications/${appId}/tracked-tables`)).data ?? [];

console.log('\n=== CHANGE TRACKING (real Oracle -> real PostgreSQL) ===');

// ---- seed ---------------------------------------------------------------------------------------
section('seed');
let r = await api('POST', '/api/v1/auth/login', { username: 'admin', password: ADMIN_PW });
if (r.status !== 200) { console.log(`login failed: ${r.status} ${JSON.stringify(r.data)}`); process.exit(1); }
token = r.data.token;
ok('logged in once');

ora(`
BEGIN
  FOR t IN (SELECT table_name FROM user_tables WHERE table_name IN ('CT_CUSTOMERS', 'CT_ORDERS', 'CT_NOKEY')) LOOP
    EXECUTE IMMEDIATE 'DROP TABLE ' || t.table_name || ' PURGE';
  END LOOP;
END;
/
CREATE TABLE ct_customers (id NUMBER(10) PRIMARY KEY, name VARCHAR2(100), notes CLOB);
CREATE TABLE ct_orders (id NUMBER(10) PRIMARY KEY, customer_id NUMBER(10), amount NUMBER(12,2));
CREATE TABLE ct_nokey (id NUMBER(10), v VARCHAR2(10));
INSERT INTO ct_customers SELECT LEVEL, 'customer ' || LEVEL, CASE WHEN MOD(LEVEL, 50) = 0 THEN TO_CLOB(RPAD('n', 4000, 'n')) || RPAD('m', 4000, 'm') END FROM dual CONNECT BY LEVEL <= 200;
INSERT INTO ct_orders SELECT LEVEL, MOD(LEVEL, 200) + 1, LEVEL * 1.5 FROM dual CONNECT BY LEVEL <= 300;
INSERT INTO ct_nokey VALUES (1, 'a');
COMMIT;`);
await sleep(7000); // Oracle cannot read a table AS OF an SCN within seconds of creating it (ORA-01466)
pg(`DROP SCHEMA IF EXISTS ${SCHEMA} CASCADE; CREATE SCHEMA ${SCHEMA};`);
ok('Oracle: 200 customers (some with LOBs), 300 orders, one table with no key; clean destination schema');

const stamp = Date.now().toString().slice(-6);
r = await api('POST', '/api/v1/connections', { name: `ct-ora-${stamp}`, kind: 'oracle', host: ORA_HOST, port: ORA_PORT, serviceOrDb: ORA_SERVICE, username: ORA_USER, password: ORA_PASSWORD });
const oraId = r.data?.id;
r = await api('POST', '/api/v1/connections', { name: `ct-pg-${stamp}`, kind: 'postgres', host: PG_HOST, port: PG_PORT, serviceOrDb: PG_DB, username: PG_USER, password: PG_PASSWORD });
const pgId = r.data?.id;
r = await api('POST', '/api/v1/applications', { name: `Change tracking ${stamp}`, description: 'temporary' });
const appId = r.data?.id;
await api('PUT', `/api/v1/applications/${appId}`, {
  id: appId, name: `Change tracking ${stamp}`, description: 'temporary',
  connections: [
    { applicationId: appId, slot: 'oracle_test', connectionId: oraId },
    { applicationId: appId, slot: 'pg_test', connectionId: pgId },
  ],
});
r = await api('POST', `/api/v1/connections/${oraId}/discovery/refresh?owner=${ORA_USER.toUpperCase()}`, { tableNames: ['CT_CUSTOMERS', 'CT_ORDERS', 'CT_NOKEY'] });
check(r.status === 200, `the three tables were scanned from the real Oracle (${r.status} ${r.data?.message ?? JSON.stringify(r.data)})`);
r = await api('POST', `/api/v1/applications/${appId}/manifests/generate?connectionId=${oraId}&owner=${ORA_USER.toUpperCase()}&version=1.0`, {});
const manifestId = r.data?.id;
check(!!(oraId && pgId && appId && manifestId), 'seeded a migration against the real databases');

// ---- readiness ------------------------------------------------------------------------------------
section('readiness');
r = await api('POST', `/api/v1/applications/${appId}/change-readiness`, { sourceSlot: 'oracle_test', manifestId });
check(r.status === 200, `the check ran (${r.status})`);
const items = r.data?.items ?? [];
check(items.filter((i) => i.name !== 'FORCE LOGGING').every((i) => i.ok), `every source prerequisite is in place (${items.filter((i) => !i.ok).map((i) => i.name).join(', ') || 'none missing'})`);
check((r.data?.layout ?? '').includes('pluggable database'), `it recognises the layout: "${r.data?.layout}"`);
const nokey = (r.data?.tables ?? []).find((t) => t.table === 'CT_NOKEY');
check(nokey && !nokey.ok && nokey.problems.some((p) => /no primary key/.test(p)), 'it flags the table with no primary key');

// ---- bulk copy sets up tracking ------------------------------------------------------------------
section('bulk copy');
r = await api('POST', '/api/v1/jobs', { applicationId: appId, manifestId, sourceSlot: 'oracle_test', targetSlot: 'pg_test', targetSchema: SCHEMA });
const bulkId = r.data?.id;
r = await api('POST', `/api/v1/jobs/${bulkId}/launch`, {});
check(r.status === 200, `bulk copy launched (${r.status} ${JSON.stringify(r.data?.errors ?? '')})`);
let run = await waitForRun(bulkId, 'bulk copy');
check(run && ['Completed', 'CompletedWithErrors'].includes(run.status), `bulk copy finished (${run?.status}: ${run?.tableRuns?.map((t) => `${t.targetTableName}=${t.status} ${t.errorMessage ?? ''}`).join('; ')})`);
check(pg(`SELECT count(*) FROM ${SCHEMA}.ct_customers`) === '200', 'all 200 customers arrived');

let list = await tracked(appId);
const trackedNames = list.map((t) => t.targetTableName).sort();
check(JSON.stringify(trackedNames) === JSON.stringify(['ct_customers', 'ct_orders']), `the two keyed tables are tracked, the unkeyed one is not (${trackedNames})`);
check(list.every((t) => t.status === 'needs_first_sync' && t.lastScn), 'each waits for a first copy to settle it, from a recorded start point');

// ---- inserts, updates, deletes, a key change and a LOB write -------------------------------------
section('first Copy changes');
ora(`
INSERT INTO ct_customers VALUES (201, 'new one', NULL);
INSERT INTO ct_customers VALUES (202, 'new two', NULL);
UPDATE ct_customers SET name = 'renamed' WHERE id IN (3, 4);
DELETE FROM ct_customers WHERE id IN (5, 6);
UPDATE ct_customers SET id = 10010 WHERE id = 10;
COMMIT;
DECLARE l CLOB; BEGIN SELECT notes INTO l FROM ct_customers WHERE id = 50 FOR UPDATE; DBMS_LOB.WRITE(l, 5, 1, 'LOBXX'); COMMIT; END;
/`);
ok('in Oracle: 2 inserts, 2 updates, 2 deletes, a key change 10 -> 10010, and a LOB-only write on 50');

r = await api('POST', `/api/v1/applications/${appId}/manifests/${manifestId}/copy-changes`, { sourceSlot: 'oracle_test', targetSlot: 'pg_test', targetSchema: SCHEMA });
check(r.status === 200, `Copy changes started (${r.status} ${JSON.stringify(r.data)})`);
check((r.data?.skipped ?? []).some((s) => /CT_NOKEY/.test(s.table)), 'the unkeyed table is listed as skipped, with a reason');
run = await waitForRun(r.data?.id, 'first change copy');
check(run?.status === 'Completed', `it completed (${run?.status}: ${run?.tableRuns?.map((t) => `${t.targetTableName}=${t.status} ${t.errorMessage ?? ''}`).join('; ')})`);
check(run?.kind === 'changes', 'it is recorded as a change copy');

const oraRows = oraCustomers();
const pgRows = pgCustomers();
check(oraRows.length === pgRows.length && oraRows.every((row, i) => row === pgRows[i]),
  `the destination matches Oracle row for row (${pgRows.length} rows${oraRows.length === pgRows.length ? '' : ` vs ${oraRows.length}`})`);
check(pg(`SELECT count(*) FROM ${SCHEMA}.ct_customers WHERE id = 10`) === '0' && pg(`SELECT count(*) FROM ${SCHEMA}.ct_customers WHERE id = 10010`) === '1', 'the key change removed 10 and added 10010');
check(pg(`SELECT substr(notes, 1, 5) FROM ${SCHEMA}.ct_customers WHERE id = 50`) === 'LOBXX', 'the LOB-only write arrived');
check(pg(`SELECT conname FROM pg_constraint WHERE conrelid = '${SCHEMA}.ct_customers'::regclass AND contype = 'p'`) === 'ct_customers_pkey', 'settling added a lower-case primary key');
list = await tracked(appId);
check(list.every((t) => t.status === 'ready' && t.lastSyncedAt && t.activeJobRunId == null), 'both tables are ready, synced, and released');
const cust = run?.tableRuns?.find((t) => t.targetTableName === 'ct_customers');
check(cust && cust.rowsDeleted >= 3, `written ${cust?.rowsWritten}, deleted ${cust?.rowsDeleted}`);

// ---- nothing changed -------------------------------------------------------------------------------
section('second Copy changes, nothing changed');
r = await api('POST', `/api/v1/applications/${appId}/manifests/${manifestId}/copy-changes`, { sourceSlot: 'oracle_test', targetSlot: 'pg_test', targetSchema: SCHEMA });
run = await waitForRun(r.data?.id, 'quiet change copy');
check(run?.status === 'Completed', `it completed (${run?.status})`);
const written = (run?.tableRuns ?? []).reduce((a, t) => a + t.rowsWritten + t.rowsDeleted, 0);
check(written === 0, `it wrote and deleted nothing (${written})`);

// ---- a transaction left open across a press ---------------------------------------------------------
section('a transaction open across a copy');
const held = oraHeldOpen(`UPDATE ct_customers SET name = 'committed late' WHERE id = 7;`, 25);
await sleep(4000);
r = await api('POST', `/api/v1/applications/${appId}/manifests/${manifestId}/copy-changes`, { sourceSlot: 'oracle_test', targetSlot: 'pg_test', targetSchema: SCHEMA });
run = await waitForRun(r.data?.id, 'copy during an open transaction');
check(run?.status === 'Completed', `the copy during the open transaction completed (${run?.status})`);
check(pg(`SELECT name FROM ${SCHEMA}.ct_customers WHERE id = 7`) === 'customer 7', 'it did not see the uncommitted value');
list = await tracked(appId);
check(list.some((t) => Array.isArray(t.heldBackBy) && t.heldBackBy.length > 0), 'the tables record what held the resume point back');
await held;
r = await api('POST', `/api/v1/applications/${appId}/manifests/${manifestId}/copy-changes`, { sourceSlot: 'oracle_test', targetSlot: 'pg_test', targetSchema: SCHEMA });
run = await waitForRun(r.data?.id, 'copy after the commit');
check(pg(`SELECT name FROM ${SCHEMA}.ct_customers WHERE id = 7`) === 'committed late', 'the next copy brought the late-committed value across');

// ---- a change run is never retried the bulk way ----------------------------------------------------
section('retry');
r = await api('POST', `/api/v1/jobs/${run?.id}/commands`, { command: 'retry_failed', scope: 'job' });
check(r.status === 400 && /not retried/.test(JSON.stringify(r.data)), `Retry is refused for a change copy, which would otherwise empty the tables (${r.status})`);

// ---- a truncate in Oracle -------------------------------------------------------------------------
section('truncate in Oracle');
ora('TRUNCATE TABLE ct_orders;\nINSERT INTO ct_customers VALUES (203, \'after truncate\', NULL);\nCOMMIT;');
r = await api('POST', `/api/v1/applications/${appId}/manifests/${manifestId}/copy-changes`, { sourceSlot: 'oracle_test', targetSlot: 'pg_test', targetSchema: SCHEMA });
run = await waitForRun(r.data?.id, 'copy after truncate');
const orders = run?.tableRuns?.find((t) => t.targetTableName === 'ct_orders');
check(run?.status === 'CompletedWithErrors', `the copy finished with errors (${run?.status})`);
check(orders?.status === 'Failed' && /truncated/.test(orders?.errorMessage ?? ''), `orders stopped and says why: "${orders?.errorMessage?.slice(0, 80)}..."`);
check(pg(`SELECT count(*) FROM ${SCHEMA}.ct_orders`) === '300', 'nothing in the destination orders table was touched');
check(pg(`SELECT count(*) FROM ${SCHEMA}.ct_customers WHERE id = 203`) === '1', 'customers still copied its change');
list = await tracked(appId);
check(list.find((t) => t.targetTableName === 'ct_orders')?.status === 'needs_bulk_copy', 'orders now needs a fresh bulk copy');

// ---- missing history ------------------------------------------------------------------------------
section('missing history');
// A start point inside this database's current timeline but older than any log it still keeps - what
// a tracker looks like when archived logs were deleted before anyone pressed Copy changes. (An SCN from
// before the last RESETLOGS would be caught by the more specific "timeline" check instead.)
const lostScn = ora('SELECT RESETLOGS_CHANGE# + 1 FROM V$DATABASE;').trim();
pg(`UPDATE o2p.tracked_tables SET "LastScn" = ${lostScn} WHERE "TargetSchema" = '${SCHEMA}' AND "TargetTableName" = 'ct_customers';`);
const before = pg(`SELECT count(*) FROM ${SCHEMA}.ct_customers`);
r = await api('POST', `/api/v1/applications/${appId}/manifests/${manifestId}/copy-changes`, { sourceSlot: 'oracle_test', targetSlot: 'pg_test', targetSchema: SCHEMA });
run = await waitForRun(r.data?.id, 'copy with history gone');
const c2 = run?.tableRuns?.find((t) => t.targetTableName === 'ct_customers');
check(c2?.status === 'Failed' && /no longer on the source server/.test(c2?.errorMessage ?? ''), `history that is gone is reported plainly: "${c2?.errorMessage?.slice(0, 70)}..."`);
check(pg(`SELECT count(*) FROM ${SCHEMA}.ct_customers`) === before, 'and nothing in the destination was touched');

// ---- a bulk copy sets tracking up again -----------------------------------------------------------
section('bulk copy again');
r = await api('POST', '/api/v1/jobs', { applicationId: appId, manifestId, sourceSlot: 'oracle_test', targetSlot: 'pg_test', targetSchema: SCHEMA });
await api('POST', `/api/v1/jobs/${r.data?.id}/launch`, {});
run = await waitForRun(r.data?.id, 'second bulk copy');
list = await tracked(appId);
check(list.every((t) => t.status === 'ready'), `both tables are tracked again, ready at once since the tables already existed (${list.map((t) => `${t.targetTableName}=${t.status}`)})`);

// ---- saving the selection keeps the trackers --------------------------------------------------------
section('saving the selection');
const manifest = (await api('GET', `/api/v1/manifests/${manifestId}`)).data;
r = await api('PUT', `/api/v1/manifests/${manifestId}/tables`, manifest.tables.map((t) => ({ ...t, id: 0 })));
check(r.status === 200, 'the selection was saved');
check((await tracked(appId)).length === 2, 'the tracked tables survived the save');

// ---- cancel before it starts -----------------------------------------------------------------------
section('cancel');
r = await api('POST', `/api/v1/applications/${appId}/manifests/${manifestId}/copy-changes`, { sourceSlot: 'oracle_test', targetSlot: 'pg_test', targetSchema: SCHEMA });
const cancelId = r.data?.id;
await api('POST', `/api/v1/jobs/${cancelId}/commands`, { command: 'cancel', scope: 'job' });
run = await waitForRun(cancelId, 'cancelled copy', 60);
check(run?.status === 'Cancelled', `it is cancelled (${run?.status})`);
await sleep(4000);
check((await tracked(appId)).every((t) => t.activeJobRunId == null), 'no table is left claimed');

// ---- tidy -----------------------------------------------------------------------------------------
await api('DELETE', `/api/v1/applications/${appId}`);
pg(`DELETE FROM o2p.tracked_tables WHERE "TargetSchema" = '${SCHEMA}';`);
await api('DELETE', `/api/v1/connections/${oraId}`);
await api('DELETE', `/api/v1/connections/${pgId}`);

console.log(failures ? `\n=== ${failures} CHECK(S) FAILED ===` : '\n=== ALL CHECKS PASSED ===');
process.exit(failures ? 1 : 0);
