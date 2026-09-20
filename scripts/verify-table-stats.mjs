// Checks the Rows/Size behaviour on the table-selection page: a figure Oracle does not know reads
// "Unknown" (never 0), a figure that genuinely IS zero reads 0, and Sync fetches both counts and
// sizes from the source.
//
// SAFETY: seeds and deletes data - throwaway API only. Refuses the usual dev ports.
//
// Usage: O2P_API=http://127.0.0.1:5050 O2P_UI_URL=http://127.0.0.1:5252 O2P_ADMIN_PW=... node scripts/verify-table-stats.mjs
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
const OUT = path.resolve(process.cwd(), 'artifacts', 'table-stats');

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

console.log('\n=== ROW COUNT / SIZE CHECK ===');

section('api');
let r = await api('POST', '/api/v1/auth/login', { username: 'admin', password: PASSWORD });
if (r.status !== 200) { console.log(`login failed: ${r.status} ${JSON.stringify(r.data)}`); process.exit(1); }
token = r.data.token;
const authState = r.data;
ok('logged in once');

const stamp = Date.now().toString().slice(-6);
r = await api('POST', '/api/v1/connections', { name: `stats-ora-${stamp}`, kind: 'oracle', host: 'mock', port: 1521, serviceOrDb: 'X', username: 'u', password: 'p' });
const connId = r.data?.id;
r = await api('POST', '/api/v1/applications', { name: `Stats check ${stamp}`, description: 'temporary' });
const appId = r.data?.id;

// A scan is what the user clicks; assert what it returns before looking at the screen.
r = await api('POST', `/api/v1/connections/${connId}/discovery/refresh?owner=APP`, { tableNames: [] });
const scanned = r.data?.tables ?? [];
const customers = scanned.find((t) => t.tableName === 'CUSTOMERS');
const orders = scanned.find((t) => t.tableName === 'ORDERS');
check(r.status === 200 && scanned.length === 2, `scan returned ${scanned.length} tables`);
check(customers?.estRows === 1500, `CUSTOMERS carries a row count (${customers?.estRows})`);
check(customers?.estBytes > 0 && customers?.sizeIsEstimate === false, `CUSTOMERS carries a measured size (${customers?.estBytes})`);
check(customers?.hasLobs === true, 'CUSTOMERS reports large objects - the LOB badge can appear again');
// The bug: a table Oracle has no statistics for must come back as null, not 0.
check(orders?.estRows === null, `ORDERS has NO row count and reports null, not 0 (${JSON.stringify(orders?.estRows)})`);
check(orders?.sizeIsEstimate === true, 'ORDERS size is flagged as an estimate');

// Generated selections used to drop the statistics entirely.
r = await api('POST', `/api/v1/applications/${appId}/manifests/generate?connectionId=${connId}&owner=APP&version=1.0`, {});
const manifestId = r.data?.id;
r = await api('GET', `/api/v1/manifests/${manifestId}`);
const genCustomers = (r.data?.tables ?? []).find((t) => t.tableName === 'CUSTOMERS');
check(genCustomers?.estRows === 1500 && genCustomers?.estBytes > 0, `"Find tables automatically" keeps the statistics (${genCustomers?.estRows} rows, ${genCustomers?.estBytes} bytes)`);

// Sync returns both figures.
r = await api('POST', `/api/v1/connections/${connId}/discovery/sync?owner=APP&table=ORDERS`);
check(r.status === 200 && typeof r.data?.rows === 'number', `sync returns an exact row count (${r.data?.rows})`);
check(r.data?.bytes > 0, `sync returns a size too (${r.data?.bytes})`);
check(!!r.data?.rowsCountedAt, 'and records when it was counted');

section('browser');
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

await page.goto(`${UI}/applications/${appId}/manifests/${manifestId}/builder`, { waitUntil: 'networkidle' });
await page.locator('.tb-vrow').first().waitFor({ timeout: 20000 });

