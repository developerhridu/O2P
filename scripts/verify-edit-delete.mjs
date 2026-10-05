// Verifies renaming and deleting migrations and table selections, through the API and in the browser:
//   - renaming changes only the name (a migration keeps its database choices), and refuses a duplicate
//   - deleting is refused while a run is still in progress, and says which
//   - deleting removes the runs and every trace of them, and nothing belonging to anything else
//   - the migration page, the builder and the migrations list offer it all
//
// SAFETY: seeds data and reads the metadata database directly, so it must only ever be pointed at a
// throwaway API + database. It refuses the usual ports. Run it WITHOUT a Worker: a launched run must stay
// waiting for the "still in progress" checks to mean anything.
//
// Usage: O2P_API=http://127.0.0.1:5050 O2P_UI_URL=http://127.0.0.1:5252 O2P_ADMIN_PW=... node scripts/verify-edit-delete.mjs
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
const CHROME = process.env.CHROME_PATH || 'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe';
const OUT = path.resolve(process.cwd(), 'artifacts', 'edit-delete');

if (!API || !UI || !PASSWORD) {
  console.log('Set O2P_API, O2P_UI_URL and O2P_ADMIN_PW (all three) - this script will not guess a target.');
  process.exit(2);
}
for (const u of [API, UI]) {
  if (['5000', '5151', '3051', '3052'].includes(new URL(u).port)) {
    console.log(`Refusing to run against ${u}: that is a normal dev/deploy port.`);
    process.exit(2);
  }
}
await fs.mkdir(OUT, { recursive: true });

let failures = 0;
const ok = (m) => console.log('  \u2713 ' + m);
const bad = (m) => { failures++; console.log('  \u2717 ' + m); };
const check = (c, m) => (c ? ok(m) : bad(m));
const section = (m) => console.log('\n[' + m + ']');

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
const pg = (sql) => execFileSync('docker', ['exec', '-i', PG_CONTAINER, 'psql', '-U', PG_USER, '-d', PG_DB, '-A', '-t', '-c', sql], { encoding: 'utf8' }).trim();
const count = (sql) => Number(pg(sql));

console.log('\n=== RENAME AND DELETE ===');

section('seed');
let r = await api('POST', '/api/v1/auth/login', { username: 'admin', password: PASSWORD });
if (r.status !== 200) { console.log(`login failed: ${r.status} ${JSON.stringify(r.data)}`); process.exit(1); }
token = r.data.token;
const authState = r.data;
ok('logged in once');

const stamp = Date.now().toString().slice(-6);
r = await api('POST', '/api/v1/connections', { name: `ed-ora-${stamp}`, kind: 'oracle', host: 'mock', port: 1521, serviceOrDb: 'X', username: 'u', password: 'p' });
const oraId = r.data?.id;
r = await api('POST', '/api/v1/connections', { name: `ed-pg-${stamp}`, kind: 'postgres', host: 'mock', port: 5432, serviceOrDb: 'x', username: 'x', password: 'x' });
const pgId = r.data?.id;

