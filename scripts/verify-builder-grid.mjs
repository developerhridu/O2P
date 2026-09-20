// Verifies the table-selection page (ManifestBuilder / TableGrid) against a real browser.
//
// Seeds a manifest with 5,000 tables through the API, opens the real page, and checks the
// things the redesign is supposed to guarantee: the grid takes the space, the page itself does
// not scroll, only a window of rows is rendered (virtual scrolling), and sort / bulk select /
// filters / popover behave.
//
// Logs in ONCE and reuses the token: the API allows 5 logins per 5 minutes.
//
// Usage: O2P_API=http://127.0.0.1:5050 O2P_UI_URL=http://127.0.0.1:5252 O2P_ADMIN_PW=... node scripts/verify-builder-grid.mjs
import fs from 'node:fs/promises';
import path from 'node:path';
import { pathToFileURL } from 'node:url';

const { chromium } = await import(
  pathToFileURL(path.resolve(process.cwd(), 'web', 'node_modules', 'playwright-core', 'index.mjs')).href
);

// SAFETY: this seeds and deletes data, so it must only ever run against a throwaway API. It has no
// default target and refuses the usual dev ports, so a stray run cannot land on a real environment.
const API = process.env.O2P_API;
const UI = process.env.O2P_UI_URL;
const PASSWORD = process.env.O2P_ADMIN_PW;
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
const CHROME = process.env.CHROME_PATH || 'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe';
const OUT = path.resolve(process.cwd(), 'artifacts', 'builder-grid');
const TABLE_COUNT = 5000;

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

console.log('\n=== TABLE-SELECTION GRID CHECK ===');

// ---- seed ---------------------------------------------------------------------------------
section('seed');
let r = await api('POST', '/api/v1/auth/login', { username: 'admin', password: PASSWORD });
if (r.status !== 200) { console.log(`login failed: ${r.status} ${JSON.stringify(r.data)}`); process.exit(1); }
token = r.data.token;
const authState = r.data;
ok('logged in once');

const stamp = Date.now().toString().slice(-6);
r = await api('POST', '/api/v1/connections', { name: `grid-ora-${stamp}`, kind: 'oracle', host: 'mock', port: 1521, serviceOrDb: 'X', username: 'u', password: 'p' });
const connId = r.data?.id;
r = await api('POST', '/api/v1/applications', { name: `Grid check ${stamp}`, description: 'temporary' });
const appId = r.data?.id;
r = await api('POST', `/api/v1/applications/${appId}/manifests`, { name: `Grid check ${stamp}`, version: 1 });
const manifestId = r.data?.id;
check(!!(connId && appId && manifestId), `created connection, migration and table selection (app ${appId}, selection ${manifestId})`);

const pad = (n) => String(n).padStart(4, '0');
const tables = Array.from({ length: TABLE_COUNT }, (_, i) => ({
  owner: 'PERF',
  tableName: `T_${pad(i + 1)}`,
  included: true,
  whereClause: null,
  estRows: ((i * 7919) % 100000) + 1,
  estBytes: ((i * 104729) % 50000000) + 1024,
  hasLobs: i % 17 === 0,
  isPartitioned: false,
  isIot: false,
  columns: [{ columnName: 'ID', oracleDataType: 'NUMBER', postgresDataType: 'bigint', isNullable: false, isPrimaryKey: false, isExcluded: false }],
}));
r = await api('PUT', `/api/v1/manifests/${manifestId}/tables`, tables);
check(r.status === 200, `seeded ${TABLE_COUNT} tables (HTTP ${r.status})`);

// ---- browser ------------------------------------------------------------------------------
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

const url = `${UI}/applications/${appId}/manifests/${manifestId}/builder`;
await page.goto(url, { waitUntil: 'networkidle' });
await page.locator('.tb-grid').waitFor({ timeout: 20000 });
await page.locator('.tb-vrow').first().waitFor({ timeout: 20000 });

const scroller = page.locator('.tb-scroll');
const rows = () => page.locator('.tb-body .tb-vrow');
const rowCount = () => rows().count();
const firstName = async () => (await rows().first().locator('.tb-cell-name').innerText()).trim();

