// Verifies the "reuse an existing target table" behaviour against a REAL PostgreSQL target.
// The Oracle source stays mock (mock checks are per-connection), so the whole target-side path -
// introspection, compatibility check, CREATE vs reuse, truncate, constraint snapshot/restore and
// the real binary COPY - runs for real.
//
// Usage: O2P_API=http://127.0.0.1:5050 O2P_ADMIN_PW=... PGHOST=... node scripts/verify-target-reuse.mjs
import { execFileSync } from 'node:child_process';

const API = process.env.O2P_API || 'http://127.0.0.1:5050';
const ADMIN_PW = process.env.O2P_ADMIN_PW || 'TestAdmin2026!Bb';
const PG_CONTAINER = process.env.O2P_PG_CONTAINER || 'dataflow-metadata-pg-1';
const PG_USER = process.env.O2P_PG_USER || 'nonoraclemigrationdb';
const PG_DB = process.env.O2P_PG_DB || 'nonoraclemigrationdb';
const PG_HOST = process.env.O2P_PG_HOST || '127.0.0.1';
const PG_PORT = Number(process.env.O2P_PG_PORT || 7936);
const PG_PASSWORD = process.env.O2P_PG_PASSWORD || 'change-me-local-only';
const SCHEMA = 'target_test';

let token = null;
let failures = 0;
const log = (m) => process.stdout.write(m + '\n');
const ok = (n, extra = '') => log(`  ✓ ${n}${extra ? ' — ' + extra : ''}`);
const fail = (n, d) => { failures++; log(`  ✗ ${n}\n      ${d}`); };
const assert = (c, n, d) => c ? ok(n) : fail(n, d || 'assertion failed');
const sleep = (ms) => new Promise(r => setTimeout(r, ms));
const uniq = (p) => `${p}${Date.now().toString().slice(-7)}`;

async function api(method, path, body) {
  const headers = { 'Content-Type': 'application/json' };
  if (token) headers['Authorization'] = `Bearer ${token}`;
  const res = await fetch(`${API}${path}`, { method, headers, body: body === undefined ? undefined : JSON.stringify(body) });
  const text = await res.text();
  let data = null;
  if (text) { try { data = JSON.parse(text); } catch { data = text; } }
  return { status: res.status, data };
}

function psql(sql) {
  return execFileSync('docker', ['exec', '-i', PG_CONTAINER, 'psql', '-U', PG_USER, '-d', PG_DB, '-A', '-t', '-c', sql], { encoding: 'utf8' }).trim();
}

async function runJobToTerminal(appId, manifestId, label) {
  let r = await api('POST', '/api/v1/jobs', { applicationId: appId, manifestId, sourceSlot: 'oracle_test', targetSlot: 'pg_test', targetSchema: SCHEMA });
  if (r.status !== 201 && r.status !== 200) { fail(`${label}: create job`, `status=${r.status} body=${JSON.stringify(r.data)}`); return null; }
  const jobId = r.data.id;

  r = await api('POST', `/api/v1/jobs/${jobId}/launch`, {});
  if (r.status !== 200) { fail(`${label}: launch job`, `status=${r.status} body=${JSON.stringify(r.data)}`); return null; }

  for (let i = 0; i < 150; i++) {
    await sleep(2000);
    r = await api('GET', `/api/v1/jobs/${jobId}`);
    const st = r.data?.status;
    if (['Completed', 'CompletedWithErrors', 'Failed', 'Cancelled'].includes(st)) return r.data;
  }
  fail(`${label}: job reached a terminal state`, 'timed out');
  return null;
}

