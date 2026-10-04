import { useEffect, useMemo, useState } from 'react';
import { Link, useNavigate, useParams } from 'react-router-dom';
import {
  ArrowLeft,
  Database,
  FileText,
  ListChecks,
  Pencil,
  Play,
  RefreshCw,
  ShieldAlert,
  Trash2,
  Unlink,
  Wand2,
} from 'lucide-react';
import {
  createJob,
  deleteApplication,
  deleteManifest,
  fetchApplication,
  hasRole,
  renameApplication,
  renameManifest,
  fetchConnections,
  fetchManifests,
  generateManifest,
  launchJob,
  refreshDiscovery,
  updateApplication,
} from '../api';
import SchemaCombobox from '../components/SchemaCombobox';
import { ReadinessDialog } from '../components/ChangeTracking';
import { SLOTS, slotLabel } from '../labels';

const TONE_BY_KIND: Record<number, string> = {
  0: 'text-rose-300 border-rose-500/20 bg-rose-500/10',
  1: 'text-sky-300 border-sky-500/20 bg-sky-500/10',
};

const slots = SLOTS.map((s) => ({ ...s, tone: TONE_BY_KIND[s.kind] }));

// Inline, not a Tailwind colour class: the unlayered global .btn rule wins over utilities. Same red as
// the Delete buttons on the Runs pages.
const DANGER = { color: '#f87171', borderColor: 'rgba(248,113,113,0.4)' };

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
  const [readinessManifest, setReadinessManifest] = useState<any>(null);
  const [sourceSlot, setSourceSlot] = useState('oracle_test');
  const [targetSlot, setTargetSlot] = useState('pg_test');
  const [targetSchema, setTargetSchema] = useState('public');
  const [confirmPhrase, setConfirmPhrase] = useState('');
  const [launchLoading, setLaunchLoading] = useState(false);
  const [launchError, setLaunchError] = useState<string | null>(null);

  // Renaming and deleting. Admins and Operators may rename anything and delete a table selection; only
  // an Admin may delete the whole migration - the same rules the server enforces.
  const canEdit = hasRole('Admin') || hasRole('Operator');
  const canDeleteMigration = hasRole('Admin');
  const [editingApp, setEditingApp] = useState(false);
  const [appName, setAppName] = useState('');
  const [appDescription, setAppDescription] = useState('');
  const [savingApp, setSavingApp] = useState(false);
  const [appEditError, setAppEditError] = useState<string | null>(null);
  const [renamingManifestId, setRenamingManifestId] = useState<number | null>(null);
  const [manifestName, setManifestName] = useState('');
  const [manifestEditError, setManifestEditError] = useState<string | null>(null);
  const [busyManifestId, setBusyManifestId] = useState<number | null>(null);

  const slotBindings = app?.connections || [];
  const oracleConnections = useMemo(() => connections.filter((c) => c.kind === 0), [connections]);
  const pgConnections = useMemo(() => connections.filter((c) => c.kind === 1), [connections]);

  // Must match JobsController.LaunchJob exactly, or the server rejects the launch.
  const livePhrase = `MIGRATE ${app?.name ?? ''} LIVE`;

  // The destination picker lists schemas from whichever database the chosen "Copy to" role points at.
  const targetConnectionId: number | '' =
    slotBindings.find((b: any) => b.slot === targetSlot)?.connectionId ?? '';

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
      alert(err.message || 'Could not connect that database.');
    } finally {
      setSavingBinding(false);
    }
  };

  const handleUnassignSlot = async (slot: string) => {
    if (!confirm(`Remove the ${slotLabel(slot)} database from this migration?`)) return;
    try {
      await saveApplicationConnections(slotBindings.filter((binding: any) => binding.slot !== slot));
    } catch (err: any) {
      alert(err.message || 'Could not remove the database.');
    }
  };

  const startEditingApp = () => {
    setAppName(app.name);
    setAppDescription(app.description ?? '');
    setAppEditError(null);
    setEditingApp(true);
  };

  const handleSaveApp = async (event: React.FormEvent) => {
    event.preventDefault();
    setSavingApp(true);
    setAppEditError(null);
    try {
      const saved = await renameApplication(appId, { name: appName, description: appDescription });
      setApp((current: any) => ({ ...current, name: saved.name, description: saved.description }));
      setEditingApp(false);
    } catch (err: any) {
      setAppEditError(err.message);
    } finally {
      setSavingApp(false);
    }
  };

  const handleDeleteApp = async () => {
    if (!confirm(
      `Delete the migration "${app.name}"?\n\nThis also deletes its ${manifests.length} table selection(s), its database choices and ` +
      'its run history, for good. Tables already copied into the destination are not touched.'
    )) return;
    try {
      await deleteApplication(appId);
      navigate('/applications');
    } catch (err: any) {
      alert(err.message);
    }
  };

  const startRenamingManifest = (manifest: any) => {
    setRenamingManifestId(manifest.id);
    setManifestName(manifest.name || '');
    setManifestEditError(null);
  };

  const handleRenameManifest = async (event: React.FormEvent, manifestId: number) => {
    event.preventDefault();
    setBusyManifestId(manifestId);
    setManifestEditError(null);
    try {
      const saved = await renameManifest(manifestId, manifestName);
      setManifests((list) => list.map((m) => (m.id === manifestId ? { ...m, name: saved.name } : m)));
      setRenamingManifestId(null);
    } catch (err: any) {
      setManifestEditError(err.message);
    } finally {
      setBusyManifestId(null);
    }
  };

  const handleDeleteManifest = async (manifest: any) => {
    const label = manifest.name || `Table selection v${manifest.version}`;
    if (!confirm(
      `Delete the table selection "${label}"?\n\nThis also deletes the history of every run made from it, for good. ` +
      'Tables already copied into the destination are not touched.'
    )) return;
    setBusyManifestId(manifest.id);
    try {
      await deleteManifest(manifest.id);
      setManifests((list) => list.filter((m) => m.id !== manifest.id));
    } catch (err: any) {
      alert(err.message);
    } finally {
      setBusyManifestId(null);
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
      alert(err.message || 'Could not find the tables automatically.');
    } finally {
      setGenLoading(false);
    }
  };

  const handleLaunchJob = async (event: React.FormEvent) => {
    event.preventDefault();
    if (!runningManifest) return;
    // The server requires this exact phrase for a live destination (JobsController.LaunchJob), so
    // check the same phrase here and then actually send it - the UI used to ask for a different
    // wording and then pass nothing, which made every live run fail.
    const isLiveTarget = targetSlot.endsWith('live');
    if (isLiveTarget && confirmPhrase !== livePhrase) {
      setLaunchError(`Type ${livePhrase} exactly to run against a live database.`);
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
      await launchJob(job.id, isLiveTarget ? confirmPhrase : undefined);
      setRunningManifest(null);
      navigate(`/jobs/${job.id}`);
    } catch (err: any) {
      setLaunchError(err.message || 'Could not start the run.');
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
        <Link to="/applications" className="btn btn-secondary">Back to Migrations</Link>
      </div>
    );
  }

  return (
    <div className="flex flex-col gap-6">
      <div className="flex flex-wrap items-start gap-4">
        <Link to="/applications" className="btn btn-secondary min-h-10 px-3" title="Back to migrations">
          <ArrowLeft size={18} />
        </Link>
        {editingApp ? (
          <form onSubmit={handleSaveApp} className="flex min-w-0 flex-1 flex-col gap-3" aria-label="Rename migration">
            <div>
              <label className="label" htmlFor="app-name">Migration name</label>
              <input id="app-name" className="input" value={appName} maxLength={200} autoFocus required
                onChange={(event) => setAppName(event.target.value)} />
            </div>
            <div>
              <label className="label" htmlFor="app-description">Description</label>
              <textarea id="app-description" className="input" rows={2} value={appDescription}
                onChange={(event) => setAppDescription(event.target.value)} />
            </div>
            {appEditError && <p className="m-0 text-sm text-rose-300" role="alert">{appEditError}</p>}
            <div className="flex gap-2">
              <button type="submit" className="btn" disabled={savingApp || !appName.trim()}>
                {savingApp ? 'Saving…' : 'Save'}
              </button>
              <button type="button" className="btn btn-secondary" onClick={() => setEditingApp(false)}>Cancel</button>
            </div>
          </form>
        ) : (
          <>
            <div className="min-w-0 flex-1">
              <h1 className="text-gradient m-0 text-3xl font-bold leading-tight md:text-4xl">{app.name}</h1>
              <p className="mt-2 max-w-4xl text-base text-slate-400">{app.description || 'No description provided.'}</p>
            </div>
            <div className="flex flex-wrap gap-2">
              {canEdit && (
                <button className="btn btn-secondary" onClick={startEditingApp}>
                  <Pencil size={16} />
                  Rename
                </button>
              )}
              {canDeleteMigration && (
                <button className="btn btn-secondary" onClick={handleDeleteApp} style={DANGER}>
                  <Trash2 size={16} />
                  Delete migration
                </button>
              )}
            </div>
          </>
        )}
      </div>

      <div className="grid grid-cols-1 gap-6 xl:grid-cols-[minmax(0,1fr)_420px]">
        <section className="card p-0 overflow-hidden">
          <div className="flex flex-wrap items-center justify-between gap-4 border-b border-slate-800 bg-slate-950/40 p-5">
            <div className="flex items-center gap-3">
              <span className="rounded-lg bg-blue-500/10 p-2 text-blue-300">
                <FileText size={20} />
              </span>
              <div>
                <h2 className="m-0 text-xl font-semibold">Table Selections</h2>
                <p className="m-0 text-sm text-slate-400">Choose which tables to copy, then start a run.</p>
              </div>
            </div>
            <div className="flex flex-wrap gap-2">
              <button className="btn btn-secondary" onClick={() => setShowGenModal(true)}>
                <Wand2 size={16} />
                Find Tables Automatically
              </button>
              <Link to={`/applications/${appId}/manifests/new/builder`} className="btn">
                Choose Tables
              </Link>
            </div>
          </div>

          <div className="flex flex-col gap-3 p-5">
            {manifests.map((manifest) => (
              <div key={manifest.id} className="rounded-lg border border-slate-800 bg-slate-950/50 p-4">
                <div className="flex flex-wrap items-center justify-between gap-4">
                  {renamingManifestId === manifest.id ? (
                    <form onSubmit={(event) => handleRenameManifest(event, manifest.id)} className="flex min-w-0 flex-1 flex-col gap-2"
                      aria-label="Rename table selection">
                      <div className="flex flex-wrap gap-2">
                        <input className="input min-w-0 flex-1" value={manifestName} maxLength={200} autoFocus required
                          aria-label="Table selection name" onChange={(event) => setManifestName(event.target.value)} />
                        <button type="submit" className="btn" disabled={busyManifestId === manifest.id || !manifestName.trim()}>
                          {busyManifestId === manifest.id ? 'Saving…' : 'Save'}
                        </button>
                        <button type="button" className="btn btn-secondary" onClick={() => setRenamingManifestId(null)}>Cancel</button>
                      </div>
                      {manifestEditError && <p className="m-0 text-sm text-rose-300" role="alert">{manifestEditError}</p>}
                    </form>
                  ) : (
                    <div className="min-w-0">
                      <div className="flex items-center gap-2">
                        <h3 className="m-0 text-lg font-semibold">{manifest.name || `Table selection v${manifest.version}`}</h3>
                        {canEdit && (
                          <button
                            className="rounded p-1 text-slate-400 hover:bg-slate-900 hover:text-slate-100"
                            onClick={() => startRenamingManifest(manifest)}
                            title="Rename"
                            aria-label={`Rename ${manifest.name || 'table selection'}`}
                          >
                            <Pencil size={15} />
                          </button>
                        )}
                      </div>
                      <p className="m-0 mt-1 text-sm text-slate-400">
                        Created {new Date(manifest.createdAt).toLocaleString()}
                      </p>
                    </div>
                  )}
                  <div className="flex flex-wrap gap-2">
                    <Link to={`/applications/${appId}/manifests/${manifest.id}/builder`} className="btn btn-secondary">
                      Edit
                    </Link>
                    <button
                      className="btn btn-secondary"
                      onClick={() => setReadinessManifest(manifest)}
                      title="Check whether the source database is ready for Copy changes. Read-only."
                    >
                      <ListChecks size={16} />
                      Check change tracking
                    </button>
                    <button className="btn bg-emerald-600" onClick={() => { setLaunchError(null); setRunningManifest(manifest); }}>
                      <Play size={16} />
                      Start Run
                    </button>
                    {canEdit && (
                      <button
                        className="btn btn-secondary px-3"
                        style={DANGER}
                        onClick={() => handleDeleteManifest(manifest)}
                        disabled={busyManifestId === manifest.id}
                        title="Delete this table selection"
                        aria-label={`Delete ${manifest.name || 'table selection'}`}
                      >
                        <Trash2 size={16} />
                      </button>
                    )}
                  </div>
                </div>
              </div>
            ))}

            {manifests.length === 0 && (
              <div className="rounded-lg border border-dashed border-slate-800 bg-slate-950/40 px-5 py-12 text-center">
                <p className="m-0 text-slate-300">No table selections yet.</p>
                <p className="m-0 mt-2 text-sm text-slate-500">Connect a source database first, then use Find Tables Automatically or Choose Tables.</p>
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
              <h2 className="m-0 text-xl font-semibold">Databases</h2>
              <p className="m-0 text-sm text-slate-400">Pick the source and destination databases for this migration.</p>
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
                        <div className="mt-3 text-sm font-semibold text-slate-500">Not set</div>
                      )}
                    </div>
                    {connection ? (
                      <button className="btn btn-secondary px-3 text-sm" onClick={() => handleUnassignSlot(slot.key)}>
                        <Unlink size={15} />
                        Remove
                      </button>
                    ) : (
                      <button className="btn px-3 text-sm" onClick={() => openAssign(slot.key)}>
                        Choose
                      </button>
                    )}
                  </div>
                </div>
              );
            })}
          </div>
        </aside>
      </div>

      <p className="text-sm text-slate-400">After the bulk copy, manage change tracking beside each table on <Link to="/">Dashboard</Link>.</p>

      {readinessManifest && (
        <ReadinessDialog appId={appId} manifest={readinessManifest} onClose={() => setReadinessManifest(null)} />
      )}

      {bindingSlot && (
        <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/70 p-4 backdrop-blur-sm">
          <div className="card w-full max-w-md">
            <h3 className="m-0 text-xl font-semibold">Choose a database</h3>
            <p className="mt-2 text-sm text-slate-400">
              Pick the database to use as <strong>{slotLabel(bindingSlot)}</strong>.
            </p>
            <select
              className="input mt-5"
              value={selectedConnId}
              onChange={(event) => setSelectedConnId(Number(event.target.value))}
            >
              <option value="">Select a database</option>
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
                {savingBinding ? 'Saving...' : 'Use this database'}
              </button>
            </div>
          </div>
        </div>
      )}

      {showGenModal && (
        <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/70 p-4 backdrop-blur-sm">
          <div className="card w-full max-w-lg">
            <h3 className="m-0 text-xl font-semibold">Find tables automatically</h3>
            <p className="mt-2 text-sm text-slate-400">
              Scan the source database and build a table selection from one of its schemas.
            </p>
            <form onSubmit={handleGenerateManifest} className="mt-5 flex flex-col gap-4">
              <div>
                <label className="label" htmlFor="gen-source-db">Source database to scan</label>
                <select id="gen-source-db" className="input" value={genConnId} onChange={(event) => setGenConnId(Number(event.target.value))} required>
                  <option value="">Select a source database</option>
                  {oracleConnections.map((connection) => (
                    <option key={connection.id} value={connection.id}>{connection.name}</option>
                  ))}
                </select>
              </div>
              <div>
                <label className="label" htmlFor="gen-source-schema">Source schema</label>
                <SchemaCombobox
                  id="gen-source-schema"
                  size="md"
                  connectionId={genConnId}
                  value={genOwner}
                  onChange={setGenOwner}
                />
              </div>
              <div className="flex justify-end gap-3 pt-2">
                <button type="button" className="btn btn-secondary" onClick={() => setShowGenModal(false)}>Cancel</button>
                <button type="submit" className="btn" disabled={genLoading || !genConnId || !genOwner.trim()}>
                  {genLoading ? <RefreshCw size={16} className="spin" /> : <Wand2 size={16} />}
                  Find tables
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
              <Play size={20} className="text-emerald-300" /> Start a run
            </h3>
            {/* whitespace-pre-line: a failed readiness check lists one line per check. */}
            {launchError && (
              <div className="mt-4 whitespace-pre-line rounded-lg border border-rose-500/30 bg-rose-500/10 p-3 text-sm text-rose-200">
                {launchError}
              </div>
            )}
            <form onSubmit={handleLaunchJob} className="mt-5 flex flex-col gap-4">
              <div className="grid grid-cols-1 gap-4 sm:grid-cols-2">
                <div>
                  <label className="label">Copy from</label>
                  <select className="input" value={sourceSlot} onChange={(event) => setSourceSlot(event.target.value)}>
                    {SLOTS.filter((s) => s.kind === 0).map((s) => (
                      <option key={s.key} value={s.key}>{s.label}</option>
                    ))}
                  </select>
                </div>
                <div>
                  <label className="label">Copy to</label>
                  <select className="input" value={targetSlot} onChange={(event) => setTargetSlot(event.target.value)}>
                    {SLOTS.filter((s) => s.kind === 1).map((s) => (
                      <option key={s.key} value={s.key}>{s.label}</option>
                    ))}
                  </select>
                </div>
              </div>
              <div>
                <label className="label" htmlFor="run-target-schema">Destination schema</label>
                <SchemaCombobox
                  id="run-target-schema"
                  size="md"
                  variant="postgres"
                  connectionId={targetConnectionId}
                  value={targetSchema}
                  onChange={setTargetSchema}
                />
              </div>
              {targetSlot.endsWith('live') && (
                <div className="rounded-lg border border-amber-500/30 bg-amber-500/10 p-4">
                  <div className="flex items-center gap-2 text-sm font-semibold text-amber-200">
                    <ShieldAlert size={18} />
                    You are copying into a live database
                  </div>
                  <p className="mt-2 text-sm text-slate-300">
                    Type <strong className="font-mono">{livePhrase}</strong> to continue.
                  </p>
                  <input className="input mt-3" value={confirmPhrase} onChange={(event) => setConfirmPhrase(event.target.value)} />
                </div>
              )}
              <div className="flex justify-end gap-3 pt-2">
                <button type="button" className="btn btn-secondary" onClick={() => { setRunningManifest(null); setLaunchError(null); }}>Cancel</button>
                <button type="submit" className="btn bg-emerald-600" disabled={launchLoading || oracleConnections.length === 0 || pgConnections.length === 0}>
                  {launchLoading ? <RefreshCw size={16} className="spin" /> : <Play size={16} />}
                  Start run
                </button>
              </div>
            </form>
          </div>
        </div>
      )}
    </div>
  );
}
