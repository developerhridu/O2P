// Checks the Destination schema picker in the "Start a run" dialog: it lists the real schemas of
// the destination database chosen under "Copy to", marks ones the account cannot create tables in,
// and follows the Copy-to selection when it changes.
//
// Unlike the source picker, this path IS fully testable here: the destination is PostgreSQL and
// there is a real PostgreSQL in the loop.
//
// SAFETY: seeds and deletes data - throwaway API only. Refuses the usual dev ports.
//
// Usage: O2P_API=http://127.0.0.1:5050 O2P_UI_URL=http://127.0.0.1:5252 O2P_ADMIN_PW=... node scripts/verify-target-schema-picker.mjs
import fs from 'node:fs/promises';
import path from 'node:path';
import { pathToFileURL } from 'node:url';

const { chromium } = await import(
  pathToFileURL(path.resolve(process.cwd(), 'web', 'node_modules', 'playwright-core', 'index.mjs')).href
);

const API = process.env.O2P_API;
const UI = process.env.O2P_UI_URL;
const PASSWORD = process.env.O2P_ADMIN_PW;
const PG_HOST = process.env.O2P_PG_HOST || '127.0.0.1';
const PG_PORT = Number(process.env.O2P_PG_PORT || 7936);
const PG_DB = process.env.O2P_PG_DB || 'nonoraclemigrationdb';
const PG_USER = process.env.O2P_PG_USER || 'nonoraclemigrationdb';
const PG_PASSWORD = process.env.O2P_PG_PASSWORD || 'change-me-local-only';
const CHROME = process.env.CHROME_PATH || 'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe';
const OUT = path.resolve(process.cwd(), 'artifacts', 'target-schema-picker');

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

console.log('\n=== DESTINATION SCHEMA PICKER CHECK ===');

section('api');
let r = await api('POST', '/api/v1/auth/login', { username: 'admin', password: PASSWORD });
if (r.status !== 200) { console.log(`login failed: ${r.status} ${JSON.stringify(r.data)}`); process.exit(1); }
token = r.data.token;
const authState = r.data;
ok('logged in once');

const stamp = Date.now().toString().slice(-6);
r = await api('POST', '/api/v1/connections', { name: `dest-ora-${stamp}`, kind: 'oracle', host: 'mock', port: 1521, serviceOrDb: 'X', username: 'u', password: 'p' });
const oraId = r.data?.id;
// A REAL PostgreSQL destination, so the listing is read from an actual catalogue.
r = await api('POST', '/api/v1/connections', { name: `dest-pg-${stamp}`, kind: 'postgres', host: PG_HOST, port: PG_PORT, serviceOrDb: PG_DB, username: PG_USER, password: PG_PASSWORD });
const pgId = r.data?.id;
// A second, mock destination, to prove the list follows the Copy-to choice.
r = await api('POST', '/api/v1/connections', { name: `dest-pg-mock-${stamp}`, kind: 'postgres', host: 'mock', port: 5432, serviceOrDb: 'x', username: 'x', password: 'x' });
const pgMockId = r.data?.id;
check(!!(oraId && pgId && pgMockId), 'created a source and two destinations');

r = await api('GET', `/api/v1/connections/${pgId}/schemas`);
const names = (r.data?.schemas ?? []).map((s) => s.name);
check(r.status === 200, `listing a real destination works (HTTP ${r.status})`);
check(names.includes('public') && names.includes('reporting'), `it returns the real schemas (${names.slice(0, 6).join(', ')})`);
check(!names.some((n) => n.startsWith('pg_') || n === 'information_schema'), 'and leaves out PostgreSQL\'s own schemas');
check((r.data?.schemas ?? []).every((s) => typeof s.canCreate === 'boolean'), 'each says whether tables can be created in it');

r = await api('GET', `/api/v1/connections/${oraId}/schemas`);
check(r.status === 400, 'asking an Oracle connection for destination schemas is refused');

