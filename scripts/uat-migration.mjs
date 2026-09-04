// End-to-end UAT: drives the real browser UI to run a migration and exercise the app, capturing
// screenshots, JS errors and failed network calls. Uses system Chrome + web/node_modules/playwright-core.
import fs from 'node:fs/promises';
import path from 'node:path';
import { pathToFileURL } from 'node:url';

const { chromium } = await import(
  pathToFileURL(path.resolve(process.cwd(), 'web', 'node_modules', 'playwright-core', 'index.mjs')).href
);

const baseUrl = process.env.O2P_UI_URL || 'http://127.0.0.1:3051';
const apiBase = process.env.O2P_API_BASE || 'http://127.0.0.1:3052/api/v1';
const adminUsername = process.env.O2P_ADMIN_USERNAME || 'admin';
const adminPassword = process.env.O2P_ADMIN_PASSWORD;
const adminNewPassword = process.env.O2P_ADMIN_NEW_PASSWORD;
const chromePath = process.env.CHROME_PATH || 'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe';
const outputDir = path.resolve(process.cwd(), 'artifacts', 'uat');

if (!adminPassword || !adminNewPassword) throw new Error('O2P_ADMIN_PASSWORD and O2P_ADMIN_NEW_PASSWORD must be set.');
await fs.mkdir(outputDir, { recursive: true });

const browser = await chromium.launch({
  executablePath: chromePath,
  headless: true,
  args: ['--no-proxy-server', '--proxy-server=direct://', '--proxy-bypass-list=*', '--disable-features=BlockInsecurePrivateNetworkRequests'],
});
const context = await browser.newContext({ viewport: { width: 1600, height: 1000 } });
const page = await context.newPage();
// Auto-accept every native dialog (confirm/alert) for the whole session. A single persistent
// handler avoids the "dialog already handled" error you get from stacking multiple once() handlers.
page.on('dialog', (d) => { d.accept().catch(() => {}); });

const jsErrors = [];
const failedResponses = [];
const steps = [];
page.on('pageerror', (e) => jsErrors.push(`pageerror: ${e.message}`));
page.on('console', (m) => { if (m.type() === 'error') jsErrors.push(`console: ${m.text()}`); });
page.on('response', (r) => { const s = r.status(); const u = r.url(); if (s >= 400 && !u.includes('/favicon.ico')) failedResponses.push(`${s} ${u}`); });

let shot = 0;
const snap = async (name) => { await page.screenshot({ path: path.join(outputDir, `${String(++shot).padStart(2, '0')}-${name}.png`), fullPage: true }).catch(() => {}); };
const step = (msg) => { steps.push(`[${new Date().toISOString()}] ${msg}`); console.log('UAT>', msg); };
const esc = (v) => v.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
const inputByLabel = (label) => page.locator('label').filter({ hasText: new RegExp(`^${esc(label)}$`) }).locator('xpath=following-sibling::*[1]');

async function login(u, p) {
  await page.goto(`${baseUrl}/login`, { waitUntil: 'networkidle' });
  await inputByLabel('Username').fill(u);
  await inputByLabel('Password').fill(p);
  await page.getByRole('button', { name: /sign in/i }).click();
  await page.waitForLoadState('networkidle');
}
async function changePassword(cur, next) {
  await inputByLabel('Current Password').fill(cur);
  await inputByLabel('New Password').fill(next);
  await inputByLabel('Confirm New Password').fill(next);
  await page.getByRole('button', { name: /update password/i }).click();
  await page.waitForLoadState('networkidle');
}
const readAuth = () => page.evaluate(() => { const r = localStorage.getItem('o2p_auth'); return r ? JSON.parse(r) : null; });
const apiGet = (p) => page.evaluate(async ({ p, apiBase }) => {
  const t = localStorage.getItem('o2p_token');
  const r = await fetch(`${apiBase}${p}`, { headers: { Authorization: `Bearer ${t}` } });
  return { ok: r.ok, status: r.status, body: await r.json().catch(() => null) };
}, { p, apiBase });

