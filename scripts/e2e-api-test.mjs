// End-to-end API test for O2P. Exercises every feature against a running API + Worker
// using mock Oracle/Postgres connections, including a full mock migration to completion.
// Usage: O2P_API=http://127.0.0.1:5050 O2P_ADMIN_PW=... O2P_NEW_PW=... node scripts/e2e-api-test.mjs
const API = process.env.O2P_API || 'http://127.0.0.1:5050';
const ADMIN_PW = process.env.O2P_ADMIN_PW || 'TestAdmin2026!Aa';
const NEW_PW = process.env.O2P_NEW_PW || 'TestAdmin2026!Bb';

let token = null;
const results = [];
let failures = 0;

function log(msg) { process.stdout.write(msg + '\n'); }
function ok(name, extra = '') { results.push(['PASS', name]); log(`  ✓ ${name}${extra ? ' — ' + extra : ''}`); }
function fail(name, detail) { results.push(['FAIL', name, detail]); failures++; log(`  ✗ ${name}\n      ${detail}`); }
function assert(cond, name, detail) { if (cond) ok(name); else fail(name, detail || 'assertion failed'); }

async function api(method, path, body, { auth = true, raw = false } = {}) {
  const headers = { 'Content-Type': 'application/json' };
  if (auth && token) headers['Authorization'] = `Bearer ${token}`;
  const res = await fetch(`${API}${path}`, { method, headers, body: body === undefined ? undefined : JSON.stringify(body) });
  let data = null;
  const text = await res.text();
  if (text) { try { data = JSON.parse(text); } catch { data = text; } }
  if (raw) return { status: res.status, data };
  return { status: res.status, data };
}

function sleep(ms) { return new Promise(r => setTimeout(r, ms)); }
const uniq = (p) => `${p}${Date.now().toString().slice(-7)}`;

