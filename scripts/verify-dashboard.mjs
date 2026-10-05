// End-to-end check of the Dashboard's row count comparison against a REAL Oracle source and a REAL
// PostgreSQL destination: table lists, exact counts, pairing by name, every status, a dropped table
// disappearing, Stop cancelling a count on the database, Viewer permissions, and the page in a browser.
//
// SAFETY: creates and drops tables in the Oracle account and the PostgreSQL schema it is given, and a
// temporary Viewer user. Throwaway databases only; it refuses the usual dev ports and port 5432.
//
// Usage: O2P_API=http://127.0.0.1:5050 O2P_UI_URL=http://127.0.0.1:5252 O2P_ADMIN_PW=... node scripts/verify-dashboard.mjs
//   (without O2P_UI_URL only the API part runs)
import fs from 'node:fs/promises';
import path from 'node:path';
import { execFileSync, spawn } from 'node:child_process';
import { pathToFileURL } from 'node:url';

const API = process.env.O2P_API;
const UI = process.env.O2P_UI_URL;
const PASSWORD = process.env.O2P_ADMIN_PW;
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
const CHROME = process.env.CHROME_PATH || 'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe';
const OUT = path.resolve(process.cwd(), 'artifacts', 'dashboard');
const OWNER = ORA_USER.toUpperCase();
const SCHEMA = 'dash_test';

if (!API || !PASSWORD) {
  console.log('Set O2P_API and O2P_ADMIN_PW - this script will not guess a target.');
  process.exit(2);
}
for (const u of [API, UI].filter(Boolean)) {
  if (['5000', '5151', '3051', '3052'].includes(new URL(u).port)) {
    console.log(`Refusing to run against ${u}: that is a normal dev/deploy port.`);
    process.exit(2);
  }
}
if (PG_PORT === 5432) { console.log('Refusing to touch a database on port 5432.'); process.exit(2); }

let failures = 0;
const ok = (m) => console.log('  \u2713 ' + m);
const bad = (m) => { failures++; console.log('  \u2717 ' + m); };
const check = (c, m) => (c ? ok(m) : bad(m));
const section = (m) => console.log('\n[' + m + ']');
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

async function call(tok, method, url, body) {
  const headers = { 'Content-Type': 'application/json' };
  if (tok) headers.Authorization = `Bearer ${tok}`;
  const res = await fetch(`${API}${url}`, { method, headers, body: body === undefined ? undefined : JSON.stringify(body) });
  const text = await res.text();
  let data = null;
  if (text) { try { data = JSON.parse(text); } catch { data = text; } }
  return { status: res.status, data };
}
let token = null;
const api = (method, url, body) => call(token, method, url, body);

const ora = (sql) => execFileSync('docker', ['exec', '-i', ORA_CONTAINER, 'sqlplus', '-s', `${ORA_USER}/${ORA_PASSWORD}@localhost:1521/${ORA_SERVICE}`],
  { input: `SET HEADING OFF FEEDBACK OFF PAGESIZE 0\nWHENEVER SQLERROR EXIT FAILURE\n${sql}\nEXIT\n`, encoding: 'utf8' }).trim();
const pg = (sql) => execFileSync('docker', ['exec', '-i', PG_CONTAINER, 'psql', '-U', PG_USER, '-d', PG_DB, '-A', '-t', '-c', sql], { encoding: 'utf8' }).trim();
function pgBackground(sql) {
  const child = spawn('docker', ['exec', '-i', PG_CONTAINER, 'psql', '-U', PG_USER, '-d', PG_DB, '-q'], { stdio: ['pipe', 'ignore', 'ignore'] });
  child.stdin.end(sql);
  return new Promise((resolve) => child.on('exit', resolve));
}

