import { commandLabel } from './labels';

// Dev: relative /api/v1 is proxied by Vite to the API. Override with VITE_API_BASE_URL for static/prod hosts.
export const API_BASE = import.meta.env.VITE_API_BASE_URL || '/api/v1';
const TOKEN_KEY = 'o2p_token';
const AUTH_KEY = 'o2p_auth';
export const AUTH_EVENT = 'o2p-auth-changed';

export type AuthState = {
  token: string;
  userId: string;
  username: string;
  email?: string | null;
  displayName?: string | null;
  roles: string[];
  mustChangePassword: boolean;
  isActive: boolean;
  lastLoginAt?: string | null;
};

export function getHeaders() {
  const token = localStorage.getItem(TOKEN_KEY);
  return {
    'Content-Type': 'application/json',
    ...(token ? { Authorization: `Bearer ${token}` } : {})
  };
}

function setAuthState(data: AuthState) {
  localStorage.setItem(TOKEN_KEY, data.token);
  localStorage.setItem(AUTH_KEY, JSON.stringify(data));
  window.dispatchEvent(new Event(AUTH_EVENT));
}

export function getAuthState(): AuthState | null {
  const raw = localStorage.getItem(AUTH_KEY);
  if (!raw) return null;

  try {
    return JSON.parse(raw);
  } catch {
    return null;
  }
}

async function readErrorMessage(res: Response, fallback: string): Promise<string> {
  try {
    const data = await res.json();
    if (typeof data === 'string' && data.trim()) return data;
    if (data && typeof data.message === 'string' && data.message.trim()) return data.message;
    if (data && typeof data.title === 'string' && data.title.trim()) return data.title;
  } catch {
    // response body wasn't JSON (or was empty) - fall through to the generic message
  }
  return fallback;
}

export function hasRole(role: string) {
  const auth = getAuthState();
  return !!auth?.roles?.includes(role);
}

// Authentication
export async function login(username: string, password: string) {
  const res = await apiFetch(`${API_BASE}/auth/login`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ username, password })
  });
  if (!res.ok) {
    const err = await res.json().catch(() => ({}));
    throw new Error(err.message || 'Invalid credentials');
  }
  const data = await res.json();
  setAuthState(data);
  return data;
}

export function logout() {
  localStorage.removeItem(TOKEN_KEY);
  localStorage.removeItem(AUTH_KEY);
  window.dispatchEvent(new Event(AUTH_EVENT));
}

// Expiry (ms since epoch) from the JWT `exp` claim, or null if the token can't be decoded.
function tokenExpiryMs(token: string): number | null {
  try {
    const payload = token.split('.')[1].replace(/-/g, '+').replace(/_/g, '/');
    const exp = JSON.parse(atob(payload)).exp;
    return typeof exp === 'number' ? exp * 1000 : null;
  } catch {
    return null;
  }
}

// Authenticated = a token is stored AND it has not expired. An expired token is cleared,
// so the caller (App.tsx) redirects to /login like any ordinary app.
export function isAuthenticated() {
  const token = localStorage.getItem(TOKEN_KEY);
  if (!token) return false;
  const expiresAt = tokenExpiryMs(token);
  if (expiresAt !== null && expiresAt <= Date.now()) {
    localStorage.removeItem(TOKEN_KEY);
    localStorage.removeItem(AUTH_KEY);
    return false;
  }
  return true;
}

// Milliseconds until the stored token expires (null when there is no token / no exp claim).
export function msUntilTokenExpiry(): number | null {
  const token = localStorage.getItem(TOKEN_KEY);
  const expiresAt = token ? tokenExpiryMs(token) : null;
  return expiresAt === null ? null : expiresAt - Date.now();
}

// A 401 here means the stored session token is no longer valid (expired or revoked).
// Without this, every page's fetch just silently fails and renders as if all data
// disappeared - clearing the session instead lets App.tsx's AUTH_EVENT listener redirect
// to /login with a clear "please sign in again" instead of a misleading empty state.
export async function apiFetch(input: string, init?: RequestInit): Promise<Response> {
  const res = await fetch(input, init);
  if (res.status === 401 && getAuthState()) {
    logout();
  }
  return res;
}

export async function authMe() {
  const res = await apiFetch(`${API_BASE}/auth/me`, { headers: getHeaders() });
  if (!res.ok) throw new Error('Could not load your account.');
  return res.json();
}

// Change password from the login page - no session needed; the current password is the proof.
export async function changePasswordPublic(username: string, currentPassword: string, newPassword: string) {
  const res = await apiFetch(`${API_BASE}/auth/change-password-public`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ username, currentPassword, newPassword })
  });
  const data = await res.json().catch(() => ({}));
  if (!res.ok) throw new Error(data.message || 'Could not change the password.');
  return data;
}

export async function changePassword(currentPassword: string, newPassword: string) {
  const res = await apiFetch(`${API_BASE}/auth/change-password`, {
    method: 'POST',
    headers: getHeaders(),
    body: JSON.stringify({ currentPassword, newPassword })
  });
  const data = await res.json().catch(() => ({}));
  if (!res.ok) {
    throw new Error(data.message || 'Could not change the password.');
  }
  setAuthState(data);
  return data;
}

