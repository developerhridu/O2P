import fs from 'node:fs/promises';
import path from 'node:path';
import { pathToFileURL } from 'node:url';

const { chromium } = await import(
  pathToFileURL(path.resolve(process.cwd(), 'web', 'node_modules', 'playwright-core', 'index.mjs')).href
);

const baseUrl = process.env.O2P_UI_URL || 'http://127.0.0.1:3051';
const adminUsername = process.env.O2P_ADMIN_USERNAME || 'admin';
const adminPassword = process.env.O2P_ADMIN_PASSWORD;
const adminNewPassword = process.env.O2P_ADMIN_NEW_PASSWORD;
const chromePath = process.env.CHROME_PATH || 'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe';
const outputDir = path.resolve(process.cwd(), 'artifacts', 'qa-ui');

if (!adminPassword || !adminNewPassword) {
  throw new Error('O2P_ADMIN_PASSWORD and O2P_ADMIN_NEW_PASSWORD must be set.');
}

await fs.mkdir(outputDir, { recursive: true });

const browser = await chromium.launch({
  executablePath: chromePath,
  headless: true,
  args: [
    '--no-proxy-server',
    '--proxy-server=direct://',
    '--proxy-bypass-list=*',
    '--disable-features=BlockInsecurePrivateNetworkRequests',
  ],
});

const context = await browser.newContext({
  viewport: { width: 1600, height: 1000 },
});

const page = await context.newPage();
const jsErrors = [];
const failedResponses = [];
const actions = [];

page.on('pageerror', (error) => {
  jsErrors.push(`pageerror: ${error.message}`);
});

page.on('console', (msg) => {
  if (msg.type() === 'error') {
    jsErrors.push(`console: ${msg.text()}`);
  }
});

page.on('response', (response) => {
  const status = response.status();
  const url = response.url();
  if (status >= 400 && !url.includes('/favicon.ico')) {
    failedResponses.push(`${status} ${url}`);
  }
});

async function snap(name) {
  await page.screenshot({ path: path.join(outputDir, name), fullPage: true });
}

function escapeRegex(value) {
  return value.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
}

function inputByLabel(label) {
  return page
    .locator('label')
    .filter({ hasText: new RegExp(`^${escapeRegex(label)}$`) })
    .locator('xpath=following-sibling::*[1]');
}

async function login(username, password) {
  await page.goto(`${baseUrl}/login`, { waitUntil: 'networkidle' });
  await inputByLabel('Username').fill(username);
  await inputByLabel('Password').fill(password);
  await page.getByRole('button', { name: /sign in/i }).click();
  await page.waitForLoadState('networkidle');
}

async function changePassword(currentPassword, newPassword) {
  await inputByLabel('Current Password').fill(currentPassword);
  await inputByLabel('New Password').fill(newPassword);
  await inputByLabel('Confirm New Password').fill(newPassword);
  await page.getByRole('button', { name: /update password/i }).click();
  await page.waitForLoadState('networkidle');
}

async function ensureAdminSession() {
  await login(adminUsername, adminPassword);
  await page.waitForTimeout(1000);
  const auth = await readAuthState();

  if (page.url().includes('/change-password') || auth?.mustChangePassword) {
    actions.push('admin_forced_password_change');
    if (!page.url().includes('/change-password')) {
      await page.goto(`${baseUrl}/change-password`, { waitUntil: 'networkidle' });
    }
    await snap('01-admin-change-password.png');
    await changePassword(adminPassword, adminNewPassword);
  }

  await page.waitForTimeout(1000);
  const postChangeAuth = await readAuthState();
  if (postChangeAuth?.mustChangePassword) {
    throw new Error('Admin password change did not clear mustChangePassword.');
  }
  if (page.url().includes('/change-password')) {
    await page.goto(`${baseUrl}/`, { waitUntil: 'networkidle' });
  }
}

async function signOut() {
  await page.getByRole('button', { name: /sign out/i }).click();
  await page.waitForURL(/\/login$/);
}

async function readAuthState() {
  return page.evaluate(() => {
    const raw = localStorage.getItem('o2p_auth');
    return raw ? JSON.parse(raw) : null;
  });
}

