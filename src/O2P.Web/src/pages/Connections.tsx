import { useState, useEffect } from 'react';
import { Database, Plus, RefreshCw, Trash2, CheckCircle2, AlertTriangle, Play } from 'lucide-react';
import { fetchConnections, createConnection, deleteConnection, testConnection } from '../api';

export default function Connections() {
  const [connections, setConnections] = useState<any[]>([]);
  const [showForm, setShowForm] = useState(false);
  const [loading, setLoading] = useState(false);
  const [testingId, setTestingId] = useState<number | null>(null);
  const [testResult, setTestResult] = useState<any>(null);
  const [testError, setTestError] = useState<string | null>(null);

  // Form State
  const [name, setName] = useState('');
  const [kind, setKind] = useState('oracle');
  const [host, setHost] = useState('');
  const [port, setPort] = useState(1521);
  const [serviceOrDb, setServiceOrDb] = useState('');
  const [username, setUsername] = useState('');
  const [password, setPassword] = useState('');
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    loadConnections();
  }, []);

  const loadConnections = async () => {
    try {
      const data = await fetchConnections();
      setConnections(data);
    } catch (err: any) {
      console.error(err);
    }
  };

  const handleKindChange = (newKind: string) => {
    setKind(newKind);
    setPort(newKind === 'oracle' ? 1521 : 5432);
  };

  const handleSubmit = async (e: React.FormEvent) => {
    e.preventDefault();
    setLoading(true);
    setError(null);
    try {
      await createConnection({
        name,
        kind,
        host,
        port: Number(port),
        serviceOrDb,
        username,
        password
      });
      setShowForm(false);
      setName('');
      setHost('');
      setServiceOrDb('');
      setUsername('');
      setPassword('');
      loadConnections();
    } catch (err: any) {
      setError(err.message || 'Failed to create connection profile.');
    } finally {
      setLoading(false);
    }
  };

  const handleDelete = async (id: number) => {
    if (!confirm('Are you sure you want to delete this connection profile?')) return;
    try {
      await deleteConnection(id);
      loadConnections();
    } catch (err: any) {
      alert(err.message || 'Failed to delete connection profile.');
    }
  };

  const handleTest = async (id: number) => {
    setTestingId(id);
    setTestResult(null);
    setTestError(null);
    try {
      const result = await testConnection(id);
      setTestResult(result);
    } catch (err: any) {
      setTestError(err.message || 'Connection test failed.');
    } finally {
      setTestingId(null);
    }
  };

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: '24px' }}>
      <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center' }}>
        <div>
          <h1 className="text-gradient" style={{ margin: 0 }}>Connection Profiles</h1>
          <p style={{ margin: '4px 0 0 0', color: 'var(--text-secondary)', fontSize: '0.9rem' }}>
            Manage source Oracle and target PostgreSQL connection settings.
          </p>
        </div>
        <button className="btn" onClick={() => setShowForm(true)}>
          <Plus size={18} />
          <span>New Connection</span>
        </button>
      </div>

      <div style={{ display: 'grid', gridTemplateColumns: showForm ? '1fr 1fr' : '1fr', gap: '24px', alignItems: 'start' }}>
        {/* Connections List */}
        <div className="card">
          <h3 style={{ margin: '0 0 16px 0' }}>Active Profiles</h3>
          <div style={{ display: 'flex', flexDirection: 'column', gap: '12px' }}>
            {connections.map(conn => (
              <div
                key={conn.id}
                className="card"
                style={{
                  background: 'rgba(255, 255, 255, 0.02)',
                  border: '1px solid rgba(255, 255, 255, 0.05)',
                  display: 'flex',
                  justifyContent: 'space-between',
                  alignItems: 'center',
                  padding: '16px'
                }}
              >
                <div style={{ display: 'flex', gap: '16px', alignItems: 'center' }}>
                  <div
                    style={{
                      padding: '10px',
                      borderRadius: '8px',
                      background: conn.kind === 0 ? 'rgba(239, 68, 68, 0.1)' : 'rgba(59, 130, 246, 0.1)',
                      color: conn.kind === 0 ? '#f87171' : '#60a5fa',
                      display: 'flex',
                      alignItems: 'center'
                    }}
                  >
                    <Database size={20} />
                  </div>
                  <div>
                    <h4 style={{ margin: 0 }}>{conn.name}</h4>
                    <span style={{ fontSize: '0.8rem', color: 'var(--text-secondary)', fontFamily: 'monospace' }}>
                      {conn.kind === 0 ? 'Oracle' : 'PostgreSQL'} • {conn.host}:{conn.port}/{conn.serviceOrDb}
                    </span>
                  </div>
                </div>

                <div style={{ display: 'flex', gap: '8px' }}>
                  <button
                    className="btn btn-secondary"
                    style={{ padding: '6px 12px', fontSize: '0.85rem' }}
                    onClick={() => handleTest(conn.id)}
                    disabled={testingId === conn.id}
                  >
                    {testingId === conn.id ? <RefreshCw size={14} className="spin" /> : <Play size={14} />}
                    <span>Test</span>
                  </button>
                  <button
                    className="btn btn-secondary"
                    style={{ padding: '6px 12px', color: '#ef4444' }}
                    onClick={() => handleDelete(conn.id)}
                  >
                    <Trash2 size={14} />
                  </button>
                </div>
              </div>
            ))}

            {connections.length === 0 && (
              <p style={{ textAlign: 'center', color: 'var(--text-secondary)', padding: '24px 0' }}>
                No connection profiles added yet.
              </p>
            )}
          </div>
        </div>

        {/* Create Connection Form */}
        {showForm && (
          <div className="card">
            <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', marginBottom: '20px' }}>
              <h3 style={{ margin: 0 }}>Create Profile</h3>
              <button className="btn btn-secondary" style={{ padding: '4px 8px' }} onClick={() => setShowForm(false)}>
                Cancel
              </button>
            </div>

            <form onSubmit={handleSubmit} style={{ display: 'flex', flexDirection: 'column', gap: '16px' }}>
              {error && <div className="card" style={{ background: 'rgba(239, 68, 68, 0.1)', color: '#f87171', border: '1px solid rgba(239, 68, 68, 0.2)' }}>{error}</div>}

              <div>
                <label className="label">Profile Name</label>
                <input className="input" type="text" placeholder="e.g. Oracle Production" value={name} onChange={e => setName(e.target.value)} required />
              </div>

              <div>
                <label className="label">Database Engine</label>
                <div style={{ display: 'flex', gap: '12px' }}>
                  <button
                    type="button"
                    className={`btn ${kind === 'oracle' ? '' : 'btn-secondary'}`}
                    style={{ flex: 1 }}
                    onClick={() => handleKindChange('oracle')}
                  >
                    Oracle
                  </button>
                  <button
                    type="button"
                    className={`btn ${kind === 'postgres' ? '' : 'btn-secondary'}`}
                    style={{ flex: 1 }}
                    onClick={() => handleKindChange('postgres')}
                  >
                    PostgreSQL
                  </button>
                </div>
              </div>

              <div style={{ display: 'grid', gridTemplateColumns: '3fr 1fr', gap: '12px' }}>
                <div>
                  <label className="label">Host Address</label>
                  <input className="input" type="text" placeholder="e.g. 192.168.1.100" value={host} onChange={e => setHost(e.target.value)} required />
                </div>
                <div>
                  <label className="label">Port</label>
                  <input className="input" type="number" value={port} onChange={e => setPort(Number(e.target.value))} required />
                </div>
              </div>

              <div>
                <label className="label">{kind === 'oracle' ? 'Service Name / SID' : 'Database Name'}</label>
                <input className="input" type="text" placeholder={kind === 'oracle' ? 'ORCL' : 'postgres'} value={serviceOrDb} onChange={e => setServiceOrDb(e.target.value)} required />
              </div>

              <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: '12px' }}>
                <div>
                  <label className="label">Username</label>
                  <input className="input" type="text" placeholder="system" value={username} onChange={e => setUsername(e.target.value)} required />
                </div>
                <div>
                  <label className="label">Password</label>
                  <input className="input" type="password" placeholder="••••••••" value={password} onChange={e => setPassword(e.target.value)} required />
                </div>
              </div>

              <button className="btn" type="submit" disabled={loading} style={{ marginTop: '8px' }}>
                {loading ? <RefreshCw size={18} className="spin" /> : <Plus size={18} />}
                <span>Add Connection Profile</span>
              </button>
            </form>
          </div>
        )}
      </div>

      {/* Test Connection Results Popup/Section */}
      {(testResult || testError) && (
        <div className="card" style={{ border: testError ? '1px solid rgba(239, 68, 68, 0.2)' : '1px solid rgba(16, 185, 129, 0.2)', background: testError ? 'rgba(239, 68, 68, 0.02)' : 'rgba(16, 185, 129, 0.02)' }}>
          <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', marginBottom: '16px' }}>
            <h3 style={{ margin: 0, display: 'flex', alignItems: 'center', gap: '8px' }}>
              {testError ? <AlertTriangle style={{ color: '#f87171' }} /> : <CheckCircle2 style={{ color: '#34d399' }} />}
              <span>Connection Test Result</span>
            </h3>
            <button className="btn btn-secondary" style={{ padding: '4px 8px' }} onClick={() => { setTestResult(null); setTestError(null); }}>
              Dismiss
            </button>
          </div>

          {testError ? (
            <div style={{ color: '#f87171', fontSize: '0.95rem' }}>
              <strong>Error:</strong> {testError}
            </div>
          ) : (
            <div style={{ display: 'flex', flexDirection: 'column', gap: '12px' }}>
              <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: '16px', fontSize: '0.9rem' }}>
                <div><strong>Server Version:</strong> {testResult.serverVersion}</div>
                <div><strong>Latency:</strong> {testResult.latencyMs} ms</div>
              </div>
              <div style={{ marginTop: '8px' }}>
                <strong style={{ display: 'block', marginBottom: '8px', fontSize: '0.9rem' }}>Privilege Checklist:</strong>
                <div style={{ display: 'flex', flexDirection: 'column', gap: '6px' }}>
                  {testResult.privileges?.map((p: any, idx: number) => (
                    <div key={idx} style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', background: 'rgba(255,255,255,0.02)', padding: '8px 12px', borderRadius: '6px', fontSize: '0.85rem' }}>
                      <span>{p.name}</span>
                      <span style={{ color: p.ok ? '#34d399' : '#f87171', fontWeight: 'bold' }}>
                        {p.ok ? '✓ OK' : '✗ FAILED'} ({p.detail})
                      </span>
                    </div>
                  ))}
                </div>
              </div>
            </div>
          )}
        </div>
      )}
    </div>
  );
}