async function newMigration(name) {
  const created = await api('POST', '/api/v1/applications', { name, description: 'temporary' });
  const id = created.data?.id;
  await api('PUT', `/api/v1/applications/${id}`, {
    id, name, description: 'temporary',
    connections: [{ applicationId: id, slot: 'oracle_test', connectionId: oraId }, { applicationId: id, slot: 'pg_test', connectionId: pgId }],
  });
  await api('POST', `/api/v1/connections/${oraId}/discovery/refresh?owner=APP`, { tableNames: [] });
  const m1 = (await api('POST', `/api/v1/applications/${id}/manifests/generate?connectionId=${oraId}&owner=APP&version=1.0`, {})).data?.id;
  const m2 = (await api('POST', `/api/v1/applications/${id}/manifests`, { name: 'Second selection', version: 1 })).data?.id;
  return { id, m1, m2 };
}
async function newRun(appId, manifestId, launch) {
  const job = (await api('POST', '/api/v1/jobs', { applicationId: appId, manifestId, sourceSlot: 'oracle_test', targetSlot: 'pg_test', targetSchema: 'public' })).data?.id;
  if (launch) await api('POST', `/api/v1/jobs/${job}/launch`, {});
  // A little history of the kinds that have no cascading link, so leftovers would show.
  pg(`INSERT INTO o2p.run_events ("JobRunId","Actor","Event","At") VALUES (${job},'test','test.event',now());
      INSERT INTO o2p.run_logs ("JobRunId","Timestamp","Level","Source","Message") VALUES (${job},now(),'Information','test','hello');`);
  return job;
}
const trace = (jobs) => {
  const list = jobs.join(',');
  return count(`SELECT (SELECT count(*) FROM o2p.job_runs WHERE "Id" IN (${list})) + (SELECT count(*) FROM o2p.run_events WHERE "JobRunId" IN (${list})) + (SELECT count(*) FROM o2p.run_logs WHERE "JobRunId" IN (${list})) + (SELECT count(*) FROM o2p."JobCommands" WHERE "JobRunId" IN (${list})) + (SELECT count(*) FROM o2p.table_runs WHERE "JobRunId" IN (${list}))`);
};

const A = await newMigration(`Edit A ${stamp}`);
const B = await newMigration(`Edit B ${stamp}`);
check(A.id && A.m1 && A.m2 && B.id, 'seeded two migrations, each with two table selections');

// ---- renaming, through the API --------------------------------------------------------------
section('rename (API)');
r = await api('PATCH', `/api/v1/applications/${A.id}`, { name: `  Renamed A ${stamp}  `, description: 'new words' });
check(r.status === 200 && r.data?.name === `Renamed A ${stamp}`, `a migration can be renamed, trimmed (${r.status} "${r.data?.name}")`);
r = await api('GET', `/api/v1/applications/${A.id}`);
check(r.data?.connections?.length === 2, 'and it keeps both of its database choices');
check(r.data?.description === 'new words', 'and the description changed too');
r = await api('PATCH', `/api/v1/applications/${A.id}`, { name: `edit b ${stamp}` });
check(r.status === 409 && /already called/.test(String(r.data)), `a name another migration already has is refused, whatever the case (${r.status})`);
r = await api('PATCH', `/api/v1/applications/${A.id}`, { name: '   ' });
check(r.status === 400, `an empty name is refused (${r.status})`);

r = await api('PATCH', `/api/v1/manifests/${A.m1}`, { name: 'Main tables' });
check(r.status === 200 && r.data?.name === 'Main tables', 'a table selection can be renamed');
r = await api('GET', `/api/v1/manifests/${A.m1}`);
check((r.data?.tables?.length ?? 0) > 0, 'and it keeps its tables');
r = await api('PATCH', `/api/v1/manifests/${A.m2}`, { name: 'MAIN TABLES' });
check(r.status === 409, `a name another selection of the same migration has is refused (${r.status})`);
r = await api('PATCH', `/api/v1/manifests/${B.m1}`, { name: 'Main tables' });
check(r.status === 200, 'but a selection in another migration may use it');

// ---- deleting, through the API --------------------------------------------------------------
section('delete (API)');
const waiting = await newRun(A.id, A.m1, true);
const finished = await newRun(A.id, A.m1, false); // a Draft run: not in progress
const otherRun = await newRun(B.id, B.m1, false);
check((await api('GET', `/api/v1/jobs/${waiting}`)).data?.status === 'Queued', `run #${waiting} is waiting (no Worker is running)`);