// Connections
export async function fetchConnections() {
  const res = await apiFetch(`${API_BASE}/connections`, { headers: getHeaders() });
  if (!res.ok) throw new Error('Could not load the databases.');
  return res.json();
}

export async function createConnection(connection: any) {
  const res = await apiFetch(`${API_BASE}/connections`, {
    method: 'POST',
    headers: getHeaders(),
    body: JSON.stringify(connection)
  });
  if (!res.ok) {
    const err = await res.json().catch(() => ({}));
    throw new Error(err.message || 'Could not add the database.');
  }
  return res.json();
}

export async function deleteConnection(id: number) {
  const res = await apiFetch(`${API_BASE}/connections/${id}`, {
    method: 'DELETE',
    headers: getHeaders()
  });
  if (!res.ok) throw new Error('Could not delete the database.');
}

export async function testConnection(id: number) {
  const res = await apiFetch(`${API_BASE}/connections/${id}/test`, {
    method: 'POST',
    headers: getHeaders()
  });
  if (!res.ok) {
    const err = await res.json().catch(() => ({}));
    throw new Error(err.message || 'Could not connect.');
  }
  return res.json();
}

// Applications
export async function fetchApplications() {
  const res = await apiFetch(`${API_BASE}/applications`, { headers: getHeaders() });
  if (!res.ok) throw new Error('Could not load the migrations.');
  return res.json();
}

export async function fetchApplication(id: number) {
  const res = await apiFetch(`${API_BASE}/applications/${id}`, { headers: getHeaders() });
  if (!res.ok) throw new Error('Could not load this migration.');
  return res.json();
}

export async function createApplication(app: any) {
  const res = await apiFetch(`${API_BASE}/applications`, {
    method: 'POST',
    headers: getHeaders(),
    body: JSON.stringify(app)
  });
  if (!res.ok) throw new Error('Could not create the migration.');
  return res.json();
}

export async function updateApplication(id: number, app: any) {
  const res = await apiFetch(`${API_BASE}/applications/${id}`, {
    method: 'PUT',
    headers: getHeaders(),
    body: JSON.stringify(app)
  });
  if (!res.ok) throw new Error('Could not save the migration.');
  return res.json();
}

// Discovery
export async function fetchDiscoveredTables(connectionId: number, owner: string) {
  const res = await apiFetch(`${API_BASE}/connections/${connectionId}/discovery?owner=${encodeURIComponent(owner)}`, {
    headers: getHeaders()
  });
  if (!res.ok) throw new Error(await readErrorMessage(res, 'Could not load the scanned tables.'));
  return res.json();
}

export type SourceSchema = { name: string; tableCount: number };
export type SourceSchemaList = { schemas: SourceSchema[]; skipped: number };

// The schemas the source account can read tables from, for the schema picker.
export async function fetchSchemas(connectionId: number): Promise<SourceSchemaList> {
  const res = await apiFetch(`${API_BASE}/connections/${connectionId}/discovery/schemas`, {
    headers: getHeaders()
  });
  if (!res.ok) throw new Error(await readErrorMessage(res, 'Could not load the schemas.'));
  return res.json();
}

// tableNames omitted/empty -> full schema scan (replaces the owner's whole cache).
// tableNames provided -> targeted lookup for just those tables (does not disturb the rest of the cache).
export async function refreshDiscovery(connectionId: number, owner: string, tableNames?: string[]) {
  const res = await apiFetch(`${API_BASE}/connections/${connectionId}/discovery/refresh?owner=${encodeURIComponent(owner)}`, {
    method: 'POST',
    headers: getHeaders(),
    body: JSON.stringify({ tableNames: tableNames && tableNames.length > 0 ? tableNames : null })
  });
  if (!res.ok) throw new Error(await readErrorMessage(res, 'Could not scan the source database.'));
  return res.json();
}

// Manifests
export async function fetchManifests(appId: number) {
  const res = await apiFetch(`${API_BASE}/applications/${appId}/manifests`, { headers: getHeaders() });
  if (!res.ok) throw new Error(await readErrorMessage(res, 'Could not load the table selections.'));
  return res.json();
}

export async function fetchManifest(id: number) {
  const res = await apiFetch(`${API_BASE}/manifests/${id}`, { headers: getHeaders() });
  if (!res.ok) throw new Error(await readErrorMessage(res, 'Could not load this table selection.'));
  return res.json();
}

export async function createManifest(appId: number, manifest: any) {
  const res = await apiFetch(`${API_BASE}/applications/${appId}/manifests`, {
    method: 'POST',
    headers: getHeaders(),
    body: JSON.stringify(manifest)
  });
  if (!res.ok) throw new Error(await readErrorMessage(res, 'Could not create the table selection.'));
  return res.json();
}

export async function updateManifestTables(manifestId: number, tables: any[]) {
  const res = await apiFetch(`${API_BASE}/manifests/${manifestId}/tables`, {
    method: 'PUT',
    headers: getHeaders(),
    body: JSON.stringify(tables)
  });
  if (!res.ok) throw new Error(await readErrorMessage(res, 'Could not save the selected tables.'));
}

