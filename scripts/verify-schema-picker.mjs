// Browser check for the Source schema picker (SchemaCombobox) on the table-selection page and in
// the "Find tables automatically" dialog.
//
// SAFETY: run this only against a throwaway API. It creates and deletes a connection, a migration
// and a table selection, so pointing it at a real environment would write to that environment.
// It refuses to run against the usual dev ports.
//
// Usage: O2P_API=http://127.0.0.1:5050 O2P_UI_URL=http://127.0.0.1:5252 O2P_ADMIN_PW=... node scripts/verify-schema-picker.mjs
import fs from 'node:fs/promises';
import path from 'node:path';
import { pathToFileURL } from 'node:url';

const { chromium } = await import(
  pathToFileURL(path.resolve(process.cwd(), 'web', 'node_modules', 'playwright-core', 'index.mjs')).href
);

const API = process.env.O2P_API;
const UI = process.env.O2P_UI_URL;
const PASSWORD = process.env.O2P_ADMIN_PW;
const CHROME = process.env.CHROME_PATH || 'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe';
const OUT = path.resolve(process.cwd(), 'artifacts', 'schema-picker');

if (!API || !UI || !PASSWORD) {
  console.log('Set O2P_API, O2P_UI_URL and O2P_ADMIN_PW (all three) - this script will not guess a target.');
  process.exit(2);
}
for (const u of [API, UI]) {
  const port = new URL(u).port;
  if (['5000', '5151', '3051', '3052'].includes(port)) {
    console.log(`Refusing to run against port ${port}: that is a normal dev/deploy port. Use a throwaway API and UI on other ports.`);
    process.exit(2);
  }
}

await fs.mkdir(OUT, { recursive: true });

let failures = 0;
const ok = (m) => console.log('  \u2713 ' + m);
const bad = (m) => { failures++; console.log('  \u2717 ' + m); };
const check = (cond, m) => (cond ? ok(m) : bad(m));
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

console.log('\n=== SOURCE SCHEMA PICKER CHECK ===');

section('seed');
let r = await api('POST', '/api/v1/auth/login', { username: 'admin', password: PASSWORD });
if (r.status !== 200) { console.log(`login failed: ${r.status} ${JSON.stringify(r.data)}`); process.exit(1); }
token = r.data.token;
const authState = r.data;
ok('logged in once');

const stamp = Date.now().toString().slice(-6);
const connName = `picker-ora-${stamp}`;
r = await api('POST', '/api/v1/connections', { name: connName, kind: 'oracle', host: 'mock', port: 1521, serviceOrDb: 'X', username: 'u', password: 'p' });
const connId = r.data?.id;
r = await api('POST', '/api/v1/applications', { name: `Picker check ${stamp}`, description: 'temporary' });
const appId = r.data?.id;
r = await api('POST', `/api/v1/applications/${appId}/manifests`, { name: `Picker check ${stamp}`, version: 1 });
const manifestId = r.data?.id;
r = await api('PUT', `/api/v1/manifests/${manifestId}/tables`, [
  { owner: 'APP', tableName: 'CUSTOMERS', included: true, whereClause: null, estRows: 10, estBytes: 2048, hasLobs: false, isPartitioned: false, isIot: false,
    columns: [{ columnName: 'ID', oracleDataType: 'NUMBER', postgresDataType: 'bigint', isNullable: false, isPrimaryKey: false, isExcluded: false }] },
]);
check(!!(connId && appId && manifestId) && r.status === 200, `seeded connection, migration and a saved selection (app ${appId}, selection ${manifestId})`);

const browser = await chromium.launch({
  executablePath: CHROME,
  headless: true,
  args: ['--no-proxy-server', '--proxy-server=direct://', '--proxy-bypass-list=*'],
});

async function newPage() {
  const context = await browser.newContext({ viewport: { width: 1440, height: 900 } });
  await context.addInitScript(({ token, auth }) => {
    localStorage.setItem('o2p_token', token);
    localStorage.setItem('o2p_auth', JSON.stringify(auth));
  }, { token, auth: authState });
  const page = await context.newPage();
  const errors = [];
  page.on('pageerror', (e) => errors.push(e.message));
  return { page, context, errors };
}

