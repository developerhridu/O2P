// Verifies that a run can be deleted: the right runs, all of its history and nothing else.
//   - a finished run is removed together with its tables, batches, checks, rejected rows, events, logs and graphs
//   - another run's history is left alone
//   - a waiting / running / paused run, or one with a batch still finishing, is refused and left intact
//   - the Runs page and the run page both offer Delete and it works there
//
// SAFETY: seeds data and inserts rows directly into the metadata database, so it must only ever be
// pointed at a throwaway API + database. It refuses the usual dev ports and port 5432.
//
// Usage: O2P_API=http://127.0.0.1:5050 O2P_UI_URL=http://127.0.0.1:5252 O2P_ADMIN_PW=... node scripts/verify-run-delete.mjs
import fs from 'node:fs/promises';
import path from 'node:path';
import { execFileSync } from 'node:child_process';
import { pathToFileURL } from 'node:url';

const { chromium } = await import(
  pathToFileURL(path.resolve(process.cwd(), 'web', 'node_modules', 'playwright-core', 'index.mjs')).href
);

const API = process.env.O2P_API;
const UI = process.env.O2P_UI_URL;
const PASSWORD = process.env.O2P_ADMIN_PW;
const PG_CONTAINER = process.env.O2P_PG_CONTAINER || 'dataflow-metadata-pg-1';
const PG_USER = process.env.O2P_PG_USER || 'nonoraclemigrationdb';
const PG_DB = process.env.O2P_PG_DB || 'nonoraclemigrationdb';
const PG_PORT = process.env.O2P_PG_PORT || '7936';
const CHROME = process.env.CHROME_PATH || 'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe';
const OUT = path.resolve(process.cwd(), 'artifacts', 'run-delete');

if (!API || !UI || !PASSWORD) {
  console.log('Set O2P_API, O2P_UI_URL and O2P_ADMIN_PW (all three) - this script will not guess a target.');
  process.exit(2);
}
for (const u of [API, UI]) {
  const port = new URL(u).port;
  if (['5000', '5151', '3051', '3052'].includes(port)) {
    console.log(`Refusing to run against port ${port}: that is a normal dev/deploy port.`);
    process.exit(2);
  }
}
if (PG_PORT === '5432') {
  console.log('Refusing to touch a database on port 5432: that is not the throwaway one.');
  process.exit(2);
}
await fs.mkdir(OUT, { recursive: true });

let failures = 0;
const ok = (m) => console.log('  \u2713 ' + m);
const bad = (m) => { failures++; console.log('  \u2717 ' + m); };
const check = (c, m) => (c ? ok(m) : bad(m));
const section = (m) => console.log('\n[' + m + ']');

let token = null;
async function api(method, url, body, withAuth = true) {
  const headers = { 'Content-Type': 'application/json' };
  if (token && withAuth) headers.Authorization = `Bearer ${token}`;
  const res = await fetch(`${API}${url}`, { method, headers, body: body === undefined ? undefined : JSON.stringify(body) });
  const text = await res.text();
  let data = null;
  if (text) { try { data = JSON.parse(text); } catch { data = text; } }
  return { status: res.status, data };
}
function psql(sql) {
  return execFileSync('docker', ['exec', '-i', PG_CONTAINER, 'psql', '-U', PG_USER, '-d', PG_DB, '-A', '-t', '-c', sql], { encoding: 'utf8' }).trim();
}