function uniqueId(prefix) {
  return `${prefix}-${Date.now().toString().slice(-8)}`;
}

async function run() {
  const tempUser = uniqueId('qauser');
  const tempUserEmail = `${tempUser}@o2p.internal`;
  const tempUserPassword = 'QaTempUser2026!A';
  const tempUserResetPassword = 'QaResetUser2026!B';
  const tempUserFinalPassword = 'QaFinalUser2026!C';
  const tempConnection = uniqueId('QA Connection');
  const tempApplication = uniqueId('QA Application');

  await ensureAdminSession();

  if (!(await page.title()).includes('O2P Migration Studio')) {
    throw new Error(`Unexpected page title: ${await page.title()}`);
  }

  await snap('02-dashboard.png');

  await page.goto(`${baseUrl}/users`, { waitUntil: 'networkidle' });
  await page.getByRole('heading', { name: /users & roles/i }).waitFor();
  await snap('03-users.png');

  await page.getByRole('button', { name: /new user/i }).click();
  await inputByLabel('Username').fill(tempUser);
  await inputByLabel('Display Name').fill('QA Operator');
  await inputByLabel('Email').fill(tempUserEmail);
  await inputByLabel('Temporary Password').fill(tempUserPassword);
  await page.getByLabel('Operator').check();
  await page.getByRole('button', { name: /save user/i }).click();
  const userRow = page.locator('tr', { hasText: tempUser });
  await userRow.waitFor();
  actions.push('created_temp_user');
  await userRow.getByRole('button', { name: /edit/i }).click();
  await page.locator('label:has-text("Force password change") input').check();
  await page.getByRole('button', { name: /save user/i }).click();
  await userRow.getByText(/must change/i).waitFor();
  actions.push('edited_temp_user');

  await userRow.getByRole('button', { name: /reset password/i }).click();
  await inputByLabel('New Temporary Password').fill(tempUserResetPassword);
  await page.locator('div.fixed.inset-0').last().getByRole('button', { name: /^reset password$/i }).click();
  await userRow.getByText(/must change/i).waitFor();
  actions.push('reset_temp_user_password');

  await signOut();
  await login(tempUser, tempUserResetPassword);
  await page.waitForTimeout(1000);
  const tempAuth = await readAuthState();
  if (!page.url().includes('/change-password') && !tempAuth?.mustChangePassword) {
    throw new Error('Temp user was not forced to change password.');
  }
  if (!page.url().includes('/change-password')) {
    await page.goto(`${baseUrl}/change-password`, { waitUntil: 'networkidle' });
  }
  await snap('04-temp-user-change-password.png');
  await changePassword(tempUserResetPassword, tempUserFinalPassword);
  await page.waitForURL((url) => url.pathname === '/');
  await page.getByRole('link', { name: /connections/i }).waitFor();
  if (await page.getByRole('link', { name: /users/i }).count()) {
    throw new Error('Non-admin user should not see Users navigation.');
  }
  actions.push('verified_temp_user_role_restriction');

  await signOut();
  await login(adminUsername, adminNewPassword);
  await page.waitForURL((url) => url.pathname === '/');

  await page.goto(`${baseUrl}/connections`, { waitUntil: 'networkidle' });
  await page.getByRole('heading', { name: /connection profiles/i }).waitFor();
  await snap('05-connections.png');

  await page.getByRole('button', { name: /new database/i }).click();
  await inputByLabel('Profile Name').fill(tempConnection);
  await inputByLabel('Database Engine').selectOption('postgres');
  await inputByLabel('Host Address / IP').fill('127.0.0.1');
  await inputByLabel('Port').fill('5432');
  await inputByLabel('Database Name').fill('postgres');
  await inputByLabel('Username').fill('qa_validation');
  await page.locator('label:has-text("Password")').locator('xpath=following-sibling::input[1]').fill('QaPlaceholder2026!D');
  await page.getByRole('button', { name: /^save$/i }).click();
  await page.getByText(tempConnection).waitFor();
  actions.push('created_temp_connection');

  const connectionRow = page.locator('tr', { hasText: tempConnection });
  await connectionRow.getByRole('button', { name: /test/i }).click();
  await connectionRow.getByText(/failed|online/i).first().waitFor({ timeout: 20000 });
  actions.push('tested_temp_connection');

  await page.goto(`${baseUrl}/applications`, { waitUntil: 'networkidle' });
  await page.getByRole('heading', { name: /^applications$/i }).waitFor();
  await snap('06-applications.png');

  await page.getByRole('button', { name: /new migration/i }).click();
  await inputByLabel('Application Name').fill(tempApplication);
  await inputByLabel('Description').fill('Temporary application created by Chrome QA.');
  await page.getByRole('button', { name: /^create$/i }).click();
  await page.getByText(tempApplication).waitFor();
  actions.push('created_temp_application');

  const appCard = page.locator('h3', { hasText: tempApplication }).locator('xpath=ancestor::div[contains(@class,"group")][1]');
  await appCard.getByRole('link', { name: /manage/i }).click();
  await page.waitForURL(/\/applications\/\d+$/);
  await page.getByRole('heading', { name: tempApplication }).waitFor();
  await snap('07-application-detail.png');

  await page.getByRole('button', { name: /^choose$/i }).first().click();
  await page.getByRole('heading', { name: /assign connection slot/i }).waitFor();
  const select = page.locator('select').last();
  const options = await select.locator('option').allTextContents();
  const viable = options.find((option) => option.includes('Biometric') || option.includes('Oracle') || option.includes('Postgres'));
  if (viable) {
    await select.selectOption({ label: viable });
  } else if (options.length > 1) {
    await select.selectOption({ index: 1 });
  } else {
    throw new Error('No connection options available for application slot assignment.');
  }
  await page.getByRole('button', { name: /^choose$/i }).last().click();
  await page.locator('div.fixed.inset-0').waitFor({ state: 'detached' });
  await page.getByRole('button', { name: /unassign/i }).first().waitFor();
  actions.push('assigned_application_slot');

  await page.goto(`${baseUrl}/settings`, { waitUntil: 'networkidle' });
  await page.getByRole('heading', { name: /global settings/i }).waitFor();
  await snap('08-settings.png');
  const maxConcurrentTables = inputByLabel('Max Concurrent Tables');
  await maxConcurrentTables.fill('5');
  await page.getByRole('button', { name: /save settings/i }).click();
  await page.getByText(/settings saved/i).waitFor();
  await page.reload({ waitUntil: 'networkidle' });
  if ((await maxConcurrentTables.inputValue()) !== '5') {
    throw new Error('Settings value did not persist after reload.');
  }
  actions.push('saved_settings');

  await page.goto(`${baseUrl}/applications`, { waitUntil: 'networkidle' });
  const tempAppCard = page.locator('h3', { hasText: tempApplication }).locator('xpath=ancestor::div[contains(@class,"group")][1]');
  page.once('dialog', (dialog) => dialog.accept());
  await tempAppCard.getByTitle('Delete migration').click();
  await page.getByText(tempApplication).waitFor({ state: 'detached' });
  actions.push('deleted_temp_application');

  await page.goto(`${baseUrl}/connections`, { waitUntil: 'networkidle' });
  const tempConnectionRow = page.locator('tr', { hasText: tempConnection });
  page.once('dialog', (dialog) => dialog.accept());
  await tempConnectionRow.getByRole('button', { name: /delete/i }).click();
  await page.getByText(tempConnection).waitFor({ state: 'detached' });
  actions.push('deleted_temp_connection');

  await snap('09-final-dashboard.png');
}

let result = 'passed';
let message = 'QA completed successfully.';

try {
  await run();
} catch (error) {
  result = 'failed';
  message = error instanceof Error ? error.message : String(error);
  await snap('99-failure.png').catch(() => {});
} finally {
  const report = {
    result,
    message,
    baseUrl,
    actions,
    jsErrors,
    failedResponses,
    finishedAt: new Date().toISOString(),
  };
  await fs.writeFile(path.join(outputDir, 'report.json'), JSON.stringify(report, null, 2));
  await browser.close();
}

if (result !== 'passed') {
  throw new Error(message);
}

if (jsErrors.length || failedResponses.length) {
  throw new Error(`QA completed with browser issues. JS errors: ${jsErrors.length}, failed responses: ${failedResponses.length}`);
}
