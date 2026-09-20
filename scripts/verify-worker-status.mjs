// Verifies the "the copier is not running" safeguards against a real Worker process:
//   - the API reports whether a Worker is alive, and whether more than one is
//   - the Runs page says so, instead of a silent "Waiting"
//   - a run with no tables is refused, and one already queued is failed by the Worker, not left hanging
//   - cancelling a run that has not started works even when no Worker is running
//
// It starts and stops its OWN Worker processes, so nothing here depends on (or touches) yours.
//
// SAFETY: seeds data, inserts rows directly into the metadata database and starts Worker processes,
// so it must only ever be pointed at a throwaway API + database. It refuses the usual dev ports.
//
// Usage: O2P_API=http://127.0.0.1:5050 O2P_UI_URL=http://127.0.0.1:5252 O2P_ADMIN_PW=... \
//        O2P_WORKER_EXE=<path to a published O2P.Worker.exe> node scripts/verify-worker-status.mjs
import fs from 'node:fs/promises';
import path from 'node:path';
import { spawn, execFileSync } from 'node:child_process';
import { pathToFileURL } from 'node:url';

const { chromium } = await import(
  pathToFileURL(path.resolve(process.cwd(), 'web', 'node_modules', 'playwright-core', 'index.mjs')).href
);

const API = process.env.O2P_API;
const UI = process.env.O2P_UI_URL;
const PASSWORD = process.env.O2P_ADMIN_PW;
const WORKER_EXE = process.env.O2P_WORKER_EXE;
const PG_CONTAINER = process.env.O2P_PG_CONTAINER || 'dataflow-metadata-pg-1';
const PG_USER = process.env.O2P_PG_USER || 'nonoraclemigrationdb';
const PG_DB = process.env.O2P_PG_DB || 'nonoraclemigrationdb';
const PG_HOST = process.env.O2P_PG_HOST || '127.0.0.1';
const PG_PORT = process.env.O2P_PG_PORT || '7936';
const PG_PASSWORD = process.env.O2P_PG_PASSWORD || 'change-me-local-only';
const CHROME = process.env.CHROME_PATH || 'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe';
const OUT = path.resolve(process.cwd(), 'artifacts', 'worker-status');

