import { useState, useEffect } from 'react';
import { useParams, Link, useNavigate } from 'react-router-dom';
import { ArrowLeft, Database, FileText, Play, RefreshCw, ShieldAlert } from 'lucide-react';
import { fetchApplication, updateApplication, fetchConnections, fetchManifests, generateManifest, createJob, launchJob } from '../api';

export default function ApplicationDetail() {
  const { id } = useParams();
  const appId = Number(id);
  const navigate = useNavigate();

  const [app, setApp] = useState<any>(null);
  const [connections, setConnections] = useState<any[]>([]);
  const [manifests, setManifests] = useState<any[]>([]);
  const [loading, setLoading] = useState(true);

  // Bind Slot State
  const [bindingSlot, setBindingSlot] = useState<string | null>(null);
  const [selectedConnId, setSelectedConnId] = useState<number | null>(null);

  // Auto-Gen Manifest State
  const [showGenModal, setShowGenModal] = useState(false);
  const [genConnId, setGenConnId] = useState<number | null>(null);
  const [genOwner, setGenOwner] = useState('');
  const [genLoading, setGenLoading] = useState(false);

  // Launch Wizard State
  const [runningManifest, setRunningManifest] = useState<any>(null);
  const [sourceSlot, setSourceSlot] = useState('oracle_test');
  const [targetSlot, setTargetSlot] = useState('pg_test');
  const [targetSchema, setTargetSchema] = useState('public');
  const [confirmPhrase, setConfirmPhrase] = useState('');
  const [launchLoading, setLaunchLoading] = useState(false);
  const [launchError, setLaunchError] = useState<string | null>(null);

  useEffect(() => {
    loadAll();
  }, [appId]);

  const loadAll = async () => {
    setLoading(true);
    try {
      const appData = await fetchApplication(appId);
      setApp(appData);

      const connData = await fetchConnections();
      setConnections(connData);

      const manifestData = await fetchManifests(appId);
      setManifests(manifestData);
    } catch (err: any) {
      console.error(err);
    } finally {
      setLoading(false);
    }
  };

  const handleAssignSlot = async () => {
    if (!selectedConnId || !bindingSlot) return;
    try {
      // Re-map application connections
      const existing = app.connections || [];
      const filtered = existing.filter((c: any) => c.slot !== bindingSlot);
      const updatedConnections = [
        ...filtered,
        { slot: bindingSlot, connectionId: selectedConnId }
      ];

      await updateApplication(appId, {
        name: app.name,
        description: app.description,
        defaultsJson: app.defaultsJson,
        connections: updatedConnections
      });
      setBindingSlot(null);
      setSelectedConnId(null);
      loadAll();
    } catch (err: any) {
      alert(err.message || 'Failed to update connection bindings.');
    }
  };

  const handleUnassignSlot = async (slot: string) => {
    if (!confirm(`Are you sure you want to unassign slot ${slot}?`)) return;
    try {
      const existing = app.connections || [];
      const updatedConnections = existing.filter((c: any) => c.slot !== slot);

      await updateApplication(appId, {
        name: app.name,
        description: app.description,
        defaultsJson: app.defaultsJson,
        connections: updatedConnections
      });
      loadAll();
    } catch (err: any) {
      alert(err.message || 'Failed to unassign slot connection.');
    }
  };

  const handleGenerateManifest = async (e: React.FormEvent) => {
    e.preventDefault();
    if (!genConnId || !genOwner) return;
    setGenLoading(true);
    try {
      await generateManifest(appId, genConnId, genOwner);
      setShowGenModal(false);
      setGenOwner('');
      loadAll();
    } catch (err: any) {
      alert(err.message || 'Failed to auto-generate manifest.');
    } finally {
      setGenLoading(false);
    }
  };

  const handleLaunchJob = async (e: React.FormEvent) => {
    e.preventDefault();
    if (targetSlot === 'pg_live' && confirmPhrase !== 'RUN LIVE MIGRATION') {
      setLaunchError('You must type the confirmation phrase exactly to run a live migration.');
      return;
    }

    setLaunchLoading(true);
    setLaunchError(null);

    try {
      // 1. Create Job Run (Draft)
      const job = await createJob({
        applicationId: appId,
        manifestId: runningManifest.id,
        sourceSlot,
        targetSlot,
        targetSchema
      });

      // 2. Launch Job Run (runs preflight and triggers background loading)
      await launchJob(job.id);

      setRunningManifest(null);
      navigate(`/jobs/${job.id}`);
    } catch (err: any) {
      setLaunchError(err.message || 'Failed to launch migration job.');
    } finally {
      setLaunchLoading(false);
    }
  };

  if (loading || !app) {
    return <div className="card" style={{ textAlign: 'center', padding: '48px' }}><RefreshCw size={24} className="spin" /></div>;
  }

  const slotBindings = app.connections || [];

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: '24px' }}>
      {/* Header */}
      <div style={{ display: 'flex', gap: '16px', alignItems: 'center' }}>
        <Link to="/applications" className="btn btn-secondary" style={{ padding: '8px' }}>
          <ArrowLeft size={18} />
        </Link>
        <div>
          <h1 className="text-gradient" style={{ margin: 0 }}>{app.name}</h1>
          <p style={{ margin: '4px 0 0 0', color: 'var(--text-secondary)', fontSize: '0.9rem' }}>
            {app.description || 'No description provided.'}
          </p>
        </div>
      </div>

      <div style={{ display: 'grid', gridTemplateColumns: '2fr 1fr', gap: '24px', alignItems: 'start' }}>
        {/* Manifests & Migration Plans */}
        <div style={{ display: 'flex', flexDirection: 'column', gap: '24px' }}>
          <div className="card">
            <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', marginBottom: '16px' }}>
              <h3 style={{ margin: 0, display: 'flex', alignItems: 'center', gap: '8px' }}>
                <FileText size={18} />
                <span>Migration Manifests</span>
              </h3>
              <div style={{ display: 'flex', gap: '8px' }}>
                <button className="btn btn-secondary" onClick={() => setShowGenModal(true)} style={{ fontSize: '0.85rem' }}>
                  <span>Auto-Gen Manifest</span>
                </button>
                <Link to={`/applications/${appId}/manifests/new/builder`} className="btn" style={{ fontSize: '0.85rem' }}>
                  <span>Custom Builder</span>
                </Link>
              </div>
            </div>

            <div style={{ display: 'flex', flexDirection: 'column', gap: '12px' }}>
              {manifests.map(m => (
                <div key={m.id} className="card" style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', background: 'rgba(255,255,255,0.01)', padding: '16px', border: '1px solid rgba(255,255,255,0.05)' }}>
                  <div>
                    <h4 style={{ margin: 0 }}>Manifest v{m.version}</h4>
                    <span style={{ fontSize: '0.75rem', color: 'var(--text-secondary)' }}>
                      Created on {new Date(m.createdAt).toLocaleString()}
                    </span>
                  </div>

                  <div style={{ display: 'flex', gap: '8px' }}>
                    <Link to={`/applications/${appId}/manifests/${m.id}/builder`} className="btn btn-secondary" style={{ padding: '6px 12px', fontSize: '0.85rem' }}>
                      <span>Edit</span>
                    </Link>
                    <button className="btn" onClick={() => setRunningManifest(m)} style={{ padding: '6px 12px', fontSize: '0.85rem', background: '#10b981', color: 'white' }}>
                      <Play size={14} />
                      <span>Run Job</span>
                    </button>
                  </div>
                </div>
              ))}

              {manifests.length === 0 && (
                <p style={{ textAlign: 'center', color: 'var(--text-secondary)', padding: '24px 0' }}>
                  No manifests created yet. Run "Auto-Gen Manifest" to pull table lists from Oracle.
                </p>
              )}
            </div>
          </div>
        </div>

        {/* Connection Slots */}
        <div className="card">
          <h3 style={{ margin: '0 0 16px 0', display: 'flex', alignItems: 'center', gap: '8px' }}>
            <Database size={18} />
            <span>Connection Slots</span>
          </h3>

          <div style={{ display: 'flex', flexDirection: 'column', gap: '12px' }}>
            {['oracle_test', 'oracle_live', 'pg_test', 'pg_live'].map(slot => {
              const binding = slotBindings.find((c: any) => c.slot === slot);
              const connection = binding ? connections.find(c => c.id === binding.connectionId) : null;
              const isOracle = slot.startsWith('oracle');

              return (
                <div key={slot} className="card" style={{ background: 'rgba(255,255,255,0.02)', border: '1px solid rgba(255,255,255,0.05)', padding: '12px 16px' }}>
                  <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'flex-start', marginBottom: '8px' }}>
                    <span style={{ fontSize: '0.75rem', fontWeight: 'bold', textTransform: 'uppercase', color: isOracle ? '#f87171' : '#60a5fa' }}>
                      {slot.replace('_', ' ')}
                    </span>
                    {connection ? (
                      <button
                        style={{ background: 'none', border: 'none', color: '#ef4444', fontSize: '0.75rem', cursor: 'pointer' }}
                        onClick={() => handleUnassignSlot(slot)}
                      >
                        Unassign
                      </button>
                    ) : (
                      <button
                        style={{ background: 'none', border: 'none', color: '#3b82f6', fontSize: '0.75rem', cursor: 'pointer', fontWeight: 'bold' }}
                        onClick={() => { setBindingSlot(slot); setSelectedConnId(connections.filter(c => c.kind === (isOracle ? 0 : 1))[0]?.id || null); }}
                      >
                        Assign
                      </button>
                    )}
                  </div>

                  {connection ? (
                    <div>
                      <div style={{ fontWeight: 'bold', fontSize: '0.9rem' }}>{connection.name}</div>
                      <div style={{ fontSize: '0.75rem', color: 'var(--text-secondary)', fontFamily: 'monospace', marginTop: '2px' }}>
                        {connection.host}:{connection.port}/{connection.serviceOrDb}
                      </div>
                    </div>
                  ) : (
                    <div style={{ fontSize: '0.85rem', color: 'var(--text-secondary)', fontStyle: 'italic' }}>
                      Not Bound
                    </div>
                  )}
                </div>
              );
            })}
          </div>
        </div>
      </div>

      {/* Assign Slot Modal/Overlay */}
      {bindingSlot && (
        <div style={{ position: 'fixed', inset: 0, background: 'rgba(0,0,0,0.6)', display: 'flex', alignItems: 'center', justifyContent: 'center', zIndex: 100 }}>
          <div className="card" style={{ maxWidth: '400px', width: '100%', display: 'flex', flexDirection: 'column', gap: '16px' }}>
            <h3 style={{ margin: 0 }}>Assign Connection Slot</h3>
            <p style={{ fontSize: '0.85rem', color: 'var(--text-secondary)', margin: 0 }}>
              Select a connection profile to bind to <strong>{bindingSlot.replace('_', ' ')}</strong>:
            </p>

            <select
              className="input"
              value={selectedConnId || ''}
              onChange={e => setSelectedConnId(Number(e.target.value))}
            >
              <option value="">-- Select Connection Profile --</option>
              {connections
                .filter(c => c.kind === (bindingSlot.startsWith('oracle') ? 0 : 1))
                .map(c => (
                  <option key={c.id} value={c.id}>
                    {c.name} ({c.host})
                  </option>
                ))}
            </select>

            <div style={{ display: 'flex', gap: '12px', justifyContent: 'flex-end', marginTop: '8px' }}>
              <button className="btn btn-secondary" onClick={() => setBindingSlot(null)}>Cancel</button>
              <button className="btn" onClick={handleAssignSlot} disabled={!selectedConnId}>Assign</button>
            </div>
          </div>
        </div>
      )}

      {/* Auto-Gen Manifest Modal */}
      {showGenModal && (
        <div style={{ position: 'fixed', inset: 0, background: 'rgba(0,0,0,0.6)', display: 'flex', alignItems: 'center', justifyContent: 'center', zIndex: 100 }}>
          <div className="card" style={{ maxWidth: '450px', width: '100%' }}>
            <h3 style={{ margin: '0 0 12px 0' }}>Auto-Generate Manifest</h3>
            <p style={{ fontSize: '0.85rem', color: 'var(--text-secondary)', margin: '0 0 20px 0' }}>
              Queries the Oracle system tables to auto-discover all tables and columns, caching them in metadata and building manifest v1.
            </p>

            <form onSubmit={handleGenerateManifest} style={{ display: 'flex', flexDirection: 'column', gap: '16px' }}>
              <div>
                <label className="label">Oracle Discovery Connection</label>
                <select
                  className="input"
                  value={genConnId || ''}
                  onChange={e => setGenConnId(Number(e.target.value))}
                  required
                >
                  <option value="">-- Select Oracle Profile --</option>
                  {connections
                    .filter(c => c.kind === 0)
                    .map(c => (
                      <option key={c.id} value={c.id}>{c.name}</option>
                    ))}
                </select>
              </div>

              <div>
                <label className="label">Oracle Schema / Owner</label>
                <input
                  className="input"
                  type="text"
                  placeholder="e.g. HR or SYSTEM"
                  value={genOwner}
                  onChange={e => setGenOwner(e.target.value.toUpperCase())}
                  required
                />
              </div>

              <div style={{ display: 'flex', gap: '12px', justifyContent: 'flex-end', marginTop: '12px' }}>
                <button type="button" className="btn btn-secondary" onClick={() => setShowGenModal(false)}>Cancel</button>
                <button type="submit" className="btn" disabled={genLoading || !genConnId || !genOwner}>
                  {genLoading ? <RefreshCw size={16} className="spin" /> : <Play size={16} />}
                  <span>Generate</span>
                </button>
              </div>
            </form>
          </div>
        </div>
      )}

      {/* Launch Job Wizard Modal */}
      {runningManifest && (
        <div style={{ position: 'fixed', inset: 0, background: 'rgba(0,0,0,0.6)', display: 'flex', alignItems: 'center', justifyContent: 'center', zIndex: 100 }}>
          <div className="card" style={{ maxWidth: '500px', width: '100%' }}>
            <h3 style={{ margin: '0 0 16px 0', display: 'flex', alignItems: 'center', gap: '8px' }}>
              <Play size={20} style={{ color: '#10b981' }} />
              <span>Launch Migration Job</span>
            </h3>

            {launchError && (
              <div className="card" style={{ background: 'rgba(239, 68, 68, 0.08)', color: '#f87171', border: '1px solid rgba(239, 68, 68, 0.2)', marginBottom: '16px', fontSize: '0.85rem' }}>
                {launchError}
              </div>
            )}

            <form onSubmit={handleLaunchJob} style={{ display: 'flex', flexDirection: 'column', gap: '16px' }}>
              <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: '12px' }}>
                <div>
                  <label className="label">Source Connection Slot</label>
                  <select className="input" value={sourceSlot} onChange={e => setSourceSlot(e.target.value)}>
                    <option value="oracle_test">Oracle Test</option>
                    <option value="oracle_live">Oracle Live</option>
                  </select>
                </div>
                <div>
                  <label className="label">Target Connection Slot</label>
                  <select className="input" value={targetSlot} onChange={e => setTargetSlot(e.target.value)}>
                    <option value="pg_test">PostgreSQL Test</option>
                    <option value="pg_live">PostgreSQL Live</option>
                  </select>
                </div>
              </div>

              <div>
                <label className="label">Target Postgres Schema Name</label>
                <input className="input" type="text" value={targetSchema} onChange={e => setTargetSchema(e.target.value)} required />
              </div>

              {targetSlot === 'pg_live' && (
                <div className="card" style={{ background: 'rgba(245, 158, 11, 0.08)', border: '1px solid rgba(245, 158, 11, 0.2)', display: 'flex', flexDirection: 'column', gap: '12px' }}>
                  <div style={{ display: 'flex', gap: '8px', color: '#fbbf24', fontSize: '0.85rem', fontWeight: 'bold' }}>
                    <ShieldAlert size={18} />
                    <span>CAUTION: LIVE TARGET MIGRATION</span>
                  </div>
                  <p style={{ fontSize: '0.8rem', color: 'var(--text-secondary)', margin: 0 }}>
                    You have selected a Live PostgreSQL target. This run could overwrite data. To launch, type <strong>RUN LIVE MIGRATION</strong> below:
                  </p>
                  <input
                    className="input"
                    type="text"
                    placeholder="Type confirmation here"
                    value={confirmPhrase}
                    onChange={e => setConfirmPhrase(e.target.value)}
                    required
                  />
                </div>
              )}

              <div style={{ display: 'flex', gap: '12px', justifyContent: 'flex-end', marginTop: '16px' }}>
                <button type="button" className="btn btn-secondary" onClick={() => { setRunningManifest(null); setConfirmPhrase(''); setLaunchError(null); }}>Cancel</button>
                <button type="submit" className="btn" disabled={launchLoading} style={{ background: '#10b981', color: 'white' }}>
                  {launchLoading ? <RefreshCw size={16} className="spin" /> : <Play size={16} />}
                  <span>Launch Migration</span>
                </button>
              </div>
            </form>
          </div>
        </div>
      )}
    </div>
  );
}