const SOURCE_TABLES = ['DB_MATCH', 'DB_MISSING', 'DB_EXTRA', 'DB_ONLYSRC', 'DB_GONE', 'DB_SLOW'];
const dropOracle = () => ora(`BEGIN
  FOR t IN (SELECT table_name FROM user_tables WHERE table_name IN (${SOURCE_TABLES.map((t) => `'${t}'`).join(', ')})) LOOP
    EXECUTE IMMEDIATE 'DROP TABLE ' || t.table_name || ' PURGE';
  END LOOP;
END;
/`);

console.log('\n=== DASHBOARD ROW COUNT COMPARISON ===');

// ---- seed --------------------------------------------------------------------------------------
section('seed');
let r = await api('POST', '/api/v1/auth/login', { username: 'admin', password: PASSWORD });
if (r.status !== 200) { console.log(`login failed: ${r.status} ${JSON.stringify(r.data)}`); process.exit(1); }
token = r.data.token;
const authState = r.data;
ok('logged in once');

dropOracle();
ora(`
CREATE TABLE db_match (id NUMBER PRIMARY KEY);
CREATE TABLE db_missing (id NUMBER PRIMARY KEY);
CREATE TABLE db_extra (id NUMBER PRIMARY KEY);
CREATE TABLE db_onlysrc (id NUMBER PRIMARY KEY);
CREATE TABLE db_gone (id NUMBER PRIMARY KEY);
CREATE TABLE db_slow (id NUMBER PRIMARY KEY);
INSERT INTO db_match SELECT LEVEL FROM dual CONNECT BY LEVEL <= 100;
INSERT INTO db_missing SELECT LEVEL FROM dual CONNECT BY LEVEL <= 50;
INSERT INTO db_extra SELECT LEVEL FROM dual CONNECT BY LEVEL <= 10;
INSERT INTO db_onlysrc SELECT LEVEL FROM dual CONNECT BY LEVEL <= 5;
INSERT INTO db_gone SELECT LEVEL FROM dual CONNECT BY LEVEL <= 3;
INSERT INTO db_slow SELECT LEVEL FROM dual CONNECT BY LEVEL <= 20;
COMMIT;`);
pg(`DROP SCHEMA IF EXISTS ${SCHEMA} CASCADE; CREATE SCHEMA ${SCHEMA};
CREATE TABLE ${SCHEMA}.db_match AS SELECT g AS id FROM generate_series(1, 100) g;
CREATE TABLE ${SCHEMA}.db_missing AS SELECT g AS id FROM generate_series(1, 40) g;
CREATE TABLE ${SCHEMA}.db_extra AS SELECT g AS id FROM generate_series(1, 12) g;
CREATE TABLE ${SCHEMA}.db_onlydst AS SELECT g AS id FROM generate_series(1, 7) g;
CREATE TABLE ${SCHEMA}.db_slow AS SELECT g AS id FROM generate_series(1, 20) g;
CREATE TABLE ${SCHEMA}.db_parted (id int) PARTITION BY RANGE (id);
CREATE TABLE ${SCHEMA}.db_parted_1 PARTITION OF ${SCHEMA}.db_parted FOR VALUES FROM (0) TO (100);
INSERT INTO ${SCHEMA}.db_parted SELECT g FROM generate_series(1, 30) g;
CREATE TABLE ${SCHEMA}._o2p_chunk_log (x int);`);
ok('Oracle: 6 tables of known sizes; PostgreSQL: matching, short, long, extra and partitioned tables, plus O2P\'s fence table');

const stamp = Date.now().toString().slice(-6);
r = await api('POST', '/api/v1/connections', { name: `dash-ora-${stamp}`, kind: 'oracle', host: ORA_HOST, port: ORA_PORT, serviceOrDb: ORA_SERVICE, username: ORA_USER, password: ORA_PASSWORD });
const oraId = r.data?.id;
r = await api('POST', '/api/v1/connections', { name: `dash-pg-${stamp}`, kind: 'postgres', host: PG_HOST, port: PG_PORT, serviceOrDb: PG_DB, username: PG_USER, password: PG_PASSWORD });
const pgId = r.data?.id;
check(!!(oraId && pgId), 'connections to the real databases');