// ---- layout -------------------------------------------------------------------------------
async function layout(vp) {
  await page.setViewportSize(vp);
  await page.waitForTimeout(300);
  const g = await page.locator('.tb-grid-card').boundingBox();
  const f = await page.locator('.tb-footer').boundingBox();
  const doc = await page.evaluate(() => ({
    scrollH: document.documentElement.scrollHeight,
    innerH: window.innerHeight,
    bodyScrollH: document.body.scrollHeight,
  }));
  check(g.height > vp.height * 0.6, `${vp.width}x${vp.height}: grid is ${Math.round(g.height)}px tall (${Math.round((g.height / vp.height) * 100)}% of the window; the bug was ~28%)`);
  check(Math.abs(f.y + f.height - (g.y + g.height)) <= 2, `${vp.width}x${vp.height}: footer sits at the bottom of the grid card`);
  check(doc.scrollH <= doc.innerH + 1, `${vp.width}x${vp.height}: page itself does not scroll (${doc.scrollH} <= ${doc.innerH})`);
}
section('layout');
await layout({ width: 1440, height: 900 });
await page.screenshot({ path: path.join(OUT, '01-desktop.png') });
await layout({ width: 1280, height: 720 });
await page.screenshot({ path: path.join(OUT, '02-1280x720.png') });
await page.setViewportSize({ width: 1440, height: 900 });
await page.waitForTimeout(300);

// ---- virtualization -----------------------------------------------------------------------
section('virtual scrolling');
const rendered = await rowCount();
const ariaRows = await page.locator('.tb-grid').getAttribute('aria-rowcount');
check(rendered > 5 && rendered < 80, `only ${rendered} of ${TABLE_COUNT} rows are in the DOM`);
check(ariaRows === String(TABLE_COUNT + 1), `aria-rowcount reports ${ariaRows} (${TABLE_COUNT} rows + header)`);
check((await firstName()) === 'T_0001', `first row is T_0001`);

// Row filter must survive scrolling out of view: the value lives in state, not the DOM.
const filterInput = rows().first().locator('.tb-cell-input');
await filterInput.fill('YEAR = 2024');
await scroller.evaluate((el) => { el.scrollTop = el.scrollHeight; });
await page.waitForTimeout(400);
const lastVisible = await rows().last().locator('.tb-cell-name').innerText();
check(lastVisible.trim() === `T_${pad(TABLE_COUNT)}`, `scrolling to the bottom renders the last table (${lastVisible.trim()})`);
await scroller.evaluate((el) => { el.scrollTop = 0; });
await page.waitForTimeout(400);
check((await rows().first().locator('.tb-cell-input').inputValue()) === 'YEAR = 2024', 'a typed row filter survives scrolling away and back');

// ---- sort ---------------------------------------------------------------------------------
section('sorting');
const tableHeader = page.getByRole('columnheader', { name: /^Table/ });
await tableHeader.getByRole('button').click();
check((await tableHeader.getAttribute('aria-sort')) === 'ascending', 'first click sorts ascending (aria-sort)');
await tableHeader.getByRole('button').click();
check((await tableHeader.getAttribute('aria-sort')) === 'descending', 'second click sorts descending');
check((await firstName()) === `T_${pad(TABLE_COUNT)}`, 'descending puts T_5000 first');
await tableHeader.getByRole('button').click();
check((await tableHeader.getAttribute('aria-sort')) === 'none', 'third click clears the sort');
check((await firstName()) === 'T_0001', 'cleared sort restores the original order');

const rowsHeader = page.getByRole('columnheader', { name: /^Rows/ });
await rowsHeader.getByRole('button').click();
await rowsHeader.getByRole('button').click();
const topRows = await rows().first().locator('.tb-num').first().innerText();
check(/^\d/.test(topRows.replace(/,/g, '')) && Number(topRows.replace(/,/g, '')) >= 99000, `sorting rows descending puts the largest first (${topRows})`);
await rowsHeader.getByRole('button').click(); // back to none

// ---- bulk select + filters ----------------------------------------------------------------
section('bulk select and filters');
const headerCheck = page.locator('.tb-head input[type=checkbox]');
const footer = page.locator('.tb-footer');
const footerText = async () => (await footer.innerText()).replace(/\s+/g, ' ');

check(/5,000 selected of 5,000/.test(await footerText()), `footer starts at "${(await footerText()).slice(0, 34)}"`);
await headerCheck.click(); // deselect all
check(/0 selected of 5,000/.test(await footerText()), 'header checkbox deselects everything');

await page.getByLabel('Search tables').fill('T_00');
await page.waitForTimeout(200);
const segText = async () => (await page.locator('.tb-seg').innerText()).replace(/\s+/g, ' ');
check(/All 99 Selected 0 Unselected 99/.test(await segText()), `search "T_00" matches 99 tables (${await segText()})`);
check(/Showing 99 of 5,000/.test(await page.locator('.tb-toolbar').last().innerText()), 'readout says "Showing 99 of 5,000"');

await headerCheck.click(); // select all SHOWN
check(/99 selected of 5,000/.test(await footerText()), 'select-all with a search active selects only the matches (99, not 5,000)');
check(await headerCheck.isChecked(), 'header checkbox is checked when every shown row is selected');

