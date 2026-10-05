// Browser check of the "Copy changes" UI against a REAL Oracle source and a REAL PostgreSQL destination:
// the tracked-tables panel, the readiness dialog, the Copy changes dialog, and how a change copy shows on
// the run page and the Runs list.
//
// SAFETY: creates, changes and drops tables in the Oracle account and PostgreSQL schema it is given.
// Throwaway databases only; it refuses the usual dev ports.
//
// Usage: O2P_API=http://127.0.0.1:5050 O2P_UI_URL=http://127.0.0.1:5252 O2P_ADMIN_PW=... node scripts/verify-change-tracking-ui.mjs
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
const ORA_CONTAINER = process.env.O2P_ORA_CONTAINER || 'o2p-oracle-test';
const ORA_USER = process.env.O2P_ORA_USER || 'o2ptrack';
const ORA_PASSWORD = process.env.O2P_ORA_PASSWORD || 'Track2026';
const ORA_PORT = Number(process.env.O2P_ORA_PORT || 15210);
const ORA_SERVICE = process.env.O2P_ORA_SERVICE || 'FREEPDB1';
const PG_CONTAINER = process.env.O2P_PG_CONTAINER || 'dataflow-metadata-pg-1';
const PG_USER = process.env.O2P_PG_USER || 'nonoraclemigrationdb';
const PG_DB = process.env.O2P_PG_DB || 'nonoraclemigrationdb';
const PG_PORT = Number(process.env.O2P_PG_PORT || 7936);
const PG_PASSWORD = process.env.O2P_PG_PASSWORD || 'change-me-local-only';
const CHROME = process.env.CHROME_PATH || 'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe';
const OUT = path.resolve(process.cwd(), 'artifacts', 'change-tracking-ui');
const SCHEMA = 'ctui_test';

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
if (PG_PORT === 5432) { console.log('Refusing to touch a database on port 5432.'); process.exit(2); }
await fs.mkdir(OUT, { recursive: true });

let failures = 0;
const ok = (m) => console.log('  \u2713 ' + m);
const bad = (m) => { failures++; console.log('  \u2717 ' + m); };
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
const ora = (sql) => execFileSync('docker', ['exec', '-i', ORA_CONTAINER, 'sqlplus', '-s', `${ORA_USER}/${ORA_PASSWORD}@localhost:1521/${ORA_SERVICE}`],
  { input: `SET HEADING OFF FEEDBACK OFF PAGESIZE 0\nWHENEVER SQLERROR EXIT FAILURE\n${sql}\nEXIT\n`, encoding: 'utf8' }).trim();
const pg = (sql) => execFileSync('docker', ['exec', '-i', PG_CONTAINER, 'psql', '-U', PG_USER, '-d', PG_DB, '-A', '-t', '-c', sql], { encoding: 'utf8' }).trim();

console.log('\n=== COPY CHANGES UI ===');

// ---- seed: real tables and a finished bulk copy ------------------------------------------------
section('seed');
let r = await api('POST', '/api/v1/auth/login', { username: 'admin', password: PASSWORD });
if (r.status !== 200) { console.log(`login failed: ${r.status} ${JSON.stringify(r.data)}`); process.exit(1); }
token = r.data.token;
const authState = r.data;
ok('logged in once');

ora(`
BEGIN
  FOR t IN (SELECT table_name FROM user_tables WHERE table_name IN ('UI_ITEMS', 'UI_NOKEY')) LOOP
    EXECUTE IMMEDIATE 'DROP TABLE ' || t.table_name || ' PURGE';
  END LOOP;
END;
/
CREATE TABLE ui_items (id NUMBER(10) PRIMARY KEY, label VARCHAR2(50));
CREATE TABLE ui_nokey (id NUMBER(10), label VARCHAR2(50));
INSERT INTO ui_items SELECT LEVEL, 'item ' || LEVEL FROM dual CONNECT BY LEVEL <= 20;
INSERT INTO ui_nokey VALUES (1, 'x');
COMMIT;`);
await sleep(7000); // ORA-01466: a table cannot be read AS OF an SCN within seconds of its creation
pg(`DROP SCHEMA IF EXISTS ${SCHEMA} CASCADE; CREATE SCHEMA ${SCHEMA};`);