const rowFor = (name) => page.locator('.tb-vrow').filter({ hasText: name }).first();
const cellsOf = async (name) => {
  const cells = rowFor(name).locator('.tb-num');
  return [(await cells.nth(0).innerText()).trim(), (await cells.nth(1).innerText()).trim()];
};

// Scan from the page itself, which is the action the user reported on.
await page.getByRole('button', { name: /scan source database/i }).first().click();
await page.waitForTimeout(2500);

let [custRows, custSize] = await cellsOf('CUSTOMERS');
let [ordRows, ordSize] = await cellsOf('ORDERS');
check(custRows === '1,500', `CUSTOMERS shows its row count (${custRows})`);
check(/KB|MB|GB|B$/.test(custSize) && !custSize.startsWith('~'), `CUSTOMERS shows a real size (${custSize})`);
check(ordRows === 'Unknown', `a table with no statistics reads "Unknown", not 0 (${ordRows})`);
check(ordSize.startsWith('~'), `an estimated size is marked with ~ (${ordSize})`);

// The reported bug, asserted directly.
const gridText = await page.locator('.tb-grid').innerText();
check(!/\b0\.00 MB\b/.test(gridText), 'no cell reads "0.00 MB"');
check((await page.locator('.tb-unknown').count()) > 0, 'unknown values are styled as such');
await page.screenshot({ path: path.join(OUT, '01-after-scan.png') });

section('sync');
const syncBtn = page.getByRole('button', { name: /Sync counts & sizes/i });
check(await syncBtn.isVisible(), 'the Sync button is offered');
check(/1 unknown/.test(await syncBtn.innerText()), 'and says how many tables have no count');
await syncBtn.click();
await page.waitForTimeout(4000);

[ordRows, ordSize] = await cellsOf('ORDERS');
check(/^[\d,]+$/.test(ordRows), `after Sync the unknown count is filled in (${ordRows})`);
[custRows, custSize] = await cellsOf('CUSTOMERS');
check(custRows === '1,500', `and an already-known count is still right (${custRows})`);
check(!(await page.getByRole('button', { name: /Sync counts & sizes/i }).innerText()).includes('unknown'), 'the "unknown" tally disappears once nothing is unknown');
await page.screenshot({ path: path.join(OUT, '02-after-sync.png') });

section('zero is a valid count');
// A table that really has no rows must show 0, not "Unknown".
await api('PUT', `/api/v1/manifests/${manifestId}/tables`, [
  { owner: 'APP', tableName: 'EMPTY_TABLE', included: true, whereClause: null, estRows: 0, estBytes: 0, sizeIsEstimate: false, hasLobs: false, isPartitioned: false, isIot: false, columns: [] },
]);
await page.reload({ waitUntil: 'networkidle' });
await page.locator('.tb-vrow').first().waitFor({ timeout: 20000 });
const [zeroRows, zeroSize] = await cellsOf('EMPTY_TABLE');
check(zeroRows === '0', `a genuinely empty table shows 0, not "Unknown" (${zeroRows})`);
check(zeroSize === '0 B', `and a zero size shows as a size (${zeroSize})`);
check((await rowFor('EMPTY_TABLE').locator('.tb-unknown').count()) === 0, 'a known zero is not styled as unknown');
await page.screenshot({ path: path.join(OUT, '03-zero.png') });

check(pageErrors.length === 0, `no uncaught page errors${pageErrors.length ? ': ' + pageErrors[0] : ''}`);

await api('DELETE', `/api/v1/applications/${appId}`);
await api('DELETE', `/api/v1/connections/${connId}`);
await browser.close();

console.log(`\nscreenshots -> ${OUT}`);
console.log(`\n=== ${failures === 0 ? 'ALL CHECKS PASSED' : failures + ' CHECK(S) FAILED'} ===\n`);
process.exit(failures ? 1 : 0);