const builderUrl = `${UI}/applications/${appId}/manifests/${manifestId}/builder`;
const schemaInput = (page) => page.locator('#tb-source-schema');
// Scoped to the popup: a bare getByRole('option') also matches the <option>s of the database <select>.
const options = (page) => page.locator('.sc-popup [role=option]');

// ---- table-selection page -----------------------------------------------------------------
section('table-selection page');
{
  const { page, context, errors } = await newPage();
  let schemaRequests = 0;
  page.on('request', (req) => { if (req.url().includes('/discovery/schemas')) schemaRequests++; });

  await page.goto(builderUrl, { waitUntil: 'networkidle' });
  await schemaInput(page).waitFor();

  check(schemaRequests === 0, 'opening the page does NOT contact the source database (list loads lazily)');
  check((await schemaInput(page).inputValue()) === 'APP', 'a saved selection seeds the box with its schema (APP)');

  await schemaInput(page).click();
  await options(page).first().waitFor({ timeout: 10000 });
  check(schemaRequests === 1, 'focusing the box fetches the list once');
  const texts = (await options(page).allInnerTexts()).map((t) => t.replace(/\s+/g, ' ').trim());
  check(texts.length === 3, `lists 3 schemas (${texts.join(' | ')})`);
  check(texts[0] === 'APP 2 tables' && texts[1] === 'HR 7 tables' && texts[2] === 'SALES 12 tables', 'each row shows its table count');
  check(texts.length === 3, 'all schemas are shown even though "APP" is already the value (untyped opens unfiltered)');

  // popup is really on screen, not clipped behind the grid
  const popup = page.locator('.sc-popup');
  const pb = await popup.boundingBox();
  const hit = await page.evaluate(({ x, y }) => !!document.elementFromPoint(x, y)?.closest('.sc-popup'), { x: pb.x + pb.width / 2, y: pb.y + Math.min(30, pb.height / 2) });
  check(hit, 'the popup is painted above the grid (not clipped)');
  await page.screenshot({ path: path.join(OUT, '01-open.png') });

  // filtering + keyboard
  await schemaInput(page).fill('H');
  check((await options(page).count()) === 1 && /^HR/.test((await options(page).first().innerText()).trim()), 'typing "H" filters to HR');
  await page.keyboard.press('Enter');
  check((await schemaInput(page).inputValue()) === 'HR', 'Enter picks the highlighted row');
  check(!(await popup.isVisible()), 'and closes the list');

  await page.keyboard.press('ArrowDown');
  await options(page).first().waitFor();
  check(schemaRequests === 1, 'reopening uses the cached list (no second request)');
  await page.keyboard.press('ArrowDown');
  await page.keyboard.press('ArrowDown');
  await page.keyboard.press('Enter');
  check((await schemaInput(page).inputValue()) === 'SALES', 'ArrowDown moves the highlight and Enter picks it (keyboard only)');

  await schemaInput(page).click();
  await page.keyboard.press('Escape');
  check(!(await popup.isVisible()) && (await schemaInput(page).inputValue()) === 'SALES', 'Escape closes the list and keeps the value');

  // type-anyway
  await schemaInput(page).click();
  await schemaInput(page).fill('zzz');
  check((await schemaInput(page).inputValue()) === 'ZZZ', 'typed text is upper-cased as before');
  check(/No listed schema matches/.test(await page.locator('.sc-popup').innerText()), 'no match shows "press Enter to use it anyway"');
  await page.keyboard.press('Enter');
  check((await schemaInput(page).inputValue()) === 'ZZZ' && !(await popup.isVisible()), 'Enter with no match keeps what was typed');
  check(await page.locator('.sc-hint').isVisible(), 'a value not in the list shows the "Not found in this database" hint');
  const scanBtn = page.getByRole('button', { name: /scan source database/i }).first();
  check(await scanBtn.isEnabled(), 'the hint does not block Scan');
  await page.screenshot({ path: path.join(OUT, '02-not-found.png') });

  // Reload link
  await schemaInput(page).click();
  await page.getByRole('button', { name: 'Reload' }).click();
  await page.waitForTimeout(400);
  check(schemaRequests === 2, 'the Reload link fetches again');

  // # in a schema name must not cut the query string short
  await page.keyboard.press('Escape');
  await schemaInput(page).fill('APP#1');
  await page.keyboard.press('Enter');
  const refreshReq = page.waitForRequest((q) => q.url().includes('/discovery/refresh'), { timeout: 10000 });
  await scanBtn.click();
  const sent = await refreshReq;
  check(new URL(sent.url()).searchParams.get('owner') === 'APP#1', `a "#" in the schema name survives the request (${new URL(sent.url()).search})`);

  check(errors.length === 0, `no uncaught page errors${errors.length ? ': ' + errors[0] : ''}`);
  await context.close();
}