if (!API || !UI || !PASSWORD || !WORKER_EXE) {
  console.log('Set O2P_API, O2P_UI_URL, O2P_ADMIN_PW and O2P_WORKER_EXE (all four) - this script will not guess a target.');
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
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

async function waitFor(fn, timeoutMs, everyMs = 1000) {
  const end = Date.now() + timeoutMs;
  let last;
  while (Date.now() < end) {
    last = await fn();
    if (last) return last;
    await sleep(everyMs);
  }
  return false;
}

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

function psql(sql) {
  return execFileSync('docker', ['exec', '-i', PG_CONTAINER, 'psql', '-U', PG_USER, '-d', PG_DB, '-A', '-t', '-c', sql], { encoding: 'utf8' }).trim();
}

const workers = [];
function startWorker() {
  const child = spawn(WORKER_EXE, [], {
    cwd: path.dirname(WORKER_EXE),
    env: {
      ...process.env,
      DOTNET_ENVIRONMENT: 'Development',
      ConnectionStrings__MetadataDb: `Host=${PG_HOST};Port=${PG_PORT};Database=${PG_DB};Username=${PG_USER};Password=${PG_PASSWORD}`,
    },
    stdio: 'ignore',
    windowsHide: true,
  });
  workers.push(child);
  return child;
}
const stopAllWorkers = () => { for (const w of workers) { try { w.kill(); } catch { /* already gone */ } } };
process.on('exit', stopAllWorkers);

const status = async () => (await api('GET', '/api/v1/workers/status')).data;

console.log('\n=== COPIER (WORKER) SAFEGUARDS CHECK ===');

// ---- seed ---------------------------------------------------------------------------------
section('seed');
let r = await api('POST', '/api/v1/auth/login', { username: 'admin', password: PASSWORD });
if (r.status !== 200) { console.log(`login failed: ${r.status} ${JSON.stringify(r.data)}`); process.exit(1); }
token = r.data.token;
const authState = r.data;
ok('logged in once');

const stamp = Date.now().toString().slice(-6);
r = await api('POST', '/api/v1/connections', { name: `ws-ora-${stamp}`, kind: 'oracle', host: 'mock', port: 1521, serviceOrDb: 'X', username: 'u', password: 'p' });
const oraId = r.data?.id;
r = await api('POST', '/api/v1/connections', { name: `ws-pg-${stamp}`, kind: 'postgres', host: 'mock', port: 5432, serviceOrDb: 'x', username: 'x', password: 'x' });
const pgId = r.data?.id;
r = await api('POST', '/api/v1/applications', { name: `Worker check ${stamp}`, description: 'temporary' });
const appId = r.data?.id;
await api('PUT', `/api/v1/applications/${appId}`, {
  id: appId, name: `Worker check ${stamp}`, description: 'temporary',
  connections: [
    { applicationId: appId, slot: 'oracle_test', connectionId: oraId },
    { applicationId: appId, slot: 'pg_test', connectionId: pgId },
  ],
});
await api('POST', `/api/v1/connections/${oraId}/discovery/refresh?owner=APP`, { tableNames: [] });
r = await api('POST', `/api/v1/applications/${appId}/manifests/generate?connectionId=${oraId}&owner=APP&version=1.0`, {});
const goodManifest = r.data?.id;

// A selection with tables saved but NONE ticked - the shape that produced the empty runs.
r = await api('POST', `/api/v1/applications/${appId}/manifests`, { name: 'nothing ticked', version: 1 });
const emptyManifest = r.data?.id;
await api('PUT', `/api/v1/manifests/${emptyManifest}/tables`, [
  { owner: 'APP', tableName: 'CUSTOMERS', included: false, whereClause: null, estRows: 1, estBytes: 1, sizeIsEstimate: false, hasLobs: false, isPartitioned: false, isIot: false, columns: [] },
]);
check(!!(appId && goodManifest && emptyManifest), 'seeded a migration with a normal selection and one with nothing ticked');

const newJob = async (manifestId) => {
  const c = await api('POST', '/api/v1/jobs', { applicationId: appId, manifestId, sourceSlot: 'oracle_test', targetSlot: 'pg_test', targetSchema: 'public' });
  return c;
};
const launch = (id) => api('POST', `/api/v1/jobs/${id}/launch`, {});
const getJob = async (id) => (await api('GET', `/api/v1/jobs/${id}`)).data;

// ============================================================================================
section('phase 1 - no Worker running');
// Scratch database only: drop rows left by a previously killed Worker so this run starts from a known state.
psql('DELETE FROM o2p.worker_heartbeats;');
check(psql('SELECT count(*) FROM o2p.worker_heartbeats;') === '0', 'starting with no Worker registered');

let s = await status();
check(s?.running === false && s?.count === 0, `the API reports no copier (running=${s?.running}, count=${s?.count})`);

// Empty runs are refused.
r = await newJob(emptyManifest);
check(r.status === 400, `starting a run with nothing ticked is refused (HTTP ${r.status})`);
check(/no tables ticked/i.test(String(r.data)), `and says why: "${String(r.data).slice(0, 70)}..."`);
check(psql(`SELECT count(*) FROM o2p.job_runs WHERE "ManifestId" = ${emptyManifest};`) === '0', 'and nothing was created behind the refusal');

// A normal run queues and waits - there is nobody to start it.
r = await newJob(goodManifest);
const runA = r.data?.id;
r = await launch(runA);
check(r.status === 200, 'a normal run launches (HTTP 200)');
await sleep(3000);
check((await getJob(runA)).status === 'Queued', 'and just waits - nothing is processing it');

// A pre-existing empty run, as left behind before the rule existed (runs 3 and 4 in the report).
const legacy = Number(psql(`INSERT INTO o2p.job_runs ("ApplicationId","ManifestId","Status","SourceSlot","TargetSlot","TargetSchema","CreatedAt") VALUES (${appId},${emptyManifest},'Queued','oracle_test','pg_test','public',now()) RETURNING "Id";`).split('\n')[0]);
check(legacy > 0, `inserted an empty queued run like the ones already in your database (run #${legacy})`);

// Cancel with NO Worker running: this is the bug where the click did nothing.
r = await api('POST', `/api/v1/jobs/${runA}/commands`, { command: 'cancel', scope: 'job' });
check(r.status === 202, 'cancel is accepted');
const cancelled = await getJob(runA);
check(cancelled.status === 'Cancelled', `and takes effect at once, with no Worker (status ${cancelled.status})`);
check((cancelled.tableRuns ?? []).length > 0 && cancelled.tableRuns.every((t) => t.status === 'Cancelled'), 'its tables are cancelled too');
check(psql(`SELECT "ProcessedAt" IS NOT NULL FROM o2p."JobCommands" WHERE "JobRunId" = ${runA} AND "Command" = 'cancel';`) === 't', 'the command is recorded as already handled, so a later Worker does not repeat it');

// ---- browser ------------------------------------------------------------------------------
const browser = await chromium.launch({
  executablePath: CHROME, headless: true,
  args: ['--no-proxy-server', '--proxy-server=direct://', '--proxy-bypass-list=*'],
});
const context = await browser.newContext({ viewport: { width: 1440, height: 900 } });
await context.addInitScript(({ token, auth }) => {
  localStorage.setItem('o2p_token', token);
  localStorage.setItem('o2p_auth', JSON.stringify(auth));
}, { token, auth: authState });
const page = await context.newPage();
const pageErrors = [];
page.on('pageerror', (e) => pageErrors.push(e.message));
page.on('dialog', (d) => d.accept());

// One more waiting run, to cancel from the UI itself.
r = await newJob(goodManifest);
const runB = r.data?.id;
await launch(runB);

await page.goto(`${UI}/jobs`, { waitUntil: 'networkidle' });
await page.getByText('Copier not running').waitFor({ timeout: 15000 });
check(true, 'the Runs page shows a red "Copier not running" indicator');
const banner = page.getByRole('alert');
await banner.waitFor({ timeout: 10000 });
const bannerText = (await banner.innerText()).replace(/\s+/g, ' ');
check(/copier is not running/i.test(bannerText), `and a banner explains why runs are waiting: "${bannerText.slice(0, 80)}..."`);
check(/2 runs are waiting/.test(bannerText), 'counting the runs it affects (2)');
await page.screenshot({ path: path.join(OUT, '01-no-worker.png') });

// Cancel from the UI with no Worker.
await page.locator('div.card').filter({ hasText: `Run #${runB}` }).filter({ has: page.getByRole('button', { name: 'Cancel run' }) }).last()
  .getByRole('button', { name: 'Cancel run' }).click();
const cancelledInUi = await waitFor(async () => (await getJob(runB)).status === 'Cancelled', 10000, 500);
check(!!cancelledInUi, 'clicking "Cancel run" in the UI works with no Worker running');

// ============================================================================================
section('phase 2 - a Worker starts');
startWorker();
const up = await waitFor(async () => (await status())?.running === true, 30000, 1000);
check(!!up, 'the API notices the Worker within seconds');
s = await status();
check(s.count === 1 && s.multiple === false, `and reports exactly one (count=${s.count})`);
check(s.workers?.[0]?.processId > 0 && !!s.workers?.[0]?.host, `with its host and process id (${s.workers?.[0]?.host}, pid ${s.workers?.[0]?.processId})`);

// The empty run is failed by the Worker instead of hanging at Running.
const failed = await waitFor(async () => (await getJob(legacy)).status === 'Failed', 25000, 1000);
check(!!failed, 'the Worker fails the empty run instead of leaving it hanging');
check(psql(`SELECT count(*) FROM o2p.run_events WHERE "JobRunId" = ${legacy} AND "Event" = 'job.failed_no_tables';`) === '1', 'and records why');

await page.reload({ waitUntil: 'networkidle' });
await page.getByText('Copier running', { exact: true }).waitFor({ timeout: 15000 });
check(true, 'the Runs page now shows a green "Copier running" indicator');
check((await page.getByRole('alert').count()) === 0, 'and the warning banner is gone');
await page.screenshot({ path: path.join(OUT, '02-worker-running.png') });

await page.goto(`${UI}/jobs/${legacy}`, { waitUntil: 'networkidle' });
await page.getByText(/No tables were selected for this run/).waitFor({ timeout: 10000 });
check(true, 'the failed empty run explains itself on its own page');

// ============================================================================================
section('phase 3 - a second Worker');
startWorker();
const two = await waitFor(async () => (await status())?.count === 2, 30000, 1000);
check(!!two, 'a second Worker is detected');
s = await status();
check(s.multiple === true, 'and flagged as a problem (multiple=true)');

await page.goto(`${UI}/jobs`, { waitUntil: 'networkidle' });
const warn = page.getByRole('alert');
await warn.waitFor({ timeout: 15000 });
check(/2 copiers are running/i.test(await warn.innerText()), 'the Runs page warns that two copiers are running');
await page.screenshot({ path: path.join(OUT, '03-two-workers.png') });

// Stop both the way the product itself does: the "Restart Copier" signal, which makes every Worker
// shut down cleanly. A Worker that stops cleanly should remove its own heartbeat at once, rather than
// leave the screen to wait out the 45s staleness window. (Not taskkill: that sends a close signal to
// every process sharing the console, including the API.)
const t0 = Date.now();
psql(`INSERT INTO o2p.worker_control ("Id","RestartRequestedAt") VALUES (1, now()) ON CONFLICT ("Id") DO UPDATE SET "RestartRequestedAt" = now();`);
const allGone = await waitFor(async () => (await status())?.count === 0, 30000, 500);
const took = ((Date.now() - t0) / 1000).toFixed(1);
check(!!allGone && Number(took) < 40, `Workers that are asked to stop remove their own heartbeat promptly (${took}s, well inside the 45s staleness window)`);
check(psql('SELECT count(*) FROM o2p.worker_heartbeats;') === '0', 'leaving no rows behind');

// ============================================================================================
section('phase 4 - a Worker dies without warning');
// A hard kill cannot clean up after itself, so this is caught by the staleness window instead.
// Started after the restart signal above, so it does not obey it.
await sleep(1500);
const survivor = startWorker();
const back = await waitFor(async () => (await status())?.running === true, 30000, 1000);
check(!!back, 'a fresh Worker registers again');
const killedAt = Date.now();
survivor.kill();
check(true, 'killed it outright (no chance to say goodbye)');
const stillThere = (await status())?.running;
const gone = await waitFor(async () => (await status())?.running === false, 75000, 2000);
const goneAfter = ((Date.now() - killedAt) / 1000).toFixed(0);
check(!!gone, `it is reported as gone once its heartbeat goes stale (after ~${goneAfter}s)`);
check(stillThere === true, 'and not before: it still counted as alive immediately after the kill');

await page.goto(`${UI}/jobs`, { waitUntil: 'networkidle' });
await page.getByText('Copier not running').waitFor({ timeout: 15000 });
check(true, 'so the Runs page is back to "Copier not running"');

check(pageErrors.length === 0, `no uncaught page errors${pageErrors.length ? ': ' + pageErrors[0] : ''}`);

// ---- cleanup ------------------------------------------------------------------------------
stopAllWorkers();
await api('DELETE', `/api/v1/applications/${appId}`);
for (const id of [oraId, pgId]) await api('DELETE', `/api/v1/connections/${id}`);
await browser.close();

console.log(`\nscreenshots -> ${OUT}`);
console.log(`\n=== ${failures === 0 ? 'ALL CHECKS PASSED' : failures + ' CHECK(S) FAILED'} ===\n`);
process.exit(failures ? 1 : 0);