const compare = async (tok = token) => (await call(tok, 'GET',
  `/api/v1/row-counts?sourceConnectionId=${oraId}&sourceSchema=${OWNER}&targetConnectionId=${pgId}&targetSchema=${SCHEMA}`)).data;
const pairOf = (data, key) => data?.pairs?.find((p) => p.key === key);

async function syncSide(id, schema) {
  const listed = await api('POST', `/api/v1/connections/${id}/row-counts/tables?schema=${encodeURIComponent(schema)}`);
  const tables = listed.data?.tables ?? [];
  for (const t of tables) {
    await api('POST', `/api/v1/connections/${id}/row-counts/count?schema=${encodeURIComponent(schema)}&table=${encodeURIComponent(t)}`);
  }
  return { status: listed.status, tables };
}

// ---- table lists ---------------------------------------------------------------------------------
section('table lists');
r = await compare();
check(r?.pairs?.length === 0, 'before any sync there is nothing to show');

const src = await syncSide(oraId, ORA_USER.toLowerCase()); // lower case on purpose: the owner is upper-cased
check(src.status === 200 && SOURCE_TABLES.every((t) => src.tables.includes(t)), `Sync Source listed the Oracle tables (${src.tables.length})`);
const dst = await syncSide(pgId, SCHEMA);
check(dst.status === 200, `Sync Destination listed ${dst.tables.join(', ')}`);
check(!dst.tables.includes('_o2p_chunk_log'), "O2P's own fence table is left out");
check(dst.tables.includes('db_parted') && !dst.tables.includes('db_parted_1'), 'a partitioned table is listed once, its partition is not');

// ---- comparison ----------------------------------------------------------------------------------
section('comparison');
r = await compare();
const expect = [
  ['DB_MATCH', 'match', 100, 100, 0],
  ['DB_MISSING', 'missing_rows', 50, 40, -10],
  ['DB_EXTRA', 'extra_rows', 10, 12, 2],
  ['DB_ONLYSRC', 'only_source', 5, null, null],
  ['db_onlydst', 'only_destination', null, 7, null],
  ['db_parted', 'only_destination', null, 30, null],
];
for (const [key, status, s, d, diff] of expect) {
  const p = pairOf(r, key);
  check(p && p.status === status && (p.source?.rows ?? null) === s && (p.destination?.rows ?? null) === d && p.difference === diff,
    `${key}: ${status} (${p?.source?.rows ?? '-'} vs ${p?.destination?.rows ?? '-'}, difference ${p?.difference})`);
}
const m = pairOf(r, 'DB_MATCH');
check(m?.source?.table === 'DB_MATCH' && m?.destination?.table === 'db_match', 'ORDERS-style names pair across case (DB_MATCH ↔ db_match)');
check(m?.source?.countedAt && m?.destination?.countedAt && m?.source?.durationMs != null, 'each count records when it was taken and how long it took');
check(r?.summary?.matching >= 2 && r?.summary?.different === 2, `summary: ${JSON.stringify(r?.summary)}`);

// ---- a table that is dropped disappears; a failed count is an error ---------------------------------
section('changes on the source');
ora('DROP TABLE db_gone PURGE;');
r = await api('POST', `/api/v1/connections/${oraId}/row-counts/tables?schema=${OWNER}`);
check(!(r.data?.tables ?? []).includes('DB_GONE'), 'after the next table-list refresh, a dropped table is gone');
check(!pairOf(await compare(), 'DB_GONE'), 'and it is gone from the comparison');

r = await api('POST', `/api/v1/connections/${oraId}/row-counts/count?schema=${OWNER}&table=NO_SUCH_TABLE`);
check(r.status === 200 && /ORA-00942/.test(r.data?.error ?? ''), `a count that fails comes back as an error on that table ("${r.data?.error}")`);
check(!pairOf(await compare(), 'NO_SUCH_TABLE'), 'and a table that was never listed is not added to the comparison by it');