// Everything that can hang off one run, counted in one number per kind.
const footprint = (jobId) => ({
  run: Number(psql(`SELECT count(*) FROM o2p.job_runs WHERE "Id" = ${jobId};`)),
  tables: Number(psql(`SELECT count(*) FROM o2p.table_runs WHERE "JobRunId" = ${jobId};`)),
  batches: Number(psql(`SELECT count(*) FROM o2p.chunk_logs c JOIN o2p.table_runs t ON t."Id" = c."TableRunId" WHERE t."JobRunId" = ${jobId};`)),
  checks: Number(psql(`SELECT count(*) FROM o2p."ValidationResults" v JOIN o2p.table_runs t ON t."Id" = v."TableRunId" WHERE t."JobRunId" = ${jobId};`)),
  rejects: Number(psql(`SELECT count(*) FROM o2p.row_rejects r JOIN o2p.table_runs t ON t."Id" = r."TableRunId" WHERE t."JobRunId" = ${jobId};`)),
  graphs: Number(psql(`SELECT count(*) FROM o2p."MetricSamples" WHERE "JobRunId" = ${jobId};`)),
  commands: Number(psql(`SELECT count(*) FROM o2p."JobCommands" WHERE "JobRunId" = ${jobId};`)),
  events: Number(psql(`SELECT count(*) FROM o2p.run_events WHERE "JobRunId" = ${jobId};`)),
  logs: Number(psql(`SELECT count(*) FROM o2p.run_logs WHERE "JobRunId" = ${jobId};`)),
});
const total = (f) => Object.values(f).reduce((a, b) => a + b, 0);

console.log('\n=== RUN DELETE CHECK ===');

section('seed');
let r = await api('POST', '/api/v1/auth/login', { username: 'admin', password: PASSWORD });
if (r.status !== 200) { console.log(`login failed: ${r.status} ${JSON.stringify(r.data)}`); process.exit(1); }
token = r.data.token;
const authState = r.data;
ok('logged in once');

const stamp = Date.now().toString().slice(-6);
r = await api('POST', '/api/v1/connections', { name: `rd-ora-${stamp}`, kind: 'oracle', host: 'mock', port: 1521, serviceOrDb: 'X', username: 'u', password: 'p' });
const oraId = r.data?.id;
r = await api('POST', '/api/v1/connections', { name: `rd-pg-${stamp}`, kind: 'postgres', host: 'mock', port: 5432, serviceOrDb: 'x', username: 'x', password: 'x' });
const pgId = r.data?.id;
r = await api('POST', '/api/v1/applications', { name: `Run delete ${stamp}`, description: 'temporary' });
const appId = r.data?.id;
await api('PUT', `/api/v1/applications/${appId}`, {
  id: appId, name: `Run delete ${stamp}`, description: 'temporary',
  connections: [
    { applicationId: appId, slot: 'oracle_test', connectionId: oraId },
    { applicationId: appId, slot: 'pg_test', connectionId: pgId },
  ],
});
await api('POST', `/api/v1/connections/${oraId}/discovery/refresh?owner=APP`, { tableNames: [] });
r = await api('POST', `/api/v1/applications/${appId}/manifests/generate?connectionId=${oraId}&owner=APP&version=1.0`, {});
const manifestId = r.data?.id;
check(!!(appId && manifestId), 'seeded a migration with a table selection');

// A run with a full set of history attached, made through the API and topped up directly in the database.
async function makeRun(label, status) {
  const c = await api('POST', '/api/v1/jobs', { applicationId: appId, manifestId, sourceSlot: 'oracle_test', targetSlot: 'pg_test', targetSchema: 'public' });
  const id = c.data?.id;
  if (!id) throw new Error(`could not create run ${label}: ${c.status} ${JSON.stringify(c.data)}`);
  const tr = Number(psql(`SELECT min("Id") FROM o2p.table_runs WHERE "JobRunId" = ${id};`));
  psql(`INSERT INTO o2p.chunk_logs ("TableRunId","ChunkIndex","StartRowId","EndRowId","Status","AttemptCount","RowsMigrated","BytesMigrated") VALUES (${tr},0,'a','b','Done',1,10,10);`);
  psql(`INSERT INTO o2p."ValidationResults" ("TableRunId","CheckKind","SourceValue","TargetValue","Passed") VALUES (${tr},'RowCount','10','10',true);`);
  psql(`INSERT INTO o2p.row_rejects ("TableRunId","Reason","CreatedAt") VALUES (${tr},'bad value',now());`);
  psql(`INSERT INTO o2p."MetricSamples" ("JobId","JobRunId","TableRunId","Timestamp","RowsPerSecond","MbPerSecond","ActiveChunkWorkers","OracleSessions") VALUES (${id},${id},${tr},now(),1,1,1,1);`);
  psql(`INSERT INTO o2p."JobCommands" ("JobRunId","Scope","Command","IssuedAt","ProcessedAt") VALUES (${id},'job','pause',now(),now());`);
  psql(`INSERT INTO o2p.run_events ("JobRunId","Actor","Event","At") VALUES (${id},'test','job.test',now());`);
  psql(`INSERT INTO o2p.run_logs ("JobRunId","Timestamp","Level","Source","Message") VALUES (${id},now(),'Information','test','hello');`);
  psql(`UPDATE o2p.job_runs SET "Status" = '${status}' WHERE "Id" = ${id};`);
  return id;
}