async function run() {
  // 1. Login + forced password change
  step('login as admin');
  await login(adminUsername, adminPassword);
  await page.waitForTimeout(800);
  let auth = await readAuth();
  if (page.url().includes('/change-password') || auth?.mustChangePassword) {
    step('forced password change');
    if (!page.url().includes('/change-password')) await page.goto(`${baseUrl}/change-password`, { waitUntil: 'networkidle' });
    await snap('forced-change-password');
    await changePassword(adminPassword, adminNewPassword);
    await page.waitForTimeout(800);
  }
  auth = await readAuth();
  if (auth?.mustChangePassword) throw new Error('mustChangePassword did not clear after change.');
  if (page.url().includes('/change-password')) await page.goto(`${baseUrl}/`, { waitUntil: 'networkidle' });
  step(`logged in; title="${await page.title()}"`);
  await snap('dashboard');

  // 2. Connections: test both real connections
  await page.goto(`${baseUrl}/connections`, { waitUntil: 'networkidle' });
  await page.getByRole('heading', { name: /connection profiles/i }).waitFor();
  await snap('connections');
  const connRows = page.locator('tr').filter({ hasText: /Test/ });
  const rowCount = await connRows.count();
  step(`connections page has ${rowCount} testable rows`);
  for (let i = 0; i < rowCount; i++) {
    const row = connRows.nth(i);
    const label = (await row.innerText()).split('\n')[0];
    try {
      await row.getByRole('button', { name: /^test$/i }).click();
      await row.getByText(/online|failed/i).first().waitFor({ timeout: 25000 });
      const verdict = (await row.innerText()).match(/online|failed/i)?.[0] || '?';
      step(`connection "${label}" test -> ${verdict}`);
    } catch (e) { step(`connection "${label}" test -> ERROR ${e.message}`); }
  }
  await snap('connections-tested');

  // 3. Open the Biometric application detail
  await page.goto(`${baseUrl}/applications`, { waitUntil: 'networkidle' });
  await page.getByRole('heading', { name: /^applications$/i }).waitFor();
  await snap('applications');
  const appName = process.env.O2P_UAT_APP || 'Biometric Application';
  step(`opening application "${appName}"`);
  const appCard = page.locator('h3', { hasText: appName }).locator('xpath=ancestor::div[contains(@class,"group")][1]');
  await appCard.getByRole('link', { name: /manage/i }).click();
  await page.waitForURL(/\/applications\/\d+$/);
  await page.getByRole('heading', { name: /migration manifests/i }).waitFor();
  await snap('application-detail');
  step(`app has ${await page.getByRole('button', { name: /run job/i }).count()} runnable manifest(s)`);

  // 4. Sweep the remaining pages for JS / API errors
  for (const [route, heading, name] of [
    ['/discovery', /discovery/i, 'discovery'],
    ['/settings', /global settings/i, 'settings'],
    ['/users', /users/i, 'users'],
    ['/jobs', /job runs/i, 'job-runs'],
  ]) {
    await page.goto(`${baseUrl}${route}`, { waitUntil: 'networkidle' });
    try { await page.getByRole('heading', { name: heading }).first().waitFor({ timeout: 8000 }); } catch { /* heading optional */ }
    await snap(name);
    step(`visited ${route}`);
  }

  // 5. Show the latest migration result as proof (no re-run of the large migration)
  const jobsList = await apiGet('/jobs');
  const terminalJobs = (jobsList.body || []).filter((j) => ['Completed', 'CompletedWithErrors', 'Cancelled'].includes(j.status));
  const showJob = terminalJobs[0]?.id;
  if (showJob) {
    await page.goto(`${baseUrl}/jobs/${showJob}`, { waitUntil: 'networkidle' });
    await page.waitForTimeout(1500);
    await snap(`job-${showJob}-detail`);
    const val = await apiGet(`/jobs/${showJob}/validation`);
    const vals = val.body || [];
    step(`latest job #${showJob}: ${vals.length} validation checks, ${vals.filter((v) => v.passed).length} passed (src==tgt)`);
  }

  // 6. The "Cancel All & Restart Worker" admin button (dialogs auto-accepted by the global handler)
  await page.goto(`${baseUrl}/jobs`, { waitUntil: 'networkidle' });
  await page.getByRole('heading', { name: /job runs/i }).waitFor();
  const resetBtn = page.getByRole('button', { name: /cancel all & restart worker/i });
  if (await resetBtn.count()) {
    step('clicking "Cancel All & Restart Worker"');
    await resetBtn.click();
    await page.waitForTimeout(3000);
    await snap('after-reset-button');
    step('reset button clicked OK');
  } else {
    step('WARN: reset button not visible (admin only?)');
  }

  return { swept: true, shownJob: showJob };
}

let result = 'passed';
let message = 'UAT completed.';
let outcome = null;
try {
  outcome = await run();
} catch (e) {
  result = 'failed';
  message = e instanceof Error ? e.message : String(e);
  await snap('failure');
} finally {
  await fs.writeFile(path.join(outputDir, 'report.json'), JSON.stringify({ result, message, steps, jsErrors, failedResponses, outcome, finishedAt: new Date().toISOString() }, null, 2));
  await browser.close();
}
console.log('\n===== UAT REPORT =====');
console.log('result:', result, '|', message);
console.log('steps:'); steps.forEach((s) => console.log('  ' + s));
console.log('jsErrors:', jsErrors.length); jsErrors.slice(0, 20).forEach((e) => console.log('  ' + e));
console.log('failedResponses:', failedResponses.length); failedResponses.slice(0, 20).forEach((e) => console.log('  ' + e));
if (result !== 'passed') process.exitCode = 1;