section('browser');
const appName = `Dest check ${stamp}`;
r = await api('POST', '/api/v1/applications', { name: appName, description: 'temporary' });
const appId = r.data?.id;
await api('PUT', `/api/v1/applications/${appId}`, {
  id: appId, name: appName, description: 'temporary',
  connections: [
    { applicationId: appId, slot: 'oracle_test', connectionId: oraId },
    { applicationId: appId, slot: 'pg_test', connectionId: pgId },
    { applicationId: appId, slot: 'pg_live', connectionId: pgMockId },
  ],
});
await api('POST', `/api/v1/connections/${oraId}/discovery/refresh?owner=APP`, { tableNames: [] });
r = await api('POST', `/api/v1/applications/${appId}/manifests/generate?connectionId=${oraId}&owner=APP&version=1.0`, {});
check(!!r.data?.id, 'seeded a migration with a table selection');

const browser = await chromium.launch({
  executablePath: CHROME,
  headless: true,
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

let schemaRequests = 0;
page.on('request', (q) => { if (/\/connections\/\d+\/schemas/.test(q.url())) schemaRequests++; });

await page.goto(`${UI}/applications/${appId}`, { waitUntil: 'networkidle' });
await page.getByRole('button', { name: /^Start Run$/i }).first().click();

const schema = page.locator('#run-target-schema');
await schema.waitFor({ timeout: 10000 });
const options = () => page.locator('.sc-popup [role=option]');

check((await schema.inputValue()) === 'public', 'the dialog still defaults to "public"');
check(schemaRequests === 0, 'the list is not fetched until the box is opened');

await schema.click();
await options().first().waitFor({ timeout: 10000 });
const shown = (await options().allInnerTexts()).map((t) => t.replace(/\s+/g, ' ').trim());
check(shown.length >= 3, `lists the destination's schemas (${shown.join(' | ')})`);
check(shown.some((t) => t.startsWith('public')), 'including public');
check(shown.some((t) => /can create tables/.test(t)), 'and says where tables can be created');
await page.screenshot({ path: path.join(OUT, '01-open.png') });

// Typing filters, and a Postgres name must NOT be upper-cased the way an Oracle owner is.
await schema.fill('report');
await page.waitForTimeout(200);
check((await schema.inputValue()) === 'report', 'typed text keeps its case (Postgres is case-sensitive when quoted)');
check((await options().count()) === 1, 'typing filters the list');
await page.keyboard.press('Enter');
check((await schema.inputValue()) === 'reporting', 'Enter picks the highlighted schema');

// A name that is not listed is still allowed through.
await schema.fill('not_a_schema');
await page.keyboard.press('Enter');
check((await schema.inputValue()) === 'not_a_schema', 'an unlisted name can still be typed');
check(await page.locator('.sc-hint').isVisible(), 'and is flagged as not found in this database');

// Switching "Copy to" must re-point the picker at the other destination.
const before = schemaRequests;
await page.locator('select').nth(1).selectOption('pg_live');
await schema.fill('');
await schema.click();
await options().first().waitFor({ timeout: 10000 });
// The name and its detail are separate elements, so split on any whitespace, not just a space.
const mockNames = (await options().allInnerTexts()).map((t) => t.trim().split(/\s+/)[0]);
check(schemaRequests > before, 'changing "Copy to" fetches the other destination\'s schemas');
check(mockNames.includes('readonly_archive'), `and shows that database's own list (${mockNames.join(', ')})`);
check(await page.locator('.sc-count-muted').count() > 0, 'a schema it cannot create tables in is marked');
await page.screenshot({ path: path.join(OUT, '02-other-destination.png') });

check(pageErrors.length === 0, `no uncaught page errors${pageErrors.length ? ': ' + pageErrors[0] : ''}`);

await api('DELETE', `/api/v1/applications/${appId}`);
for (const id of [oraId, pgId, pgMockId]) await api('DELETE', `/api/v1/connections/${id}`);
await browser.close();

console.log(`\nscreenshots -> ${OUT}`);
console.log(`\n=== ${failures === 0 ? 'ALL CHECKS PASSED' : failures + ' CHECK(S) FAILED'} ===\n`);
process.exit(failures ? 1 : 0);
