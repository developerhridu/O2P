const API_BASE = 'http://localhost:5000/api/v1';

function getHeaders() {
  const token = localStorage.getItem('o2p_token');
  return {
    'Content-Type': 'application/json',
    ...(token ? { Authorization: `Bearer ${token}` } : {})
  };
}

// Authentication
export async function login(username: string, password: string) {
  const res = await fetch(`${API_BASE}/auth/login`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ username, password })
  });
  if (!res.ok) throw new Error('Invalid credentials');
  const data = await res.json();
  localStorage.setItem('o2p_token', data.token);
  return data;
}

export function logout() {
  localStorage.removeItem('o2p_token');
}

export function isAuthenticated() {
  return !!localStorage.getItem('o2p_token');
}

// Connections
export async function fetchConnections() {
  const res = await fetch(`${API_BASE}/connections`, { headers: getHeaders() });
  if (!res.ok) throw new Error('Failed to fetch connections');
  return res.json();
}

export async function createConnection(connection: any) {
  const res = await fetch(`${API_BASE}/connections`, {
    method: 'POST',
    headers: getHeaders(),
    body: JSON.stringify(connection)
  });
  if (!res.ok) {
    const err = await res.json().catch(() => ({}));
    throw new Error(err.message || 'Failed to create connection');
  }
  return res.json();
}

export async function deleteConnection(id: number) {
  const res = await fetch(`${API_BASE}/connections/${id}`, {
    method: 'DELETE',
    headers: getHeaders()
  });
  if (!res.ok) throw new Error('Failed to delete connection');
}

export async function testConnection(id: number) {
  const res = await fetch(`${API_BASE}/connections/${id}/test`, {
    method: 'POST',
    headers: getHeaders()
  });
  if (!res.ok) {
    const err = await res.json().catch(() => ({}));
    throw new Error(err.message || 'Connection test failed');
  }
  return res.json();
}

// Applications
export async function fetchApplications() {
  const res = await fetch(`${API_BASE}/applications`, { headers: getHeaders() });
  if (!res.ok) throw new Error('Failed to fetch applications');
  return res.json();
}

export async function fetchApplication(id: number) {
  const res = await fetch(`${API_BASE}/applications/${id}`, { headers: getHeaders() });
  if (!res.ok) throw new Error('Failed to fetch application');
  return res.json();
}

export async function createApplication(app: any) {
  const res = await fetch(`${API_BASE}/applications`, {
    method: 'POST',
    headers: getHeaders(),
    body: JSON.stringify(app)
  });
  if (!res.ok) throw new Error('Failed to create application');
  return res.json();
}

export async function updateApplication(id: number, app: any) {
  const res = await fetch(`${API_BASE}/applications/${id}`, {
    method: 'PUT',
    headers: getHeaders(),
    body: JSON.stringify(app)
  });
  if (!res.ok) throw new Error('Failed to update application');
  return res.json();
}

// Discovery
export async function fetchDiscoveredTables(connectionId: number, owner: string) {
  const res = await fetch(`${API_BASE}/connections/${connectionId}/discovery?owner=${owner}`, {
    headers: getHeaders()
  });
  if (!res.ok) throw new Error('Failed to fetch discovery cache');
  return res.json();
}

export async function refreshDiscovery(connectionId: number, owner: string) {
  const res = await fetch(`${API_BASE}/connections/${connectionId}/discovery/refresh?owner=${owner}`, {
    method: 'POST',
    headers: getHeaders()
  });
  if (!res.ok) throw new Error('Failed to refresh discovery');
  return res.json();
}

// Manifests
export async function fetchManifests(appId: number) {
  const res = await fetch(`${API_BASE}/applications/${appId}/manifests`, { headers: getHeaders() });
  if (!res.ok) throw new Error('Failed to fetch manifests');
  return res.json();
}

export async function fetchManifest(id: number) {
  const res = await fetch(`${API_BASE}/manifests/${id}`, { headers: getHeaders() });
  if (!res.ok) throw new Error('Failed to fetch manifest');
  return res.json();
}

export async function createManifest(appId: number, manifest: any) {
  const res = await fetch(`${API_BASE}/applications/${appId}/manifests`, {
    method: 'POST',
    headers: getHeaders(),
    body: JSON.stringify(manifest)
  });
  if (!res.ok) throw new Error('Failed to create manifest');
  return res.json();
}

export async function updateManifestTables(manifestId: number, tables: any[]) {
  const res = await fetch(`${API_BASE}/manifests/${manifestId}/tables`, {
    method: 'PUT',
    headers: getHeaders(),
    body: JSON.stringify(tables)
  });
  if (!res.ok) throw new Error('Failed to update manifest tables');
}

export async function generateManifest(appId: number, connectionId: number, owner: string) {
  const res = await fetch(`${API_BASE}/applications/${appId}/manifests/generate?connectionId=${connectionId}&owner=${owner}`, {
    method: 'POST',
    headers: getHeaders()
  });
  if (!res.ok) throw new Error('Failed to auto-generate manifest');
  return res.json();
}

// Jobs
export async function fetchJobs() {
  const res = await fetch(`${API_BASE}/jobs`, { headers: getHeaders() });
  if (!res.ok) throw new Error('Failed to fetch jobs');
  return res.json();
}

export async function fetchJob(id: number) {
  const res = await fetch(`${API_BASE}/jobs/${id}`, { headers: getHeaders() });
  if (!res.ok) throw new Error('Failed to fetch job run');
  return res.json();
}

export async function createJob(job: any) {
  const res = await fetch(`${API_BASE}/jobs`, {
    method: 'POST',
    headers: getHeaders(),
    body: JSON.stringify(job)
  });
  if (!res.ok) throw new Error('Failed to create job run');
  return res.json();
}

export async function launchJob(id: number) {
  const res = await fetch(`${API_BASE}/jobs/${id}/launch`, {
    method: 'POST',
    headers: getHeaders()
  });
  if (!res.ok) {
    const err = await res.json().catch(() => ({}));
    throw new Error(err.message || 'Job launch failed');
  }
  return res.json();
}

export async function fetchMetrics(jobId: number) {
  const res = await fetch(`${API_BASE}/jobs/${jobId}/metrics`, { headers: getHeaders() });
  if (!res.ok) throw new Error('Failed to fetch metrics');
  return res.json();
}

export async function fetchValidation(jobId: number) {
  const res = await fetch(`${API_BASE}/jobs/${jobId}/validation`, { headers: getHeaders() });
  if (!res.ok) throw new Error('Failed to fetch validation');
  return res.json();
}

export async function runPreflight(jobId: number) {
  const res = await fetch(`${API_BASE}/jobs/${jobId}/preflight`, {
    method: 'POST',
    headers: getHeaders()
  });
  if (!res.ok) throw new Error('Failed to run preflight');
  return res.json();
}