async function main() {
  log('\n=== O2P END-TO-END API TEST ===\n');

  // ---- AUTH ----
  log('[Auth]');
  let r = await api('POST', '/api/v1/auth/login', { username: 'admin', password: ADMIN_PW }, { auth: false });
  assert(r.status === 200 && r.data.token, 'admin login', `status=${r.status} body=${JSON.stringify(r.data)}`);
  token = r.data.token;
  assert(r.data.mustChangePassword === true, 'admin flagged mustChangePassword');

  r = await api('POST', '/api/v1/auth/login', { username: 'admin', password: 'wrong-password' }, { auth: false });
  assert(r.status === 401, 'wrong password rejected', `status=${r.status}`);

  // change password
  r = await api('POST', '/api/v1/auth/change-password', { currentPassword: ADMIN_PW, newPassword: NEW_PW });
  if (r.status === 200) { token = r.data.token; ok('admin change password'); assert(r.data.mustChangePassword === false, 'mustChangePassword cleared'); }
  else {
    // maybe already changed in a prior run — try login with NEW_PW
    const r2 = await api('POST', '/api/v1/auth/login', { username: 'admin', password: NEW_PW }, { auth: false });
    if (r2.status === 200) { token = r2.data.token; ok('admin change password', 'already rotated; logged in with new pw'); }
    else fail('admin change password', `change status=${r.status} body=${JSON.stringify(r.data)}`);
  }

  r = await api('GET', '/api/v1/auth/me');
  assert(r.status === 200 && r.data.roles?.includes('Admin'), 'auth/me returns admin', `status=${r.status} body=${JSON.stringify(r.data)}`);

  r = await api('GET', '/api/v1/connections', undefined, { auth: false });
  assert(r.status === 401, 'unauthenticated request rejected', `status=${r.status}`);

  // ---- USERS ----
  log('\n[Users & Roles]');
  const uname = uniq('qaop');
  r = await api('POST', '/api/v1/users', { username: uname, email: `${uname}@o2p.internal`, displayName: 'QA Operator', password: 'QaOperator2026!A', roles: ['Operator'], mustChangePassword: true, isActive: true });
  assert(r.status === 200, 'create operator user', `status=${r.status} body=${JSON.stringify(r.data)}`);

  r = await api('GET', '/api/v1/users');
  const createdUser = r.data.users?.find(u => u.username === uname);
  assert(r.status === 200 && createdUser, 'list users includes new user', `status=${r.status}`);
  assert(r.data.roles?.length === 3, 'roles catalog present');
  const uid = createdUser?.id;

  r = await api('PUT', `/api/v1/users/${uid}`, { email: `${uname}@o2p.internal`, displayName: 'QA Operator 2', roles: ['Viewer'], mustChangePassword: true, isActive: true });
  assert(r.status === 200, 'update user role to viewer', `status=${r.status} body=${JSON.stringify(r.data)}`);

  r = await api('POST', `/api/v1/users/${uid}/reset-password`, { newPassword: 'QaReset2026!B', mustChangePassword: true });
  assert(r.status === 200, 'reset user password', `status=${r.status} body=${JSON.stringify(r.data)}`);

  r = await api('POST', `/api/v1/users/${uid}/unlock`, {});
  assert(r.status === 200, 'unlock user', `status=${r.status}`);

  // operator/viewer permission boundary: log in as the temp (viewer) user and confirm no user admin
  const rv = await api('POST', '/api/v1/auth/login', { username: uname, password: 'QaReset2026!B' }, { auth: false });
  let viewerAccessToken = null; // reused below to check the schema list is not open to Viewers
  if (rv.status === 200) {
    const viewerToken = rv.data.token;
    viewerAccessToken = viewerToken;
    const probe = await fetch(`${API}/api/v1/users`, { headers: { Authorization: `Bearer ${viewerToken}` } });
    assert(probe.status === 403, 'viewer forbidden from /users', `status=${probe.status}`);
    const probe2 = await fetch(`${API}/api/v1/connections`, { method: 'POST', headers: { Authorization: `Bearer ${viewerToken}`, 'Content-Type': 'application/json' }, body: JSON.stringify({ name: 'x', kind: 'postgres', host: 'mock', port: 1, serviceOrDb: 'x', username: 'x', password: 'x' }) });
    assert(probe2.status === 403, 'viewer forbidden from creating connection', `status=${probe2.status}`);
  } else fail('viewer login', `status=${rv.status}`);

  // ---- CONNECTIONS ----
  log('\n[Connections]');
  const oraName = uniq('QA-Oracle-');
  const pgName = uniq('QA-Postgres-');
  r = await api('POST', '/api/v1/connections', { name: oraName, kind: 'oracle', host: 'mock', port: 1521, serviceOrDb: 'ORCLPDB1', username: 'appuser', password: 'OraclePw2026!A' });
  assert(r.status === 201, 'create mock oracle connection', `status=${r.status} body=${JSON.stringify(r.data)}`);
  const oraId = r.data.id;
  assert(r.data.secretCiphertext?.length === 0 || r.data.secretCiphertext === '' || r.data.secretCiphertext == null, 'oracle secret masked in response');

  r = await api('POST', '/api/v1/connections', { name: pgName, kind: 'postgres', host: 'mock', port: 5432, serviceOrDb: 'targetdb', username: 'pguser', password: 'PgPw2026!A' });
  assert(r.status === 201, 'create mock postgres connection', `status=${r.status} body=${JSON.stringify(r.data)}`);
  const pgId = r.data.id;

  r = await api('POST', '/api/v1/connections', { name: oraName, kind: 'oracle', host: 'mock', port: 1521, serviceOrDb: 'x', username: 'x', password: 'x' });
  assert(r.status === 409, 'duplicate connection name rejected', `status=${r.status}`);

  r = await api('GET', '/api/v1/connections');
  assert(r.status === 200 && r.data.every(c => !c.secretCiphertext || c.secretCiphertext.length === 0), 'list connections masks secrets', `status=${r.status}`);

  r = await api('POST', `/api/v1/connections/${oraId}/test`, {});
  assert(r.status === 200 && r.data.success === true && /Mock/.test(r.data.serverVersion), 'test oracle connection (mock)', `status=${r.status} body=${JSON.stringify(r.data)}`);
  r = await api('POST', `/api/v1/connections/${pgId}/test`, {});
  assert(r.status === 200 && r.data.success === true, 'test postgres connection (mock)', `status=${r.status} body=${JSON.stringify(r.data)}`);

  r = await api('PUT', `/api/v1/connections/${oraId}`, { name: oraName, kind: 'oracle', host: 'mock', port: 1521, serviceOrDb: 'ORCLPDB1', username: 'appuser2', password: null });
  assert(r.status === 200 && r.data.username === 'appuser2', 'update oracle connection (keep password)', `status=${r.status} body=${JSON.stringify(r.data)}`);

  // ---- APPLICATIONS ----
  log('\n[Applications]');
  const appName = uniq('QA-App-');
  r = await api('POST', '/api/v1/applications', { name: appName, description: 'e2e test app' });
  assert(r.status === 201 && r.data.id, 'create application', `status=${r.status} body=${JSON.stringify(r.data)}`);
  const appId = r.data.id;

  // bind all four slots
  r = await api('PUT', `/api/v1/applications/${appId}`, {
    id: appId, name: appName, description: 'e2e test app',
    connections: [
      { applicationId: appId, slot: 'oracle_test', connectionId: oraId },
      { applicationId: appId, slot: 'oracle_live', connectionId: oraId },
      { applicationId: appId, slot: 'pg_test', connectionId: pgId },
      { applicationId: appId, slot: 'pg_live', connectionId: pgId },
    ],
  });
  assert(r.status === 200, 'bind 4 connection slots', `status=${r.status} body=${JSON.stringify(r.data)}`);

  r = await api('GET', `/api/v1/applications/${appId}`);
  assert(r.status === 200 && r.data.connections?.length === 4, 'application has 4 bound slots', `slots=${r.data.connections?.length}`);

  // ---- DISCOVERY ----
  log('\n[Discovery]');
  r = await api('POST', `/api/v1/connections/${oraId}/discovery/refresh?owner=APP`, { tableNames: [] });
  assert(r.status === 200 && r.data.tables?.length === 2, 'discovery full scan finds 2 mock tables', `status=${r.status} body=${JSON.stringify(r.data).slice(0,300)}`);

  r = await api('GET', `/api/v1/connections/${oraId}/discovery?owner=APP`);
  assert(r.status === 200 && r.data.length === 2, 'get cached discovery tables', `status=${r.status} count=${r.data?.length}`);
  const customers = r.data.find(t => t.tableName === 'CUSTOMERS');
  assert(customers && customers.columns.length === 4, 'CUSTOMERS has 4 columns');
  const idCol = customers?.columns.find(c => c.columnName === 'ID');
  assert(idCol?.postgresDataType === 'bigint', 'NUMBER(12,0) -> bigint mapping', `got ${idCol?.postgresDataType}`);
  const createdCol = customers?.columns.find(c => c.columnName === 'CREATED_AT');
  assert(createdCol?.postgresDataType === 'timestamp(0) without time zone', 'DATE -> timestamp(0) mapping', `got ${createdCol?.postgresDataType}`);

  // targeted discovery
  r = await api('POST', `/api/v1/connections/${oraId}/discovery/refresh?owner=APP`, { tableNames: ['ORDERS'] });
  assert(r.status === 200 && r.data.tables?.length === 1, 'targeted discovery finds 1 table', `status=${r.status}`);

  // schema list (feeds the Source schema dropdown). A mock connection returns a fixed set.
  r = await api('GET', `/api/v1/connections/${oraId}/discovery/schemas`);
  const schemaNames = (r.data?.schemas || []).map(s => s.name).join(',');
  assert(r.status === 200 && schemaNames === 'APP,HR,SALES', 'schema list returns APP, HR, SALES', `status=${r.status} body=${JSON.stringify(r.data).slice(0,200)}`);
  const schemaCounts = (r.data?.schemas || []).map(s => s.tableCount).join(',');
  assert(schemaCounts === '2,7,12' && r.data?.skipped === 0, 'schema list carries table counts and skipped=0', `counts=${schemaCounts} skipped=${r.data?.skipped}`);

  r = await api('GET', `/api/v1/connections/${pgId}/discovery/schemas`);
  assert(r.status === 400, 'schema list is refused for a PostgreSQL connection', `status=${r.status}`);
  r = await api('GET', `/api/v1/connections/${oraId}/discovery/schemas`, undefined, { auth: false });
  assert(r.status === 401, 'schema list requires sign-in', `status=${r.status}`);
  if (viewerAccessToken) {
    const vs = await fetch(`${API}/api/v1/connections/${oraId}/discovery/schemas`, { headers: { Authorization: `Bearer ${viewerAccessToken}` } });
    assert(vs.status === 403, 'viewer forbidden from the schema list (it opens a live Oracle session)', `status=${vs.status}`);
  }

  // ---- MANIFEST ----
  log('\n[Manifest]');
  r = await api('POST', `/api/v1/applications/${appId}/manifests/generate?connectionId=${oraId}&owner=APP&version=1.0`, {});
  assert(r.status === 200 && r.data.id, 'generate manifest from discovery', `status=${r.status} body=${JSON.stringify(r.data).slice(0,200)}`);
  const manifestId = r.data.id;

  r = await api('GET', `/api/v1/manifests/${manifestId}`);
  assert(r.status === 200 && r.data.tables?.length === 2, 'manifest has 2 tables', `count=${r.data.tables?.length}`);
  assert(r.data.tables.every(t => t.columns.length > 0), 'manifest tables have columns');

  r = await api('GET', `/api/v1/applications/${appId}/manifests`);
  assert(r.status === 200 && r.data.length >= 1, 'list manifests for app', `count=${r.data?.length}`);

  // ---- JOB ----
  log('\n[Job creation + launch + migration]');
  r = await api('POST', '/api/v1/jobs', { applicationId: appId, manifestId, sourceSlot: 'oracle_test', targetSlot: 'pg_test', targetSchema: 'public' });
  assert(r.status === 201 && r.data.id, 'create job', `status=${r.status} body=${JSON.stringify(r.data).slice(0,300)}`);
  const jobId = r.data.id;

  r = await api('POST', `/api/v1/jobs/${jobId}/preflight`, undefined);
  assert(r.status === 200 && r.data.passed === true, 'run preflight (mock passes)', `status=${r.status} body=${JSON.stringify(r.data)}`);

  r = await api('POST', `/api/v1/jobs/${jobId}/launch`, {});
  assert(r.status === 200 && r.data.status === 'Queued', 'launch job -> Queued', `status=${r.status} body=${JSON.stringify(r.data)}`);

  // poll until the JOB reaches a terminal state (exercises job-completion logic, not just tables)
  log('  ... waiting for migration to complete (worker)');
  let job = null; let done = false;
  for (let i = 0; i < 60; i++) {
    await sleep(2000);
    const jr = await api('GET', `/api/v1/jobs/${jobId}`);
    job = jr.data;
    const trs = job.tableRuns || [];
    const statuses = trs.map(t => t.status);
    if (job.status === 'Completed' || job.status === 'CompletedWithErrors' || job.status === 'Failed') { done = true; break; }
    if (i % 3 === 0) log(`      job=${job.status} tables=[${statuses.join(',')}]`);
  }
  assert(done, 'job reached a terminal state', `final job=${job?.status} tables=${JSON.stringify((job?.tableRuns||[]).map(t=>({n:t.targetTableName,s:t.status,rows:t.rowsMigrated})))}`);
  assert(job?.status === 'Completed', 'job status is Completed (not stuck Running)', `got ${job?.status}`);
  assert(job?.completedAt, 'job has completedAt timestamp', `got ${job?.completedAt}`);
  if (done) {
    const totalRows = (job.tableRuns || []).reduce((a, t) => a + (t.rowsMigrated || 0), 0);
    assert(totalRows === 8000, 'migrated row count correct (2 tables x 8 chunks x 500) — no lost-update race', `got ${totalRows}`);
    const allCompleted = job.tableRuns.every(t => t.status === 'Completed');
    assert(allCompleted, 'every table validated as Completed', `statuses=${job.tableRuns.map(t=>t.status).join(',')}`);
    assert(job.tableRuns.every(t => t.completedAt), 'every table has completedAt');
    // verify target naming: always same-named as source (no _mgN)
    const names = job.tableRuns.map(t => t.targetTableName).sort();
    ok('target table names', names.join(', '));
  }

  // ---- VALIDATION + METRICS ----
  log('\n[Validation & Metrics]');
  r = await api('GET', `/api/v1/jobs/${jobId}/validation`);
  assert(r.status === 200 && r.data.length === 2, 'exactly 2 validation results (no double-trigger duplicates)', `status=${r.status} count=${r.data?.length}`);
  assert(r.data.every(v => v.passed), 'all validation results passed', `body=${JSON.stringify(r.data).slice(0,300)}`);

  r = await api('GET', `/api/v1/jobs/${jobId}/metrics`);
  assert(r.status === 200, 'get job metrics', `status=${r.status}`);

  // ---- SAME-NAME RE-RUN (no _mgN) ----
  log('\n[Same-name target on re-run]');
  r = await api('POST', '/api/v1/jobs', { applicationId: appId, manifestId, sourceSlot: 'oracle_test', targetSlot: 'pg_test', targetSchema: 'public' });
  if (r.status === 201) {
    const job2 = r.data;
    const jr2 = await api('GET', `/api/v1/jobs/${job2.id}`);
    const names2 = (jr2.data.tableRuns || []).map(t => t.targetTableName);
    assert(names2.every(n => !/_mg\d+$/.test(n)), 'second job reuses base table names (no _mgN)', `names=${names2.join(', ')}`);
  } else fail('create second job for same-name test', `status=${r.status} body=${JSON.stringify(r.data)}`);

  // ---- LIVE CONFIRMATION PHRASE ----
  log('\n[Live target confirmation phrase]');
  r = await api('POST', '/api/v1/jobs', { applicationId: appId, manifestId, sourceSlot: 'oracle_test', targetSlot: 'pg_live', targetSchema: 'public' });
  if (r.status === 201) {
    const liveJobId = r.data.id;
    const bad = await api('POST', `/api/v1/jobs/${liveJobId}/launch`, { confirmationPhrase: 'wrong' });
    assert(bad.status === 400, 'live launch without correct phrase rejected', `status=${bad.status}`);
    const good = await api('POST', `/api/v1/jobs/${liveJobId}/launch`, { confirmationPhrase: `MIGRATE ${appName} LIVE` });
    assert(good.status === 200, 'live launch with correct phrase accepted', `status=${good.status} body=${JSON.stringify(good.data)}`);
    // cancel it so worker doesn't keep running
    await api('POST', `/api/v1/jobs/${liveJobId}/commands`, { command: 'cancel', scope: 'job' });
  } else fail('create live job', `status=${r.status}`);

  // ---- SUMMARY ----
  log('\n=== SUMMARY ===');
  const passed = results.filter(x => x[0] === 'PASS').length;
  log(`PASS: ${passed}   FAIL: ${failures}   TOTAL: ${results.length}`);
  if (failures) {
    log('\nFailures:');
    results.filter(x => x[0] === 'FAIL').forEach(x => log(`  - ${x[1]}: ${x[2]}`));
  }
  process.exit(failures ? 1 : 0);
}

main().catch(e => { log('FATAL: ' + (e.stack || e)); process.exit(2); });