await page.getByLabel('Search tables').fill('');
await page.waitForTimeout(200);
check(await headerCheck.evaluate((el) => el.indeterminate), 'header checkbox is indeterminate when only some rows are selected');
check(/All 5000 Selected 99 Unselected 4901/.test(await segText()), `counts add up: ${await segText()}`);

await page.getByRole('button', { name: /^Selected/ }).click();
await page.waitForTimeout(200);
check(/Showing 99 of 5,000/.test(await page.locator('.tb-toolbar').last().innerText()), 'the Selected filter shows only the 99');
check((await rowCount()) <= 99, 'and only those rows are rendered');
await page.getByRole('button', { name: /^Unselected/ }).click();
await page.waitForTimeout(200);
check(/Showing 4,901 of 5,000/.test(await page.locator('.tb-toolbar').last().innerText()), 'the Unselected filter shows the other 4,901');
await page.getByRole('button', { name: /^All/ }).click();

// empty state when a filter hides everything
await page.getByLabel('Search tables').fill('zzz-no-such-table');
await page.waitForTimeout(200);
check(await page.getByText('No tables match').isVisible(), 'a search with no hits shows the "No tables match" state');
await page.getByRole('button', { name: /clear search and filter/i }).click();
await page.waitForTimeout(200);
check((await rowCount()) > 5, 'clearing brings the rows back');

// ---- popover ------------------------------------------------------------------------------
section('add-tables popover');
const addBtn = page.locator('button[aria-haspopup="dialog"]');
await addBtn.click();
const dialog = page.getByRole('dialog', { name: /add specific tables/i });
check(await dialog.isVisible(), 'opens on click');
check(await page.evaluate(() => document.activeElement?.tagName === 'TEXTAREA'), 'the textarea takes focus');
const dlgBox = await dialog.boundingBox();
const btnBox = await addBtn.boundingBox();
check(dlgBox.y > btnBox.y + btnBox.height - 2, 'it opens below its button, not clipped behind the grid');
await page.screenshot({ path: path.join(OUT, '03-popover.png') });
await page.keyboard.press('Escape');
await page.waitForTimeout(150);
check(!(await dialog.isVisible()), 'Escape closes it');
check(await addBtn.evaluate((el) => document.activeElement === el), 'focus returns to the button');
await addBtn.click();
await page.mouse.click(900, 700);
await page.waitForTimeout(150);
check(!(await dialog.isVisible()), 'an outside click closes it');

// ---- narrow -------------------------------------------------------------------------------
section('narrow window (800px)');
await page.setViewportSize({ width: 800, height: 900 });
await page.waitForTimeout(400);
const sc = await scroller.evaluate((el) => ({ sw: el.scrollWidth, cw: el.clientWidth }));
check(sc.sw > sc.cw, `the grid scrolls horizontally (${sc.sw}px of content in ${sc.cw}px)`);
await scroller.evaluate((el) => { el.scrollLeft = 160; });
await page.waitForTimeout(200);
const headTableCell = await page.locator('.tb-head .tb-cell').nth(2).boundingBox();
const rowTableCell = await rows().first().locator('.tb-cell').nth(2).boundingBox();
check(Math.abs(headTableCell.x - rowTableCell.x) <= 1, `header and rows stay aligned after horizontal scroll (${Math.round(headTableCell.x)} vs ${Math.round(rowTableCell.x)})`);
await page.screenshot({ path: path.join(OUT, '04-narrow.png') });
await page.setViewportSize({ width: 1440, height: 900 });
await page.waitForTimeout(300);
await scroller.evaluate((el) => { el.scrollLeft = 0; });

// ---- save round trip ----------------------------------------------------------------------
section('save round trip');
await page.getByRole('button', { name: /save selection/i }).click();
await page.waitForTimeout(3000);
r = await api('GET', `/api/v1/manifests/${manifestId}`);
const saved = r.data?.tables ?? [];
const t1 = saved.find((t) => t.tableName === 'T_0001');
check(saved.length === TABLE_COUNT, `all ${TABLE_COUNT} tables saved (${saved.length})`);
check(t1?.whereClause === 'YEAR = 2024', `the row filter reached the API (${JSON.stringify(t1?.whereClause)})`);
check(saved.filter((t) => t.included).length === 99, `only the 99 selected tables are marked included (${saved.filter((t) => t.included).length})`);

check(pageErrors.length === 0, `no uncaught page errors${pageErrors.length ? ': ' + pageErrors[0] : ''}`);

// ---- cleanup ------------------------------------------------------------------------------
await api('DELETE', `/api/v1/applications/${appId}`);
await api('DELETE', `/api/v1/connections/${connId}`);
await browser.close();

console.log(`\nscreenshots -> ${OUT}`);
console.log(`\n=== ${failures === 0 ? 'ALL CHECKS PASSED' : failures + ' CHECK(S) FAILED'} ===\n`);
process.exit(failures ? 1 : 0);