const finished = await makeRun('finished', 'Failed');
const neighbour = await makeRun('neighbour', 'Completed');
const waiting = await makeRun('waiting', 'Queued');
const running = await makeRun('running', 'Running');
const paused = await makeRun('paused', 'Paused');
const finishing = await makeRun('finishing', 'Cancelled');
psql(`UPDATE o2p.chunk_logs SET "Status" = 'Running' WHERE "TableRunId" IN (SELECT "Id" FROM o2p.table_runs WHERE "JobRunId" = ${finishing});`);
const draft = (await api('POST', '/api/v1/jobs', { applicationId: appId, manifestId, sourceSlot: 'oracle_test', targetSlot: 'pg_test', targetSchema: 'public' })).data?.id;

const before = footprint(finished);
check(Object.values(before).every((n) => n >= 1), `the run to delete has history of every kind (${JSON.stringify(before)})`);
const neighbourBefore = footprint(neighbour);

// ============================================================================================
section('API');
r = await api('DELETE', `/api/v1/jobs/${finished}`, undefined, false);
check(r.status === 401, `deleting without signing in is refused (HTTP ${r.status})`);
check(total(footprint(finished)) === total(before), 'and nothing was removed');

for (const [label, id] of [['waiting', waiting], ['running', running], ['paused', paused]]) {
  const f = footprint(id);
  r = await api('DELETE', `/api/v1/jobs/${id}`);
  check(r.status === 409, `a ${label} run cannot be deleted (HTTP ${r.status})`);
  if (label === 'waiting') check(/cancel it first/i.test(String(r.data)), `and it says what to do: "${String(r.data).slice(0, 60)}..."`);
  check(total(footprint(id)) === total(f), `the ${label} run is left intact`);
}
{
  const f = footprint(finishing);
  r = await api('DELETE', `/api/v1/jobs/${finishing}`);
  check(r.status === 409, `a run with a batch still finishing is refused (HTTP ${r.status})`);
  check(/still finishing/i.test(String(r.data)), `and says why: "${String(r.data).slice(0, 60)}..."`);
  check(total(footprint(finishing)) === total(f), 'and is left intact');
}

r = await api('DELETE', `/api/v1/jobs/${finished}`);
check(r.status === 204, `a finished run is deleted (HTTP ${r.status})`);
const after = footprint(finished);
check(total(after) === 0, `every trace of it is gone (${JSON.stringify(after)})`);
check(JSON.stringify(footprint(neighbour)) === JSON.stringify(neighbourBefore), "another run's history is untouched");
r = await api('GET', `/api/v1/jobs/${finished}`);
check(r.status === 404, `it no longer opens (HTTP ${r.status})`);
r = await api('DELETE', `/api/v1/jobs/${finished}`);
check(r.status === 404, `deleting it again says not found (HTTP ${r.status})`);
r = await api('DELETE', '/api/v1/jobs/999999999');
check(r.status === 404, `deleting a run that never existed says not found (HTTP ${r.status})`);

// A batch that has settled no longer blocks deletion.
psql(`UPDATE o2p.chunk_logs SET "Status" = 'Done' WHERE "TableRunId" IN (SELECT "Id" FROM o2p.table_runs WHERE "JobRunId" = ${finishing});`);
r = await api('DELETE', `/api/v1/jobs/${finishing}`);
check(r.status === 204, `once its batch has settled, the cancelled run can be deleted (HTTP ${r.status})`);
check(total(footprint(finishing)) === 0, 'and its history goes with it');