r = await api('DELETE', `/api/v1/manifests/${A.m1}`);
check(r.status === 409 && String(r.data).includes(`#${waiting}`), `deleting a selection with a waiting run is refused, naming it (${r.status}: "${String(r.data).slice(0, 60)}...")`);
check(trace([waiting, finished]) > 0 && count(`SELECT count(*) FROM o2p.manifests WHERE "Id" = ${A.m1}`) === 1, 'and nothing was removed');
r = await api('DELETE', `/api/v1/applications/${A.id}`);
check(r.status === 409 && String(r.data).includes(`#${waiting}`), `so is deleting the whole migration (${r.status})`);

await api('POST', `/api/v1/jobs/${waiting}/commands`, { command: 'cancel', scope: 'job' });
r = await api('DELETE', `/api/v1/manifests/${A.m1}`);
check(r.status === 204, `once the run is cancelled the selection is deleted (${r.status})`);
check(trace([waiting, finished]) === 0, 'with its runs and every trace of them, events and logs included');
check(count(`SELECT count(*) FROM o2p.manifest_tables WHERE "ManifestId" = ${A.m1}`) === 0, 'and its tables');
check(count(`SELECT count(*) FROM o2p.manifests WHERE "Id" = ${A.m2}`) === 1, "the migration's other selection is untouched");
check(trace([otherRun]) > 0, "another migration's runs are untouched");
r = await api('DELETE', `/api/v1/manifests/${A.m1}`);
check(r.status === 404, `deleting it again says not found (${r.status})`);

// ---- the browser ----------------------------------------------------------------------------
const browser = await chromium.launch({ executablePath: CHROME, headless: true, args: ['--no-proxy-server', '--proxy-server=direct://', '--proxy-bypass-list=*'] });
const context = await browser.newContext({ viewport: { width: 1440, height: 1000 } });
await context.addInitScript(({ token, auth }) => {
  localStorage.setItem('o2p_token', token);
  localStorage.setItem('o2p_auth', JSON.stringify(auth));
}, { token, auth: authState });
const page = await context.newPage();
const pageErrors = [];
page.on('pageerror', (e) => pageErrors.push(e.message));
const dialogs = [];
page.on('dialog', async (d) => { dialogs.push(d.message()); await d.accept(); });

section('rename a migration (UI)');
await page.goto(`${UI}/applications/${B.id}`, { waitUntil: 'networkidle' });
await page.getByRole('button', { name: /^rename$/i }).click();
await page.locator('#app-name').fill(`Renamed in the UI ${stamp}`);
await page.locator('#app-description').fill('described in the UI');
await page.screenshot({ path: path.join(OUT, '01-rename-migration.png') });
await page.getByRole('button', { name: /^save$/i }).click();
await page.getByRole('heading', { level: 1, name: `Renamed in the UI ${stamp}` }).waitFor({ timeout: 10000 });
ok('the new name shows at once');
await page.reload({ waitUntil: 'networkidle' });
check(await page.getByRole('heading', { level: 1, name: `Renamed in the UI ${stamp}` }).count() === 1, 'and is still there after a reload');
check(await page.getByText('described in the UI').count() === 1, 'with the new description');

await page.getByRole('button', { name: /^rename$/i }).click();
await page.locator('#app-name').fill(`Renamed A ${stamp}`);
await page.getByRole('button', { name: /^save$/i }).click();
const nameError = page.getByRole('alert');
await nameError.waitFor({ timeout: 10000 });
check(/already called/.test(await nameError.innerText()), `a taken name is explained in the form: "${(await nameError.innerText()).slice(0, 60)}"`);
await page.getByRole('button', { name: /^cancel$/i }).click();

section('rename a table selection (UI)');
await page.getByRole('button', { name: /^rename Second selection$/i }).click();
await page.getByRole('textbox', { name: 'Table selection name' }).fill('Renamed selection');
await page.getByRole('button', { name: /^save$/i }).click();
await page.getByRole('heading', { level: 3, name: 'Renamed selection' }).waitFor({ timeout: 10000 });
ok('the selection shows its new name');
check((await api('GET', `/api/v1/manifests/${B.m2}`)).data?.name === 'Renamed selection', 'and it was saved');

