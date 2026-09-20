// Visual check for the icon rail: logs in, screenshots the sidebar at desktop and
// mobile widths, hovers an item to capture the tooltip, and asserts the geometry that
// the CSS is supposed to produce.
import fs from 'node:fs/promises';
import path from 'node:path';
import { pathToFileURL } from 'node:url';

const { chromium } = await import(
  pathToFileURL(path.resolve(process.cwd(), 'web', 'node_modules', 'playwright-core', 'index.mjs')).href
);

const baseUrl = process.env.O2P_UI_URL || 'http://127.0.0.1:5151';
const password = process.env.O2P_ADMIN_PW || 'TestAdmin2026!Bb';
const chromePath = process.env.CHROME_PATH || 'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe';
const outDir = path.resolve(process.cwd(), 'artifacts', 'sidebar');
await fs.mkdir(outDir, { recursive: true });

let failures = 0;
const ok = (m) => console.log('  \u2713 ' + m);
const bad = (m) => { failures++; console.log('  \u2717 ' + m); };
const check = (cond, m) => cond ? ok(m) : bad(m);

const browser = await chromium.launch({
  executablePath: chromePath,
  headless: true,
  args: ['--no-proxy-server', '--proxy-server=direct://', '--proxy-bypass-list=*'],
});
const page = await browser.newPage({ viewport: { width: 1440, height: 900 } });

console.log('\n=== SIDEBAR RAIL CHECK ===\n');

await page.goto(baseUrl, { waitUntil: 'networkidle' });
if (page.url().includes('/login')) {
  await page.getByLabel(/username/i).or(page.locator('input[type="text"]')).first().fill('admin');
  await page.locator('input[type="password"]').first().fill(password);
  await page.getByRole('button', { name: /sign in/i }).click();
  await page.waitForURL((u) => !u.pathname.includes('/login'), { timeout: 20000 });
}
ok('signed in');

const sidebar = page.locator('aside.sidebar');
await sidebar.waitFor();

// --- geometry -------------------------------------------------------------
const box = await sidebar.boundingBox();
check(box.width > 60 && box.width < 90, `rail is narrow (${Math.round(box.width)}px)`);

const items = page.locator('aside.sidebar nav .rail-item');
const count = await items.count();
check(count === 6, `nav has ${count} items`);

// The original bug: items shared rows. Every item must now have a distinct top edge.
const tops = [];
for (let i = 0; i < count; i++) tops.push(Math.round((await items.nth(i).boundingBox()).y));
check(new Set(tops).size === count, `all ${count} items stack in one column (tops: ${tops.join(', ')})`);

const lefts = [];
for (let i = 0; i < count; i++) lefts.push(Math.round((await items.nth(i).boundingBox()).x));
check(new Set(lefts).size === 1, 'all items share one x position');

// --- accessible names -----------------------------------------------------
for (const name of ['Dashboard', 'Databases', 'Migrations', 'Runs', 'Settings', 'Change Password', 'Sign Out']) {
  const n = await page.getByRole(name === 'Change Password' || name === 'Sign Out' ? 'button' : 'link', { name, exact: true }).count();
  check(n > 0, `"${name}" reachable by accessible name`);
}

await page.screenshot({ path: path.join(outDir, '01-desktop.png') });

// --- tooltip --------------------------------------------------------------
const migrations = page.getByRole('link', { name: 'Migrations', exact: true });
const label = migrations.locator('.rail-label');
check((await label.evaluate((el) => getComputedStyle(el).opacity)) === '0', 'label hidden before hover');

await migrations.hover();
await page.waitForTimeout(350);
check((await label.evaluate((el) => getComputedStyle(el).opacity)) === '1', 'label visible on hover');

// The gotcha the plan called out: overflow on the sidebar would clip this.
const lb = await label.boundingBox();
check(lb.x > box.x + box.width - 2, `label sits outside the rail (label x=${Math.round(lb.x)}, rail ends ${Math.round(box.x + box.width)})`);
check(lb.width > 0 && lb.height > 0, 'label has real size (not clipped away)');
const clipped = await sidebar.evaluate((el) => {
  const s = getComputedStyle(el);
  return s.overflowX !== 'visible' || s.overflowY !== 'visible';
});
check(!clipped, 'sidebar does not clip its overflow');

await page.screenshot({ path: path.join(outDir, '02-hover.png') });

// --- keyboard -------------------------------------------------------------
await migrations.focus();
await page.waitForTimeout(250);
check((await label.evaluate((el) => getComputedStyle(el).opacity)) === '1', 'label visible on keyboard focus');

// --- mobile ---------------------------------------------------------------
await page.setViewportSize({ width: 800, height: 900 });
await page.waitForTimeout(400);
const mBox = await sidebar.boundingBox();
check(mBox.width > 700, `below 900px the bar spans the width (${Math.round(mBox.width)}px)`);
const mTops = [];
for (let i = 0; i < count; i++) mTops.push(Math.round((await items.nth(i).boundingBox()).y));
check(new Set(mTops).size === 1, 'mobile: icons sit in one row');

await page.getByRole('link', { name: 'Runs', exact: true }).hover();
await page.waitForTimeout(350);
const mLabel = page.getByRole('link', { name: 'Runs', exact: true }).locator('.rail-label');
const mlb = await mLabel.boundingBox();
const runsBox = await page.getByRole('link', { name: 'Runs', exact: true }).boundingBox();
check(mlb.y > runsBox.y + runsBox.height - 2, 'mobile: label drops below the icon');

await page.screenshot({ path: path.join(outDir, '03-mobile.png') });

console.log(`\nscreenshots -> ${outDir}`);
console.log(`\n=== ${failures === 0 ? 'ALL CHECKS PASSED' : failures + ' CHECK(S) FAILED'} ===\n`);

await browser.close();
process.exit(failures ? 1 : 0);