async function main() {
  log('\n=== TARGET-REUSE VERIFICATION (real PostgreSQL target) ===\n');

  let r = await api('POST', '/api/v1/auth/login', { username: 'admin', password: ADMIN_PW });
  if (r.status !== 200) { fail('login', `status=${r.status} body=${JSON.stringify(r.data)}`); process.exit(1); }
  token = r.data.token;
  ok('login');

  psql(`DROP SCHEMA IF EXISTS ${SCHEMA} CASCADE; CREATE SCHEMA ${SCHEMA};`);
  ok('clean target schema');

  // mock Oracle source + REAL postgres target
  const oraName = uniq('vr-ora-');
  const pgName = uniq('vr-pg-');
  r = await api('POST', '/api/v1/connections', { name: oraName, kind: 'oracle', host: 'mock', port: 1521, serviceOrDb: 'ORCLPDB1', username: 'appuser', password: 'OraclePw2026!A' });
  const oraId = r.data.id;
  r = await api('POST', '/api/v1/connections', { name: pgName, kind: 'postgres', host: PG_HOST, port: PG_PORT, serviceOrDb: PG_DB, username: PG_USER, password: PG_PASSWORD });
  assert(r.status === 201, 'create REAL postgres target connection', `status=${r.status} body=${JSON.stringify(r.data)}`);
  const pgId = r.data.id;

  r = await api('POST', `/api/v1/connections/${pgId}/test`, {});
  assert(r.status === 200 && r.data.success === true, 'real postgres connection reachable', JSON.stringify(r.data));

  const appName = uniq('VR-App-');
  r = await api('POST', '/api/v1/applications', { name: appName, description: 'target reuse verification' });
  const appId = r.data.id;
  await api('PUT', `/api/v1/applications/${appId}`, {
    id: appId, name: appName, description: 'target reuse verification',
    connections: [
      { applicationId: appId, slot: 'oracle_test', connectionId: oraId },
      { applicationId: appId, slot: 'pg_test', connectionId: pgId },
    ],
  });

  await api('POST', `/api/v1/connections/${oraId}/discovery/refresh?owner=APP`, { tableNames: null });
  r = await api('POST', `/api/v1/applications/${appId}/manifests/generate?connectionId=${oraId}&owner=APP&version=1.0`, {});
  assert(r.status === 200 || r.status === 201, 'generate manifest', `status=${r.status} body=${JSON.stringify(r.data)}`);
  const manifestId = r.data.id;

  // ---------------------------------------------------------------- PATH A
  log('\n[Path A] target table absent -> created and loaded');
  let job = await runJobToTerminal(appId, manifestId, 'A');
  if (!job) { log('\nAborting: path A did not finish.'); process.exit(1); }

  assert(job.status === 'Completed', 'A: job Completed', `status=${job.status}`);
  assert(job.tableRuns.every(t => t.status === 'Completed'), 'A: every table Completed',
    JSON.stringify(job.tableRuns.map(t => [t.targetTableName, t.status, t.errorMessage])));
  assert(job.tableRuns.every(t => t.targetTablePreExisted === false), 'A: every table reported as created',
    JSON.stringify(job.tableRuns.map(t => [t.targetTableName, t.targetTablePreExisted])));

  const tables = psql(`SELECT relname FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname='${SCHEMA}' AND c.relkind='r' ORDER BY relname;`).split('\n').filter(Boolean);
  assert(tables.length >= 2, 'A: tables physically created', `found=${tables.join(',')}`);
  const custRows = Number(psql(`SELECT count(*) FROM ${SCHEMA}."CUSTOMERS";`));
  assert(custRows > 0, 'A: CUSTOMERS holds rows', `rows=${custRows}`);

  // ---------------------------------------------------------------- PATH B
  log('\n[Path B] target table present and matching -> reused, no DDL, truncate+reload');
  psql(`COMMENT ON TABLE ${SCHEMA}."CUSTOMERS" IS 'operator-owned comment';`);
  psql(`ALTER TABLE ${SCHEMA}."CUSTOMERS" ADD COLUMN extra_note text;`);
  psql(`ALTER TABLE ${SCHEMA}."CUSTOMERS" ADD CONSTRAINT ck_extra CHECK (extra_note IS NULL OR length(extra_note) < 500);`);
  ok('B: added comment, nullable column and CHECK to the existing table');

  job = await runJobToTerminal(appId, manifestId, 'B');
  if (!job) { log('\nAborting: path B did not finish.'); process.exit(1); }

  assert(job.status === 'Completed', 'B: job Completed', `status=${job.status}`);
  assert(job.tableRuns.every(t => t.targetTablePreExisted === true), 'B: every table reported as reused',
    JSON.stringify(job.tableRuns.map(t => [t.targetTableName, t.targetTablePreExisted])));

  const comment = psql(`SELECT obj_description('${SCHEMA}."CUSTOMERS"'::regclass, 'pg_class');`);
  assert(comment === 'operator-owned comment', 'B: operator comment survived (no table recreate)', `got=${comment}`);
  const extraCol = psql(`SELECT count(*) FROM pg_attribute a JOIN pg_class c ON c.oid=a.attrelid JOIN pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname='${SCHEMA}' AND c.relname='CUSTOMERS' AND a.attname='extra_note' AND NOT a.attisdropped;`);
  assert(extraCol === '1', 'B: operator-added column survived', `count=${extraCol}`);
  const chk = psql(`SELECT count(*) FROM pg_constraint WHERE conname='ck_extra' AND conrelid='${SCHEMA}."CUSTOMERS"'::regclass;`);
  assert(chk === '1', 'B: CHECK constraint restored after load', `count=${chk}`);
  // Compare against a second reuse run rather than against path A: a partial path A (see the
  // known _o2p_chunk_log creation race) would otherwise make this look like duplication.
  const custRowsB = Number(psql(`SELECT count(*) FROM ${SCHEMA}."CUSTOMERS";`));
  const jobB2 = await runJobToTerminal(appId, manifestId, 'B2');
  const custRowsB2 = Number(psql(`SELECT count(*) FROM ${SCHEMA}."CUSTOMERS";`));
  assert(jobB2?.status === 'Completed', 'B: second reuse run Completed', `status=${jobB2?.status}`);
  assert(custRowsB2 === custRowsB, 'B: truncate+reload is repeatable (no duplicates)', `run1=${custRowsB} run2=${custRowsB2}`);

  // ---------------------------------------------------------------- PATH C
  log('\n[Path C] target table present but mismatching -> only that table fails, data untouched');
  // Break ORDERS deliberately. Narrow a column and add a mandatory one.
  const ordersCols = psql(`SELECT a.attname || ' ' || format_type(a.atttypid,a.atttypmod) FROM pg_attribute a JOIN pg_class c ON c.oid=a.attrelid JOIN pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname='${SCHEMA}' AND c.relname='ORDERS' AND a.attnum>0 AND NOT a.attisdropped ORDER BY a.attnum;`);
  log(`      ORDERS columns before breaking:\n        ${ordersCols.split('\n').join('\n        ')}`);

  psql(`ALTER TABLE ${SCHEMA}."ORDERS" ADD COLUMN tenant_id integer NOT NULL DEFAULT 0;`);
  psql(`ALTER TABLE ${SCHEMA}."ORDERS" ALTER COLUMN tenant_id DROP DEFAULT;`);
  ok('C: added a mandatory tenant_id column with no default to ORDERS');

  const ordersRowsBefore = Number(psql(`SELECT count(*) FROM ${SCHEMA}."ORDERS";`));
  log(`      ORDERS row count before the mismatching run: ${ordersRowsBefore}`);

  job = await runJobToTerminal(appId, manifestId, 'C');
  if (!job) { log('\nAborting: path C did not finish.'); process.exit(1); }

  const ordersRun = job.tableRuns.find(t => t.targetTableName === 'ORDERS');
  const otherRuns = job.tableRuns.filter(t => t.targetTableName !== 'ORDERS');

  assert(ordersRun?.status === 'Failed', 'C: ORDERS failed', `status=${ordersRun?.status}`);
  assert(otherRuns.every(t => t.status === 'Completed'), 'C: every other table still Completed',
    JSON.stringify(otherRuns.map(t => [t.targetTableName, t.status])));
  assert(job.status === 'CompletedWithErrors', 'C: job settled as CompletedWithErrors (did not hang)', `status=${job.status}`);
  assert(/already exists, but its columns do not match/.test(ordersRun?.errorMessage || ''), 'C: message explains the mismatch',
    `errorMessage=${ordersRun?.errorMessage}`);
  assert(/tenant_id/.test(ordersRun?.errorMessage || ''), 'C: message names the offending column', `errorMessage=${ordersRun?.errorMessage}`);
  log(`      ORDERS errorMessage:\n        ${(ordersRun?.errorMessage || '').split('\n').join('\n        ')}`);

  const ordersRowsAfter = Number(psql(`SELECT count(*) FROM ${SCHEMA}."ORDERS";`));
  assert(ordersRowsAfter === ordersRowsBefore, 'C: ORDERS rows NOT truncated', `before=${ordersRowsBefore} after=${ordersRowsAfter}`);

  const snapshot = psql(`SELECT coalesce("ConstraintSnapshotJson"::text,'NULL') FROM o2p.table_runs WHERE "Id"=${ordersRun.id};`);
  assert(snapshot === 'NULL', 'C: ConstraintSnapshotJson left NULL', `got=${snapshot.slice(0, 120)}`);

  // ------------------------------------------------------- PATH C (retry)
  log('\n[Path C-retry] retry the mismatch without fixing it -> still non-destructive');
  // This is the regression test for the stale-snapshot hole: without clearing
  // ConstraintSnapshotJson the Worker safety net would truncate this table.
  r = await api('POST', `/api/v1/jobs/${job.id}/commands`, { command: 'retry_failed', scope: 'job' });
  assert(r.status >= 200 && r.status < 300, 'C-retry: retry_failed accepted', `status=${r.status}`);

  let retried = null;
  for (let i = 0; i < 60; i++) {
    await sleep(2000);
    const jr = await api('GET', `/api/v1/jobs/${job.id}`);
    const run = jr.data?.tableRuns?.find(t => t.targetTableName === 'ORDERS');
    if (run?.status === 'Failed' && ['Completed', 'CompletedWithErrors', 'Failed'].includes(jr.data.status)) { retried = run; break; }
  }
  assert(retried != null, 'C-retry: ORDERS failed again', 'never settled');

  const ordersRowsRetry = Number(psql(`SELECT count(*) FROM ${SCHEMA}."ORDERS";`));
  assert(ordersRowsRetry === ordersRowsBefore, 'C-retry: ORDERS rows STILL not truncated (stale-snapshot regression)',
    `before=${ordersRowsBefore} after=${ordersRowsRetry}`);

  // ---------------------------------------------------------------- PATH D
  log('\n[Path D] type mismatch on an existing table (timestamp(0) -> date)');
  // Oracle DATE maps to timestamp(0); hand-built Postgres targets commonly use date. 8 bytes into
  // a 4-byte date_recv would abort mid-COPY, so it has to be caught before the truncate.
  psql(`ALTER TABLE ${SCHEMA}."ORDERS" DROP COLUMN tenant_id;`);
  psql(`ALTER TABLE ${SCHEMA}."ORDERS" DROP COLUMN "CREATED_AT";`);
  psql(`ALTER TABLE ${SCHEMA}."ORDERS" ADD COLUMN "CREATED_AT" date;`);
  ok('D: retyped ORDERS."CREATED_AT" from timestamp(0) to date');

  const ordersRowsD = Number(psql(`SELECT count(*) FROM ${SCHEMA}."ORDERS";`));
  job = await runJobToTerminal(appId, manifestId, 'D');
  if (!job) { log('\nAborting: path D did not finish.'); process.exit(1); }

  const ordersRunD = job.tableRuns.find(t => t.targetTableName === 'ORDERS');
  assert(ordersRunD?.status === 'Failed', 'D: ORDERS failed on the type mismatch', `status=${ordersRunD?.status}`);
  assert(/CREATED_AT/.test(ordersRunD?.errorMessage || ''), 'D: message names CREATED_AT', `errorMessage=${ordersRunD?.errorMessage}`);
  assert(/date/.test(ordersRunD?.errorMessage || ''), 'D: message names the target type', `errorMessage=${ordersRunD?.errorMessage}`);
  log(`      ORDERS errorMessage:\n        ${(ordersRunD?.errorMessage || '').split('\n').join('\n        ')}`);
  assert(Number(psql(`SELECT count(*) FROM ${SCHEMA}."ORDERS";`)) === ordersRowsD, 'D: ORDERS rows NOT truncated',
    `before=${ordersRowsD}`);

  log(`\n=== SUMMARY ===\nFAIL: ${failures}\n`);
  process.exit(failures ? 1 : 0);
}

main().catch(e => { log('FATAL ' + (e?.stack || e)); process.exit(1); });