export async function generateManifest(appId: number, connectionId: number, owner: string) {
  const res = await apiFetch(`${API_BASE}/applications/${appId}/manifests/generate?connectionId=${connectionId}&owner=${encodeURIComponent(owner)}`, {
    method: 'POST',
    headers: getHeaders()
  });
  if (!res.ok) throw new Error(await readErrorMessage(res, 'Could not build the table selection automatically.'));
  return res.json();
}

// Jobs
export async function fetchJobs() {
  const res = await apiFetch(`${API_BASE}/jobs`, { headers: getHeaders() });
  if (!res.ok) throw new Error('Could not load the runs.');
  return res.json();
}

export async function fetchJob(id: number) {
  const res = await apiFetch(`${API_BASE}/jobs/${id}`, { headers: getHeaders() });
  if (!res.ok) throw new Error('Could not load this run.');
  return res.json();
}

export async function createJob(job: any) {
  const res = await apiFetch(`${API_BASE}/jobs`, {
    method: 'POST',
    headers: getHeaders(),
    body: JSON.stringify(job)
  });
  if (!res.ok) throw new Error('Could not create the run.');
  return res.json();
}

export async function launchJob(id: number, confirmationPhrase?: string) {
  const res = await apiFetch(`${API_BASE}/jobs/${id}/launch`, {
    method: 'POST',
    headers: getHeaders(),
    body: JSON.stringify({ confirmationPhrase })
  });
  if (!res.ok) {
    const err = await res.json().catch(() => ({}));
    // `errors` carries the per-check preflight detail. Without it the operator just sees
    // "Preflight check failed" and has no idea which check stopped the launch.
    const message = err.message || 'Could not start the run.';
    throw new Error(err.errors ? `${message}\n${err.errors}` : message);
  }
  return res.json();
}

export async function commandJob(id: number, command: string, payload?: string) {
  const res = await apiFetch(`${API_BASE}/jobs/${id}/commands`, {
    method: 'POST',
    headers: getHeaders(),
    body: JSON.stringify({ command, scope: 'job', payload })
  });
  if (!res.ok) {
    throw new Error(await readErrorMessage(res, `Could not ${commandLabel(command)}.`));
  }
}

// Admin: cancel every non-terminal job at once and signal the Worker(s) to restart.
export async function cancelAllAndRestartWorker() {
  const res = await apiFetch(`${API_BASE}/jobs/cancel-all-and-restart-worker`, {
    method: 'POST',
    headers: getHeaders()
  });
  const data = await res.json().catch(() => ({}));
  if (!res.ok) throw new Error(data.message || 'Could not cancel the runs or restart the copier.');
  return data;
}

export async function fetchMetrics(jobId: number) {
  const res = await apiFetch(`${API_BASE}/jobs/${jobId}/metrics`, { headers: getHeaders() });
  if (!res.ok) throw new Error('Could not load progress figures.');
  return res.json();
}

export async function fetchValidation(jobId: number) {
  const res = await apiFetch(`${API_BASE}/jobs/${jobId}/validation`, { headers: getHeaders() });
  if (!res.ok) throw new Error('Could not load the row count check.');
  return res.json();
}

export async function runPreflight(jobId: number) {
  const res = await apiFetch(`${API_BASE}/jobs/${jobId}/preflight`, {
    method: 'POST',
    headers: getHeaders()
  });
  if (!res.ok) throw new Error('Could not run the readiness check.');
  return res.json();
}

// User management
export async function fetchUsers() {
  const res = await apiFetch(`${API_BASE}/users`, { headers: getHeaders() });
  if (!res.ok) throw new Error('Could not load the users.');
  return res.json();
}

export async function createUser(payload: any) {
  const res = await apiFetch(`${API_BASE}/users`, {
    method: 'POST',
    headers: getHeaders(),
    body: JSON.stringify(payload)
  });
  const data = await res.json().catch(() => ({}));
  if (!res.ok) throw new Error(data.message || 'Could not create the user.');
  return data;
}

export async function updateUser(id: string, payload: any) {
  const res = await apiFetch(`${API_BASE}/users/${id}`, {
    method: 'PUT',
    headers: getHeaders(),
    body: JSON.stringify(payload)
  });
  const data = await res.json().catch(() => ({}));
  if (!res.ok) throw new Error(data.message || 'Could not save the user.');
  return data;
}

export async function resetUserPassword(id: string, newPassword: string, mustChangePassword: boolean) {
  const res = await apiFetch(`${API_BASE}/users/${id}/reset-password`, {
    method: 'POST',
    headers: getHeaders(),
    body: JSON.stringify({ newPassword, mustChangePassword })
  });
  const data = await res.json().catch(() => ({}));
  if (!res.ok) throw new Error(data.message || 'Could not reset the password.');
  return data;
}

export async function unlockUser(id: string) {
  const res = await apiFetch(`${API_BASE}/users/${id}/unlock`, {
    method: 'POST',
    headers: getHeaders()
  });
  const data = await res.json().catch(() => ({}));
  if (!res.ok) throw new Error(data.message || 'Could not unlock the user.');
  return data;
}