// ============================================================================================
section('Runs page');
const browser = await chromium.launch({ executablePath: CHROME, headless: true });
const ctx = await browser.newContext({ viewport: { width: 1440, height: 900 } });
await ctx.addInitScript(({ token, auth }) => {
  localStorage.setItem('o2p_token', token);
  localStorage.setItem('o2p_auth', JSON.stringify(auth));
}, { token, auth: authState });
const page = await ctx.newPage();
const pageErrors = [];
page.on('pageerror', (e) => pageErrors.push(String(e)));
const dialogs = [];
page.on('dialog', async (d) => { dialogs.push(d.message()); await d.accept(); });

await page.goto(`${UI}/jobs`, { waitUntil: 'networkidle' });
// A prefix match: the heading also carries the run's kind ("Bulk copy" / "Change copy").
const rowFor = (id) => page.locator('h4', { hasText: new RegExp(`^Run #${id}\\b`) }).locator('xpath=ancestor::div[contains(@class,"card")][1]');
const visible = async (id) => (await page.locator('h4', { hasText: new RegExp(`^Run #${id}\\b`) }).count()) > 0;

check(await visible(neighbour), 'the finished neighbour run is listed');
check((await rowFor(neighbour).getByRole('button', { name: /^delete$/i }).count()) === 1, 'a finished run shows a Delete button');
check((await rowFor(running).getByRole('button', { name: /^delete$/i }).count()) === 0, 'a running run does not');
check((await rowFor(waiting).getByRole('button', { name: /^delete$/i }).count()) === 0, 'a waiting run does not');
check((await rowFor(paused).getByRole('button', { name: /^delete$/i }).count()) === 0, 'a paused run does not');
check((await rowFor(draft).getByRole('button', { name: /^delete$/i }).count()) === 1, 'a draft run (never started) does');
await page.screenshot({ path: path.join(OUT, '01-runs.png') });

await rowFor(neighbour).getByRole('button', { name: /^delete$/i }).click();
await page.waitForTimeout(1500);
check(dialogs.some((d) => d.includes(`Delete run #${neighbour}`) && /not touched/i.test(d)), 'it asks first, and says destination data is untouched');
check(!(await visible(neighbour)), 'the run leaves the list');
check(total(footprint(neighbour)) === 0, 'and its history is gone from the database');

// ============================================================================================
section('Run page');
await page.goto(`${UI}/jobs/${draft}`, { waitUntil: 'networkidle' });
check((await page.getByRole('button', { name: /delete run/i }).count()) === 1, 'a never-started run offers "Delete run"');
await page.screenshot({ path: path.join(OUT, '02-run-page.png') });
await page.getByRole('button', { name: /delete run/i }).click();
await page.waitForURL(`${UI}/jobs`, { timeout: 10000 }).catch(() => {});
check(page.url().endsWith('/jobs'), 'deleting there returns to the Runs list');
check(!(await visible(draft)), 'and the run is gone from it');

await page.goto(`${UI}/jobs/${running}`, { waitUntil: 'networkidle' });
check((await page.getByRole('button', { name: /delete run/i }).count()) === 0, 'a running run offers no "Delete run"');
check((await page.getByRole('button', { name: /cancel run/i }).count()) === 1, 'it still offers "Cancel run"');
check(pageErrors.length === 0, `no uncaught page errors${pageErrors.length ? ': ' + pageErrors[0] : ''}`);
await browser.close();

// ---- tidy up the seed data -----------------------------------------------------------------
for (const id of [waiting, running, paused]) psql(`UPDATE o2p.job_runs SET "Status" = 'Cancelled' WHERE "Id" = ${id};`);
for (const id of [waiting, running, paused]) await api('DELETE', `/api/v1/jobs/${id}`);
await api('DELETE', `/api/v1/applications/${appId}`);
await api('DELETE', `/api/v1/connections/${oraId}`);
await api('DELETE', `/api/v1/connections/${pgId}`);

console.log(failures ? `\n=== ${failures} CHECK(S) FAILED ===` : '\n=== ALL CHECKS PASSED ===');
process.exit(failures ? 1 : 0);