const stamp = Date.now().toString().slice(-6);
r = await api('POST', '/api/v1/connections', { name: `ui-ora-${stamp}`, kind: 'oracle', host: '127.0.0.1', port: ORA_PORT, serviceOrDb: ORA_SERVICE, username: ORA_USER, password: ORA_PASSWORD });
const oraId = r.data?.id;
r = await api('POST', '/api/v1/connections', { name: `ui-pg-${stamp}`, kind: 'postgres', host: '127.0.0.1', port: PG_PORT, serviceOrDb: PG_DB, username: PG_USER, password: PG_PASSWORD });
const pgId = r.data?.id;
const appName = `Copy changes UI ${stamp}`;
r = await api('POST', '/api/v1/applications', { name: appName, description: 'temporary' });
const appId = r.data?.id;
await api('PUT', `/api/v1/applications/${appId}`, {
  id: appId, name: appName, description: 'temporary',
  connections: [{ applicationId: appId, slot: 'oracle_test', connectionId: oraId }, { applicationId: appId, slot: 'pg_test', connectionId: pgId }],
});
await api('POST', `/api/v1/connections/${oraId}/discovery/refresh?owner=${ORA_USER.toUpperCase()}`, { tableNames: ['UI_ITEMS', 'UI_NOKEY'] });
r = await api('POST', `/api/v1/applications/${appId}/manifests/generate?connectionId=${oraId}&owner=${ORA_USER.toUpperCase()}&version=1.0`, {});
const manifestId = r.data?.id;
r = await api('POST', '/api/v1/jobs', { applicationId: appId, manifestId, sourceSlot: 'oracle_test', targetSlot: 'pg_test', targetSchema: SCHEMA });
const bulkId = r.data?.id;
await api('POST', `/api/v1/jobs/${bulkId}/launch`, {});
for (let i = 0; i < 90; i++) {
  await sleep(2000);
  const s = (await api('GET', `/api/v1/jobs/${bulkId}`)).data?.status;
  if (['Completed', 'CompletedWithErrors', 'Failed', 'Cancelled'].includes(s)) break;
}
check(pg(`SELECT count(*) FROM ${SCHEMA}.ui_items`) === '20', 'a bulk copy of a real Oracle table finished into a real PostgreSQL');

// ---- browser --------------------------------------------------------------------------------------
const browser = await chromium.launch({ executablePath: CHROME, headless: true, args: ['--no-proxy-server', '--proxy-server=direct://', '--proxy-bypass-list=*'] });
const context = await browser.newContext({ viewport: { width: 1440, height: 1000 } });
await context.addInitScript(({ token, auth }) => {
  localStorage.setItem('o2p_token', token);
  localStorage.setItem('o2p_auth', JSON.stringify(auth));
}, { token, auth: authState });
const page = await context.newPage();
const pageErrors = [];
page.on('pageerror', (e) => pageErrors.push(e.message));

section('tracked tables panel');
await page.goto(`${UI}/applications/${appId}`, { waitUntil: 'networkidle' });
const panel = page.locator('[data-testid="tracked-tables"]');
await panel.waitFor({ timeout: 15000 });
const itemsRow = panel.locator('tr[data-table="ui_items"]');
check(await itemsRow.count() === 1, 'the keyed table is listed as tracked');
check(await panel.locator('tr[data-table="ui_nokey"]').count() === 0, 'the table with no key is not');
check(/Ready for first change copy/.test(await itemsRow.innerText()), `its status reads "${(await itemsRow.locator('.ct-status').innerText())}"`);
check(/Not yet/.test(await itemsRow.innerText()), 'and it has not been change-copied yet');
await page.screenshot({ path: path.join(OUT, '01-panel.png'), fullPage: true });

section('readiness dialog');
await page.getByRole('button', { name: /check change tracking/i }).first().click();
const dialog = page.getByRole('dialog');
await dialog.getByRole('button', { name: /^check$/i }).click();
const result = dialog.locator('[data-testid="readiness-result"]');
await result.waitFor({ timeout: 60000 });
const resultText = await result.innerText();
check(/pluggable database FREEPDB1/.test(resultText), 'it names the source layout');
check((await result.locator('li[data-ok="false"]').count()) >= 1, 'it marks what is missing');
const nokeyItem = result.locator('li[data-table="UI_NOKEY"]');
check(/no primary key/.test(await nokeyItem.innerText()), 'the unkeyed table is explained');
check(await result.locator('li[data-table="UI_ITEMS"][data-ok="true"]').count() === 1, 'the keyed table passes');
check(/Start a LogMiner session/.test(resultText), 'it tried a real LogMiner session');
await page.screenshot({ path: path.join(OUT, '02-readiness.png'), fullPage: true });
await dialog.getByRole('button', { name: /^close$/i }).click();

section('Copy changes dialog');
ora(`INSERT INTO ui_items VALUES (21, 'added');
UPDATE ui_items SET label = 'changed' WHERE id = 2;
DELETE FROM ui_items WHERE id = 3;
COMMIT;`);
ok('in Oracle: one row added, one changed, one deleted');