ora('INSERT INTO db_missing SELECT 1000 + LEVEL FROM dual CONNECT BY LEVEL <= 5;\nCOMMIT;');
r = await api('POST', `/api/v1/connections/${oraId}/row-counts/count?schema=${OWNER}&table=DB_MISSING`);
check(r.data?.rows === 55 && pairOf(await compare(), 'DB_MISSING')?.difference === -15, 'recounting one table updates just that table (55, now 15 missing)');

// ---- permissions ---------------------------------------------------------------------------------
section('a Viewer');
const vname = `dashviewer${stamp}`;
const vpass = 'DashViewer2026!Aa';
r = await api('POST', '/api/v1/users', { username: vname, email: `${vname}@o2p.internal`, displayName: 'Dash Viewer', password: vpass, roles: ['Viewer'], mustChangePassword: false, isActive: true });
const viewerId = r.data?.id;
r = await call(null, 'POST', '/api/v1/auth/login', { username: vname, password: vpass });
const vtoken = r.data?.token;
check(!!vtoken, 'a Viewer can sign in');
const seen = await compare(vtoken);
check(seen?.pairs?.length > 0, 'a Viewer sees the saved comparison');
r = await call(vtoken, 'GET', `/api/v1/row-counts/schemas?connectionId=${pgId}`);
check((r.data?.schemas ?? []).includes(SCHEMA), 'and can pick from the schemas that have saved counts');
r = await call(vtoken, 'POST', `/api/v1/connections/${pgId}/row-counts/count?schema=${SCHEMA}&table=db_match`);
check(r.status === 403, `but cannot count (${r.status})`);
r = await call(vtoken, 'POST', `/api/v1/connections/${pgId}/row-counts/tables?schema=${SCHEMA}`);
check(r.status === 403, `or refresh a table list (${r.status})`);

