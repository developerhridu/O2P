import { useEffect, useMemo, useState } from 'react';
import { Link, useNavigate, useParams } from 'react-router-dom';
import {
  ArrowLeft,
  Database,
  FileText,
  Play,
  RefreshCw,
  ShieldAlert,
  Unlink,
  Wand2,
} from 'lucide-react';
import {
  createJob,
  fetchApplication,
  fetchConnections,
  fetchManifests,
  generateManifest,
  launchJob,
  refreshDiscovery,
  updateApplication,
} from '../api';

const slots = [
  { key: 'oracle_test', label: 'Oracle Test', kind: 0, tone: 'text-rose-300 border-rose-500/20 bg-rose-500/10' },
  { key: 'oracle_live', label: 'Oracle Live', kind: 0, tone: 'text-rose-300 border-rose-500/20 bg-rose-500/10' },
  { key: 'pg_test', label: 'Postgres Test', kind: 1, tone: 'text-sky-300 border-sky-500/20 bg-sky-500/10' },
  { key: 'pg_live', label: 'Postgres Live', kind: 1, tone: 'text-sky-300 border-sky-500/20 bg-sky-500/10' },
];

export default function ApplicationDetail() {
  const { id } = useParams();
  const appId = Number(id);
  const navigate = useNavigate();

  const [app, setApp] = useState<any>(null);
  const [connections, setConnections] = useState<any[]>([]);
  const [manifests, setManifests] = useState<any[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  const [bindingSlot, setBindingSlot] = useState<string | null>(null);
  const [selectedConnId, setSelectedConnId] = useState<number | ''>('');
  const [savingBinding, setSavingBinding] = useState(false);

  const [showGenModal, setShowGenModal] = useState(false);
  const [genConnId, setGenConnId] = useState<number | ''>('');
  const [genOwner, setGenOwner] = useState('');
  const [genLoading, setGenLoading] = useState(false);

  const [runningManifest, setRunningManifest] = useState<any>(null);
  const [sourceSlot, setSourceSlot] = useState('oracle_test');
  const [targetSlot, setTargetSlot] = useState('pg_test');
  const [targetSchema, setTargetSchema] = useState('public');
  const [confirmPhrase, setConfirmPhrase] = useState('');
  const [launchLoading, setLaunchLoading] = useState(false);
  const [launchError, setLaunchError] = useState<string | null>(null);

  const slotBindings = app?.connections || [];
  const oracleConnections = useMemo(() => connections.filter((c) => c.kind === 0), [connections]);
  const pgConnections = useMemo(() => connections.filter((c) => c.kind === 1), [connections]);

  const loadAll = async () => {
    setLoading(true);
    setError(null);
    try {
      const [appData, connData, manifestData] = await Promise.all([
        fetchApplication(appId),
        fetchConnections(),
        fetchManifests(appId),
      ]);
      setApp(appData);
      setConnections(connData);
      setManifests(manifestData);
    } catch (err: any) {
      setError(err.message || 'Failed to load application.');
    } finally {
      setLoading(false);
    }
  };

  useEffect(() => {
    if (Number.isFinite(appId)) {
      loadAll();
    }
  }, [appId]);

  const openAssign = (slot: string) => {
    const slotMeta = slots.find((s) => s.key === slot);
    const firstMatch = connections.find((c) => c.kind === slotMeta?.kind);
    setBindingSlot(slot);
    setSelectedConnId(firstMatch?.id || '');
  };

  const saveApplicationConnections = async (connectionsPayload: any[]) => {
    await updateApplication(appId, {
      name: app.name,
      description: app.description,
      defaultsJson: app.defaultsJson,
      connections: connectionsPayload,
    });
    await loadAll();
  };

  const handleAssignSlot = async () => {
    if (!bindingSlot || !selectedConnId) return;
    setSavingBinding(true);
    try {
      const filtered = slotBindings.filter((binding: any) => binding.slot !== bindingSlot);
      await saveApplicationConnections([
        ...filtered,
        { slot: bindingSlot, connectionId: Number(selectedConnId) },
      ]);
      setBindingSlot(null);
      setSelectedConnId('');
    } catch (err: any) {
      alert(err.message || 'Failed to assign connection slot.');
    } finally {
      setSavingBinding(false);
    }
  };

  const handleUnassignSlot = async (slot: string) => {
    if (!confirm(`Unassign ${slot.replace('_', ' ')} from this application?`)) return;
    try {
      await saveApplicationConnections(slotBindings.filter((binding: any) => binding.slot !== slot));
    } catch (err: any) {
      alert(err.message || 'Failed to unassign connection slot.');
    }
  };

  const handleGenerateManifest = async (event: React.FormEvent) => {
    event.preventDefault();
    if (!genConnId || !genOwner.trim()) return;
    setGenLoading(true);
    try {
      const owner = genOwner.trim().toUpperCase();
      await refreshDiscovery(Number(genConnId), owner);
      await generateManifest(appId, Number(genConnId), owner);
      setShowGenModal(false);
      setGenConnId('');
      setGenOwner('');
      await loadAll();
    } catch (err: any) {
      alert(err.message || 'Failed to auto-generate manifest.');
    } finally {
      setGenLoading(false);
    }
  };

  const handleLaunchJob = async (event: React.FormEvent) => {
    event.preventDefault();
    if (!runningManifest) return;
    if (targetSlot === 'pg_live' && confirmPhrase !== 'RUN LIVE MIGRATION') {
      setLaunchError('Type RUN LIVE MIGRATION exactly to run against a live target.');
      return;
    }

    setLaunchLoading(true);
    setLaunchError(null);
    try {
      const job = await createJob({
        applicationId: appId,
        manifestId: runningManifest.id,
        sourceSlot,
        targetSlot,
        targetSchema,
      });
      await launchJob(job.id);
      setRunningManifest(null);
      navigate(`/jobs/${job.id}`);
    } catch (err: any) {
      setLaunchError(err.message || 'Failed to launch migration job.');
    } finally {
      setLaunchLoading(false);
    }
  };

  if (loading) {
    return (
      <div className="card flex items-center justify-center gap-3 py-12 text-slate-300">
        <RefreshCw size={20} className="spin" />
        Loading application...
      </div>
    );
  }

  if (error || !app) {
    return (
      <div className="card flex flex-col items-center gap-4 py-12 text-center">
        <p className="text-rose-300">{error || 'Application not found.'}</p>
        <Link to="/applications" className="btn btn-secondary">Back to Applications</Link>
      </div>
    );
  }

  return (
    <div className="flex flex-col gap-6">
      <div className="flex flex-wrap items-start gap-4">
        <Link to="/applications" className="btn btn-secondary min-h-10 px-3" title="Back to applications">
          <ArrowLeft size={18} />
        </Link>
        <div className="min-w-0">
          <h1 className="text-gradient m-0 text-3xl font-bold leading-tight md:text-4xl">{app.name}</h1>
          <p className="mt-2 max-w-4xl text-base text-slate-400">{app.description || 'No description provided.'}</p>
        </div>
      </div>

      <div className="grid grid-cols-1 gap-6 xl:grid-cols-[minmax(0,1fr)_420px]">
        <section className="card p-0 overflow-hidden">
          <div className="flex flex-wrap items-center justify-between gap-4 border-b border-slate-800 bg-slate-950/40 p-5">
            <div className="flex items-center gap-3">
              <span className="rounded-lg bg-blue-500/10 p-2 text-blue-300">
                <FileText size={20} />
              </span>
              <div>
                <h2 className="m-0 text-xl font-semibold">Migration Manifests</h2>
                <p className="m-0 text-sm text-slate-400">Build table lists, review mappings, then launch controlled runs.</p>
              </div>
            </div>
            <div className="flex flex-wrap gap-2">
              <button className="btn btn-secondary" onClick={() => setShowGenModal(true)}>
                <Wand2 size={16} />
                Auto-Gen Manifest
              </button>
              <Link to={`/applications/${appId}/manifests/new/builder`} className="btn">
                Custom Builder
              </Link>
            </div>
          </div>

          <div className="flex flex-col gap-3 p-5">
            {manifests.map((manifest) => (
              <div key={manifest.id} className="rounded-lg border border-slate-800 bg-slate-950/50 p-4">
                <div className="flex flex-wrap items-center justify-between gap-4">
                  <div>
                    <h3 className="m-0 text-lg font-semibold">{manifest.name || `Manifest v${manifest.version}`}</h3>
                    <p className="m-0 mt-1 text-sm text-slate-400">
                      Created {new Date(manifest.createdAt).toLocaleString()}
                    </p>
                  </div>
                  <div className="flex gap-2">
                    <Link to={`/applications/${appId}/manifests/${manifest.id}/builder`} className="btn btn-secondary">
                      Edit
                    </Link>
                    <button className="btn bg-emerald-600" onClick={() => setRunningManifest(manifest)}>
                      <Play size={16} />
                      Run Job
                    </button>
                  </div>
                </div>
              </div>
            ))}

            {manifests.length === 0 && (
              <div className="rounded-lg border border-dashed border-slate-800 bg-slate-950/40 px-5 py-12 text-center">
                <p className="m-0 text-slate-300">No manifests found.</p>
                <p className="m-0 mt-2 text-sm text-slate-500">Use Auto-Gen Manifest after binding an Oracle profile, or open Custom Builder.</p>
              </div>
            )}
          </div>
        </section>

        <aside className="card p-0 overflow-hidden">
          <div className="flex items-center gap-3 border-b border-slate-800 bg-slate-950/40 p-5">
            <span className="rounded-lg bg-indigo-500/10 p-2 text-indigo-300">
              <Database size={20} />
            </span>
            <div>
              <h2 className="m-0 text-xl font-semibold">Connection Slots</h2>
              <p className="m-0 text-sm text-slate-400">Bind source and target databases for this application.</p>
            </div>
          </div>

          <div className="flex flex-col gap-3 p-5">
            {slots.map((slot) => {
              const binding = slotBindings.find((item: any) => item.slot === slot.key);
              const connection = binding ? connections.find((item) => item.id === binding.connectionId) : null;

              return (
                <div key={slot.key} className="rounded-lg border border-slate-800 bg-slate-950/50 p-4">
                  <div className="flex items-start justify-between gap-3">
                    <div className="min-w-0">
                      <span className={`inline-flex rounded-md border px-2 py-1 text-xs font-semibold uppercase ${slot.tone}`}>
                        {slot.label}
                      </span>
                      {connection ? (
                        <>
                          <div className="mt-3 truncate text-base font-semibold text-slate-100">{connection.name}</div>
                          <div className="mt-1 truncate font-mono text-xs text-slate-400">
                            {connection.host}:{connection.port}/{connection.serviceOrDb}
                          </div>
                        </>
                      ) : (
                        <div className="mt-3 text-sm font-semibold text-slate-500">Not Bound</div>
                      )}
                    </div>
                    {connection ? (
                      <button className="btn btn-secondary px-3 text-sm" onClick={() => handleUnassignSlot(slot.key)}>
                        <Unlink size={15} />
                        Unassign
                      </button>
                    ) : (
                      <button className="btn px-3 text-sm" onClick={() => openAssign(slot.key)}>
                        Assign
                      </button>
                    )}
                  </div>
                </div>
              );
            })}
          </div>
        </aside>
      </div>

      {bindingSlot && (
        <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/70 p-4 backdrop-blur-sm">
          <div className="card w-full max-w-md">
            <h3 className="m-0 text-xl font-semibold">Assign Connection Slot</h3>
            <p className="mt-2 text-sm text-slate-400">
              Select a matching database profile for <strong>{bindingSlot.replace('_', ' ')}</strong>.
            </p>
            <select
              className="input mt-5"
              value={selectedConnId}
              onChange={(event) => setSelectedConnId(Number(event.target.value))}
            >
              <option value="">Select connection profile</option>
              {connections
                .filter((connection) => connection.kind === slots.find((slot) => slot.key === bindingSlot)?.kind)
                .map((connection) => (
                  <option key={connection.id} value={connection.id}>
                    {connection.name} ({connection.host}:{connection.port})
                  </option>
                ))}
            </select>
            <div className="mt-6 flex justify-end gap-3">
              <button className="btn btn-secondary" onClick={() => setBindingSlot(null)}>Cancel</button>
              <button className="btn" onClick={handleAssignSlot} disabled={!selectedConnId || savingBinding}>
                {savingBinding ? 'Assigning...' : 'Assign'}
              </button>
            </div>
          </div>
        </div>
      )}

      {showGenModal && (
        <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/70 p-4 backdrop-blur-sm">
          <div className="card w-full max-w-lg">
            <h3 className="m-0 text-xl font-semibold">Auto-Generate Manifest</h3>
            <p className="mt-2 text-sm text-slate-400">
              Discover Oracle tables and build a migration manifest for the selected owner/schema.
            </p>
            <form onSubmit={handleGenerateManifest} className="mt-5 flex flex-col gap-4">
              <div>
                <label className="label">Oracle Discovery Connection</label>
                <select className="input" value={genConnId} onChange={(event) => setGenConnId(Number(event.target.value))} required>
                  <option value="">Select Oracle profile</option>
                  {oracleConnections.map((connection) => (
                    <option key={connection.id} value={connection.id}>{connection.name}</option>
                  ))}
                </select>
              </div>
              <div>
                <label className="label">Oracle Schema / Owner</label>
                <input className="input" value={genOwner} onChange={(event) => setGenOwner(event.target.value.toUpperCase())} placeholder="HR" required />
              </div>
              <div className="flex justify-end gap-3 pt-2">
                <button type="button" className="btn btn-secondary" onClick={() => setShowGenModal(false)}>Cancel</button>
                <button type="submit" className="btn" disabled={genLoading || !genConnId || !genOwner.trim()}>
                  {genLoading ? <RefreshCw size={16} className="spin" /> : <Wand2 size={16} />}
                  Generate
                </button>
              </div>
            </form>
          </div>
        </div>
      )}

      {runningManifest && (
        <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/70 p-4 backdrop-blur-sm">
          <div className="card w-full max-w-xl">
            <h3 className="m-0 flex items-center gap-2 text-xl font-semibold">
              <Play size={20} className="text-emerald-300" />
              Launch Migration Job
            </h3>
            {launchError && (
              <div className="mt-4 rounded-lg border border-rose-500/30 bg-rose-500/10 p-3 text-sm text-rose-200">
                {launchError}
              </div>
            )}
            <form onSubmit={handleLaunchJob} className="mt-5 flex flex-col gap-4">
              <div className="grid grid-cols-1 gap-4 sm:grid-cols-2">
                <div>
                  <label className="label">Source Slot</label>
                  <select className="input" value={sourceSlot} onChange={(event) => setSourceSlot(event.target.value)}>
                    <option value="oracle_test">Oracle Test</option>
                    <option value="oracle_live">Oracle Live</option>
                  </select>
                </div>
                <div>
                  <label className="label">Target Slot</label>
                  <select className="input" value={targetSlot} onChange={(event) => setTargetSlot(event.target.value)}>
                    <option value="pg_test">Postgres Test</option>
                    <option value="pg_live">Postgres Live</option>
                  </select>
                </div>
              </div>
              <div>
                <label className="label">Target Postgres Schema</label>
                <input className="input" value={targetSchema} onChange={(event) => setTargetSchema(event.target.value)} required />
              </div>
              {targetSlot === 'pg_live' && (
                <div className="rounded-lg border border-amber-500/30 bg-amber-500/10 p-4">
                  <div className="flex items-center gap-2 text-sm font-semibold text-amber-200">
                    <ShieldAlert size={18} />
                    Live target selected
                  </div>
                  <p className="mt-2 text-sm text-slate-300">Type RUN LIVE MIGRATION to continue.</p>
                  <input className="input mt-3" value={confirmPhrase} onChange={(event) => setConfirmPhrase(event.target.value)} />
                </div>
              )}
              <div className="flex justify-end gap-3 pt-2">
                <button type="button" className="btn btn-secondary" onClick={() => { setRunningManifest(null); setLaunchError(null); }}>Cancel</button>
                <button type="submit" className="btn bg-emerald-600" disabled={launchLoading || oracleConnections.length === 0 || pgConnections.length === 0}>
                  {launchLoading ? <RefreshCw size={16} className="spin" /> : <Play size={16} />}
                  Launch Migration
                </button>
              </div>
            </form>
          </div>
        </div>
      )}
    </div>
  );
}
