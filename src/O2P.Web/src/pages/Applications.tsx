import { useState, useEffect } from 'react';
import { Link } from 'react-router-dom';
import { FolderTree, Plus, RefreshCw } from 'lucide-react';
import { fetchApplications, createApplication } from '../api';

export default function Applications() {
  const [apps, setApps] = useState<any[]>([]);
  const [showForm, setShowForm] = useState(false);
  const [name, setName] = useState('');
  const [description, setDescription] = useState('');
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    loadApps();
  }, []);

  const loadApps = async () => {
    try {
      const data = await fetchApplications();
      setApps(data);
    } catch (err: any) {
      console.error(err);
    }
  };

  const handleSubmit = async (e: React.FormEvent) => {
    e.preventDefault();
    setLoading(true);
    setError(null);
    try {
      await createApplication({
        name,
        description,
        defaultsJson: JSON.stringify({
          max_concurrent_tables: 4,
          chunk_target_mb: 256,
          global_max_oracle_sessions: 8
        })
      });
      setShowForm(false);
      setName('');
      setDescription('');
      loadApps();
    } catch (err: any) {
      setError(err.message || 'Failed to create application.');
    } finally {
      setLoading(false);
    }
  };

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: '24px' }}>
      <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center' }}>
        <div>
          <h1 className="text-gradient" style={{ margin: 0 }}>Applications</h1>
          <p style={{ margin: '4px 0 0 0', color: 'var(--text-secondary)', fontSize: '0.9rem' }}>
            A project-style grouping containing connection slot bindings and migration manifests.
          </p>
        </div>
        <button className="btn" onClick={() => setShowForm(true)}>
          <Plus size={18} />
          <span>New Application</span>
        </button>
      </div>

      <div style={{ display: 'grid', gridTemplateColumns: showForm ? '2fr 1fr' : '1fr', gap: '24px', alignItems: 'start' }}>
        {/* Applications List */}
        <div className="card">
          <h3 style={{ margin: '0 0 16px 0' }}>All Applications</h3>
          <div style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fill, minmax(280px, 1fr))', gap: '16px' }}>
            {apps.map(app => (
              <div key={app.id} className="card hover-card" style={{ display: 'flex', flexDirection: 'column', gap: '16px', background: 'rgba(255, 255, 255, 0.01)', border: '1px solid rgba(255,255,255,0.04)' }}>
                <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'flex-start' }}>
                  <div style={{ display: 'flex', gap: '12px', alignItems: 'center' }}>
                    <div style={{ padding: '8px', borderRadius: '8px', background: 'rgba(16, 185, 129, 0.1)', color: '#34d399', display: 'flex' }}>
                      <FolderTree size={20} />
                    </div>
                    <h3 style={{ margin: 0, fontSize: '1.1rem' }}>{app.name}</h3>
                  </div>
                </div>

                <p style={{ color: 'var(--text-secondary)', fontSize: '0.85rem', margin: 0, minHeight: '38px', lineClamp: 2, WebkitLineClamp: 2, display: '-webkit-box', WebkitBoxOrient: 'vertical', overflow: 'hidden' }}>
                  {app.description || 'No description provided.'}
                </p>

                <div style={{ borderTop: '1px solid rgba(255,255,255,0.05)', paddingTop: '12px', display: 'flex', justifyContent: 'space-between', alignItems: 'center' }}>
                  <span style={{ fontSize: '0.75rem', color: 'var(--text-secondary)' }}>
                    Slots: {app.connections?.length || 0} bound
                  </span>
                  <Link to={`/applications/${app.id}`} className="btn btn-secondary" style={{ padding: '6px 12px', fontSize: '0.85rem' }}>
                    <span>Manage</span>
                  </Link>
                </div>
              </div>
            ))}

            {apps.length === 0 && (
              <div style={{ gridColumn: '1 / -1', textAlign: 'center', padding: '48px 0', color: 'var(--text-secondary)' }}>
                No applications configured. Click "New Application" to get started.
              </div>
            )}
          </div>
        </div>

        {/* Create Application Form */}
        {showForm && (
          <div className="card">
            <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', marginBottom: '20px' }}>
              <h3 style={{ margin: 0 }}>Create Application</h3>
              <button className="btn btn-secondary" style={{ padding: '4px 8px' }} onClick={() => setShowForm(false)}>
                Cancel
              </button>
            </div>

            <form onSubmit={handleSubmit} style={{ display: 'flex', flexDirection: 'column', gap: '16px' }}>
              {error && <div className="card" style={{ background: 'rgba(239, 68, 68, 0.1)', color: '#f87171', border: '1px solid rgba(239, 68, 68, 0.2)' }}>{error}</div>}

              <div>
                <label className="label">Application Name</label>
                <input className="input" type="text" placeholder="e.g. Core Banking System" value={name} onChange={e => setName(e.target.value)} required />
              </div>

              <div>
                <label className="label">Description</label>
                <textarea
                  className="input"
                  style={{ minHeight: '100px', resize: 'vertical' }}
                  placeholder="e.g. Production Oracle EBS instance to Postgres cloud staging migration."
                  value={description}
                  onChange={e => setDescription(e.target.value)}
                />
              </div>

              <button className="btn" type="submit" disabled={loading} style={{ marginTop: '8px' }}>
                {loading ? <RefreshCw size={18} className="spin" /> : <Plus size={18} />}
                <span>Create Application</span>
              </button>
            </form>
          </div>
        )}
      </div>
    </div>
  );
}