// ---- summary cards ---------------------------------------------------------------------------------
section('summary cards');
r = await api('GET', '/api/v1/dashboard/summary');
check(r.status === 200 && typeof r.data?.runsInProgress === 'number' && typeof r.data?.mbPerSecond === 'number',
  `the summary answers (${JSON.stringify({ ...r.data, lastRun: r.data?.lastRun ? `#${r.data.lastRun.id}` : null })})`);

// ---- the page ----------------------------------------------------------------------------------------
if (UI) {
  section('the page');
  await fs.mkdir(OUT, { recursive: true });
  const { chromium } = await import(pathToFileURL(path.resolve(process.cwd(), 'web', 'node_modules', 'playwright-core', 'index.mjs')).href);
  const browser = await chromium.launch({ executablePath: CHROME, headless: true, args: ['--no-proxy-server', '--proxy-server=direct://', '--proxy-bypass-list=*'] });
  const context = await browser.newContext({ viewport: { width: 1360, height: 900 } });
  await context.addInitScript(({ token, auth, choice }) => {
    localStorage.setItem('o2p_token', token);
    localStorage.setItem('o2p_auth', JSON.stringify(auth));
    localStorage.setItem('o2p_dashboard_compare', JSON.stringify(choice));
  }, {
    token, auth: authState,
    choice: { source: { connectionId: oraId, schema: OWNER }, destination: { connectionId: pgId, schema: SCHEMA } },
  });
  const page = await context.newPage();
  const errors = [];
  page.on('pageerror', (e) => errors.push(e.message));

  await page.goto(`${UI}/`, { waitUntil: 'networkidle' });
  await page.getByRole('cell', { name: 'db_match', exact: true }).waitFor({ timeout: 15000 });
  check(await page.getByText('Runs in progress').isVisible(), 'the summary cards are shown');
  const row = page.locator('tr', { has: page.getByRole('cell', { name: 'DB_MISSING', exact: true }) });
  check(await row.getByText('Missing rows').isVisible() && await row.getByText('−15').isVisible(), 'a short table shows "Missing rows" and −15');
  check(await page.locator('tr', { has: page.getByRole('cell', { name: 'db_onlydst', exact: true }) }).getByText('Only in destination').isVisible(),
    'a destination-only table is shown as such');
  check(await page.getByText('Difference in paired tables −13').isVisible(),
    'the difference counts only tables on both sides (−15 + 2 = −13), not the source-only ones');
  await page.screenshot({ path: path.join(OUT, '01-dashboard.png'), fullPage: true });

  await page.getByRole('button', { name: /Differences/ }).click();
  const shown = await page.locator('.dash-table tbody tr').count();
  check(shown === 2, `"Differences" narrows the list to the two tables that differ (${shown})`);
  await page.getByRole('button', { name: /^All/ }).click();
  await page.getByLabel('Find a table').fill('extra');
  check(await page.locator('.dash-table tbody tr').count() === 1, 'the search box finds a table by name');
  await page.getByLabel('Find a table').fill('');

  // Stop: hold a lock on db_slow so its count waits, press Sync Destination, then Stop.
  const lockHeld = pgBackground(`BEGIN; LOCK TABLE ${SCHEMA}.db_slow IN ACCESS EXCLUSIVE MODE; SELECT pg_sleep(40); COMMIT;`);
  await sleep(1500);
  await page.getByRole('button', { name: 'Sync Destination' }).click();
  await page.getByText(/Counting \d+ of \d+: db_slow/).waitFor({ timeout: 20000 });
  check(true, 'the sync shows which table it is counting ("Counting n of m: db_slow")');
  await page.screenshot({ path: path.join(OUT, '02-syncing.png'), fullPage: true });
  const waiting = () => pg(`SELECT count(*) FROM pg_stat_activity WHERE query ILIKE '%count(*)%db_slow%' AND state = 'active' AND pid <> pg_backend_pid()`);
  check(waiting() === '1', 'the count is waiting on the database');
  await page.getByRole('button', { name: 'Stop' }).click();
  await page.getByText(/Stopped after \d+ of \d+ tables/).waitFor({ timeout: 10000 });
  check(true, 'Stop ends the sync and says how far it got');
  let cancelled = false;
  for (let i = 0; i < 20 && !cancelled; i++) { await sleep(500); cancelled = waiting() === '0'; }
  check(cancelled, 'and the count query was cancelled on the database, not left running');
  await lockHeld;

  await page.getByRole('button', { name: 'Sync Source' }).click();
  await page.getByText(/Counted \d+ tables?/).waitFor({ timeout: 60000 });
  check(true, 'Sync Source runs to the end');

  await page.setViewportSize({ width: 390, height: 844 });
  await page.waitForTimeout(300);
  const overflow = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
  check(overflow <= 0, `no sideways page scroll at phone width (${overflow}px)`);
  await page.screenshot({ path: path.join(OUT, '03-phone.png'), fullPage: true });

  check(errors.length === 0, `no page errors${errors.length ? `: ${errors.join('; ')}` : ''}`);
  await browser.close();
  console.log(`  screenshots -> ${OUT}`);
}

// ---- tidy up ---------------------------------------------------------------------------------------
section('tidy up');
if (viewerId) await api('DELETE', `/api/v1/users/${viewerId}`);
await api('DELETE', `/api/v1/connections/${oraId}`);
await api('DELETE', `/api/v1/connections/${pgId}`);
check(pg(`SELECT count(*) FROM o2p.table_row_counts WHERE "ConnectionId" IN (${oraId}, ${pgId})`) === '0', 'deleting a database removes its saved counts');
dropOracle();
pg(`DROP SCHEMA IF EXISTS ${SCHEMA} CASCADE;`);
ok('test tables dropped on both sides');

console.log(failures === 0 ? '\nALL DASHBOARD CHECKS PASSED' : `\n${failures} CHECK(S) FAILED`);
process.exit(failures === 0 ? 0 : 1);