await page.getByRole('button', { name: /^copy changes$/i }).first().click();
const form = page.locator('form').filter({ has: page.locator('#run-target-schema') });
check(await page.getByRole('heading', { name: /copy changes/i }).count() >= 1, 'the dialog is titled Copy changes');
check(/Nothing is emptied or recreated/.test(await page.locator('.card').filter({ has: form }).innerText()), 'it says nothing is emptied or recreated');
const schemaInput = page.locator('#run-target-schema');
await schemaInput.fill(SCHEMA);
await schemaInput.press('Escape');
await page.screenshot({ path: path.join(OUT, '03-dialog.png') });
await form.getByRole('button', { name: /^copy changes$/i }).click();
await page.waitForURL(/\/jobs\/\d+$/, { timeout: 15000 });
const runId = Number(page.url().split('/').pop());
check(runId > 0, `it started run #${runId} and opened it`);

section('run page');
await page.getByText('Finished', { exact: true }).first().waitFor({ timeout: 120000 });
ok('the run finished');
check(await page.locator('.run-kind[data-kind="changes"]').count() === 1, 'the run is tagged as a change copy');
check(await page.getByRole('button', { name: /retry/i }).count() === 0, 'no Retry is offered for a change copy');
check(await page.getByRole('button', { name: /^pause$/i }).count() === 0, 'no Pause either');
await page.getByRole('button', { name: /show details for UI_ITEMS/i }).click();
const written = page.locator('[data-testid="rows-written"]').first();
await written.waitFor({ timeout: 10000 });
check(/Rows added or updated: 2/.test(await written.innerText()), `it reports "${await written.innerText()}"`);
check(/Rows deleted: 1/.test(await page.locator('[data-testid="rows-deleted"]').first().innerText()), 'and one row deleted');
const stats = await page.locator('[data-testid="change-stats"]').innerText();
check(/Rows added or updated\s*2/i.test(stats) && /Rows deleted\s*1/i.test(stats) && /1 \/ 1/.test(stats), 'the headline figures are about rows written and deleted, not batches');
check(await page.getByText(/Batches running|Batch progress|Data transferred/i).count() === 0, 'no batch or byte figures that mean nothing for a change copy');
check(await page.locator('[data-testid="no-rowcount"]').count() === 1, 'it explains why there is no row-count check, instead of promising one');
await page.screenshot({ path: path.join(OUT, '04-run.png'), fullPage: true });

check(pg(`SELECT label FROM ${SCHEMA}.ui_items WHERE id = 2`) === 'changed' && pg(`SELECT count(*) FROM ${SCHEMA}.ui_items WHERE id IN (3)`) === '0' && pg(`SELECT count(*) FROM ${SCHEMA}.ui_items WHERE id = 21`) === '1',
  'the destination now has the added and changed rows and not the deleted one');

section('Runs list');
await page.goto(`${UI}/jobs`, { waitUntil: 'networkidle' });
const card = page.locator('.card').filter({ has: page.locator('h4', { hasText: new RegExp(`^Run #${runId}\\b`) }) }).last();
check(/Change copy/.test(await card.innerText()), 'the Runs list tags it as a change copy');
check(await card.getByRole('button', { name: /retry/i }).count() === 0, 'with no Retry button');
const bulkCard = page.locator('.card').filter({ has: page.locator('h4', { hasText: new RegExp(`^Run #${bulkId}\\b`) }) }).last();
check(/Bulk copy/.test(await bulkCard.innerText()), 'the bulk run is tagged as a bulk copy');
await page.screenshot({ path: path.join(OUT, '05-runs.png') });

section('panel afterwards');
await page.goto(`${UI}/applications/${appId}`, { waitUntil: 'networkidle' });
await page.locator('[data-testid="tracked-tables"]').waitFor();
const after = page.locator('[data-testid="tracked-tables"] tr[data-table="ui_items"]');
check(/Tracking/.test(await after.innerText()) && !/Not yet/.test(await after.innerText()), `the table now reads "${(await after.locator('.ct-status').innerText())}" with a last-copied time`);
await page.screenshot({ path: path.join(OUT, '06-panel-after.png'), fullPage: true });

section('narrow screen');
await page.setViewportSize({ width: 390, height: 844 });
await page.goto(`${UI}/applications/${appId}`, { waitUntil: 'networkidle' });
const overflow = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
check(overflow <= 1, `the page does not scroll sideways on a phone-width screen (${overflow}px)`);
await page.screenshot({ path: path.join(OUT, '07-narrow.png'), fullPage: true });

check(pageErrors.length === 0, `no uncaught page errors${pageErrors.length ? ': ' + pageErrors[0] : ''}`);
await browser.close();

// ---- tidy ------------------------------------------------------------------------------------------
await api('DELETE', `/api/v1/applications/${appId}`);
pg(`DELETE FROM o2p.tracked_tables WHERE "TargetSchema" = '${SCHEMA}';`);
await api('DELETE', `/api/v1/connections/${oraId}`);
await api('DELETE', `/api/v1/connections/${pgId}`);

console.log(`\nscreenshots -> ${OUT}`);
console.log(failures ? `\n=== ${failures} CHECK(S) FAILED ===` : '\n=== ALL CHECKS PASSED ===');
process.exit(failures ? 1 : 0);