section('rename in the builder (UI)');
// A selection with tables: the builder's Save is (rightly) disabled for an empty one.
await page.goto(`${UI}/applications/${B.id}/manifests/${B.m1}/builder`, { waitUntil: 'networkidle' });
const builderName = page.getByRole('textbox', { name: 'Table selection name' });
await builderName.fill('Named in the builder');
await page.getByRole('button', { name: /save selection/i }).click();
await page.waitForTimeout(2000);
check((await api('GET', `/api/v1/manifests/${B.m1}`)).data?.name === 'Named in the builder', 'saving an existing selection in the builder now saves its name too');

section('delete a table selection (UI)');
await page.goto(`${UI}/applications/${B.id}`, { waitUntil: 'networkidle' });
await page.getByRole('button', { name: /^delete Renamed selection$/i }).click();
await page.getByRole('heading', { level: 3, name: 'Renamed selection' }).waitFor({ state: 'detached', timeout: 10000 });
check(dialogs.some((d) => /Delete the table selection "Renamed selection"/.test(d) && /not touched/.test(d)), 'it asks first, and says the destination is not touched');
check(count(`SELECT count(*) FROM o2p.manifests WHERE "Id" = ${B.m2}`) === 0, 'and the selection is gone');
await page.screenshot({ path: path.join(OUT, '02-after-delete-selection.png'), fullPage: true });

section('a refused delete is explained (UI)');
const blocker = await newRun(B.id, B.m1, true);
await page.goto(`${UI}/applications`, { waitUntil: 'networkidle' });
dialogs.length = 0;
await page.getByRole('button', { name: `Delete migration Renamed in the UI ${stamp}` }).click();
await page.waitForTimeout(1500);
check(dialogs.some((d) => d.includes(`#${blocker}`) && /still waiting/.test(d)), `the list says why it cannot delete: "${(dialogs.at(-1) ?? '').slice(0, 70)}..."`);
check(count(`SELECT count(*) FROM o2p.applications WHERE "Id" = ${B.id}`) === 1, 'and the migration is still there');
await api('POST', `/api/v1/jobs/${blocker}/commands`, { command: 'cancel', scope: 'job' });

section('delete a migration (UI)');
await page.goto(`${UI}/applications/${B.id}`, { waitUntil: 'networkidle' });
dialogs.length = 0;
await page.getByRole('button', { name: /^delete migration$/i }).click();
await page.waitForURL(/\/applications$/, { timeout: 10000 });
check(dialogs.some((d) => /Delete the migration/.test(d)), 'it asks first');
check(count(`SELECT count(*) FROM o2p.applications WHERE "Id" = ${B.id}`) === 0, 'the migration is gone');
check(count(`SELECT count(*) FROM o2p.manifests WHERE "ApplicationId" = ${B.id}`) === 0, 'with its table selections');
check(trace([otherRun, blocker]) === 0, 'and its runs, events and logs');
check(count(`SELECT count(*) FROM o2p.connections WHERE "Id" IN (${oraId}, ${pgId})`) === 2, 'the databases themselves are untouched');
check(await page.getByText(`Renamed in the UI ${stamp}`).count() === 0, 'and the list no longer shows it');
check(pageErrors.length === 0, `no uncaught page errors${pageErrors.length ? ': ' + pageErrors[0] : ''}`);
await browser.close();

// ---- tidy -------------------------------------------------------------------------------------
await api('DELETE', `/api/v1/applications/${A.id}`);
await api('DELETE', `/api/v1/connections/${oraId}`);
await api('DELETE', `/api/v1/connections/${pgId}`);

console.log(`\nscreenshots -> ${OUT}`);
console.log(failures ? `\n=== ${failures} CHECK(S) FAILED ===` : '\n=== ALL CHECKS PASSED ===');
process.exit(failures ? 1 : 0);