// ---- error path ---------------------------------------------------------------------------
section('when the list cannot be loaded');
{
  const { page, context } = await newPage();
  await page.route('**/discovery/schemas', (route) =>
    route.fulfill({ status: 400, contentType: 'application/json', body: JSON.stringify('Could not read the schemas: ORA-01017: invalid username/password') }));

  await page.goto(builderUrl, { waitUntil: 'networkidle' });
  await schemaInput(page).click();
  const alert = page.locator('.sc-popup [role=alert]');
  await alert.waitFor({ timeout: 10000 });
  check(/ORA-01017/.test(await alert.innerText()), 'shows the server\'s message');
  check(await schemaInput(page).isEnabled(), 'the box stays enabled');
  await schemaInput(page).fill('HR');
  check((await schemaInput(page).inputValue()) === 'HR', 'and you can still type a schema name');
  check(!(await page.locator('.sc-hint').isVisible()), 'no "not found" hint when the list itself failed');
  await page.screenshot({ path: path.join(OUT, '03-error.png') });

  await page.unroute('**/discovery/schemas');
  await page.getByRole('button', { name: 'Retry' }).click();
  await options(page).first().waitFor({ timeout: 10000 });
  // "HR" was typed above, so the recovered list is filtered by it - one row, not three.
  check((await options(page).count()) === 1 && /^HR/.test((await options(page).first().innerText()).trim()), 'Retry recovers once the source is reachable, and still honours what was typed');
  await context.close();
}

// ---- the dialog ---------------------------------------------------------------------------
section('"Find tables automatically" dialog');
{
  const { page, context, errors } = await newPage();
  await page.goto(`${UI}/applications/${appId}`, { waitUntil: 'networkidle' });
  const before = (await api('GET', `/api/v1/applications/${appId}/manifests`)).data?.length ?? 0;

  await page.getByRole('button', { name: /find tables automatically/i }).click();
  const schema = page.locator('#gen-source-schema');
  await schema.waitFor();
  check(await schema.isDisabled(), 'the schema box is disabled until a database is chosen');
  check((await schema.getAttribute('placeholder')) === 'Choose a source database first', 'and says why');

  await page.locator('#gen-source-db').selectOption({ label: connName });
  check(await schema.isEnabled(), 'choosing a database enables it');
  await schema.click();
  await options(page).first().waitFor({ timeout: 10000 });
  check((await options(page).count()) === 3, 'it lists the same three schemas');
  await options(page).filter({ hasText: 'SALES' }).click();
  check((await schema.inputValue()) === 'SALES', 'clicking a row picks it');
  await page.screenshot({ path: path.join(OUT, '04-dialog.png') });

  await page.getByRole('button', { name: 'Find tables', exact: true }).click();
  await page.waitForTimeout(2500);
  const after = (await api('GET', `/api/v1/applications/${appId}/manifests`)).data?.length ?? 0;
  check(after === before + 1, `submitting builds a table selection from the picked schema (${before} -> ${after})`);
  check(errors.length === 0, `no uncaught page errors${errors.length ? ': ' + errors[0] : ''}`);
  await context.close();
}

// ---- cleanup ------------------------------------------------------------------------------
await api('DELETE', `/api/v1/applications/${appId}`);
await api('DELETE', `/api/v1/connections/${connId}`);
await browser.close();

console.log(`\nscreenshots -> ${OUT}`);
console.log(`\n=== ${failures === 0 ? 'ALL CHECKS PASSED' : failures + ' CHECK(S) FAILED'} ===\n`);
process.exit(failures ? 1 : 0);
