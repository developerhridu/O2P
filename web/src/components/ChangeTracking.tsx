import { useEffect, useState } from 'react';
import { Link } from 'react-router-dom';
import { CheckCircle2, Copy, History, ListChecks, RefreshCw, XCircle } from 'lucide-react';
import { checkChangeReadiness, fetchTrackedTables, type ChangeReadiness, type TrackedTable } from '../api';
import { SLOTS, trackingLabel } from '../labels';
import './ChangeTracking.css';

/**
 * The tables of this migration that "Copy changes" keeps up to date, and where each one stands.
 * Refreshes when `refreshKey` changes, so the page can ask for a reload after starting a copy.
 */
export function TrackedTablesPanel({ appId, refreshKey }: { appId: number; refreshKey: number }) {
  const [tables, setTables] = useState<TrackedTable[] | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let alive = true;
    fetchTrackedTables(appId)
      .then((t) => { if (alive) { setTables(t); setError(null); } })
      .catch((e) => { if (alive) setError(e.message); });
    return () => { alive = false; };
  }, [appId, refreshKey]);

  return (
    <section className="card p-0 overflow-hidden" aria-labelledby="tracked-heading">
      <div className="flex items-center gap-3 border-b border-slate-800 bg-slate-950/40 p-5">
        <span className="rounded-lg bg-emerald-500/10 p-2 text-emerald-300">
          <History size={20} />
        </span>
        <div>
          <h2 id="tracked-heading" className="m-0 text-xl font-semibold">Change tracking</h2>
          <p className="m-0 text-sm text-slate-400">
            After a bulk copy, <strong>Copy changes</strong> brings these tables up to date with the source: new rows added,
            changed rows updated, deleted rows deleted. Nothing is emptied or recreated.
          </p>
        </div>
      </div>

      <div className="p-5">
        {error && <p className="m-0 text-sm text-rose-300">{error}</p>}
        {!error && tables === null && <p className="m-0 text-sm text-slate-400">Loading…</p>}
        {!error && tables?.length === 0 && (
          <p className="m-0 text-sm text-slate-400">
            No table is tracked yet. Run a bulk copy of a table selection; each table with a primary key is then set up
            for change tracking automatically, if the source is ready for it (<strong>Check change tracking</strong> says).
          </p>
        )}
        {!error && tables && tables.length > 0 && (
          <div className="overflow-x-auto">
            <table className="ct-table" data-testid="tracked-tables">
              <thead>
                <tr>
                  <th>Source table</th>
                  <th>Destination</th>
                  <th>Status</th>
                  <th>Last copied</th>
                  <th>Notes</th>
                </tr>
              </thead>
              <tbody>
                {tables.map((t) => {
                  const status = trackingLabel(t.status);
                  return (
                    <tr key={t.id} data-table={t.targetTableName}>
                      <td className="font-mono">{t.sourceOwner}.{t.sourceTable}</td>
                      <td className="font-mono">{t.targetSchema}.{t.targetTableName}</td>
                      <td>
                        <span className="ct-status" style={{ color: status.color }} title={status.hint}>{status.label}</span>
                        {t.activeJobRunId != null && (
                          <div className="text-xs text-slate-400">
                            Copying now in <Link to={`/jobs/${t.activeJobRunId}`}>run #{t.activeJobRunId}</Link>
                          </div>
                        )}
                      </td>
                      <td className="whitespace-nowrap">{t.lastSyncedAt ? new Date(t.lastSyncedAt).toLocaleString() : 'Not yet'}</td>
                      <td className="text-sm">
                        {t.lastError && <div className="text-rose-300">{t.lastError}</div>}
                        {t.heldBackBy && t.heldBackBy.length > 0 && (
                          <div className="text-amber-200" title="These Oracle transactions were still open, so the next copy looks at their rows again once they commit.">
                            Held back by {t.heldBackBy.length} open Oracle transaction{t.heldBackBy.length > 1 ? 's' : ''}
                            {t.heldBackBy[0].username ? ` (${[t.heldBackBy[0].username, t.heldBackBy[0].program].filter(Boolean).join(', ')}${t.heldBackBy.length > 1 ? ', …' : ''})` : ''}
                          </div>
                        )}
                      </td>
                    </tr>
                  );
                })}
              </tbody>
            </table>
          </div>
        )}
        <p className="m-0 mt-4 text-xs text-slate-500">
          Not carried across: new columns added in the source, and anything written to the source without logging.
          Keep the source's archived logs for longer than the gap between presses, or the history in between is lost and
          a bulk copy is needed again.
        </p>
      </div>
    </section>
  );
}

/** Read-only check of the source, with the SQL a DBA would run for anything missing. */
export function ReadinessDialog({ appId, manifest, onClose }: { appId: number; manifest: any; onClose: () => void }) {
  const [sourceSlot, setSourceSlot] = useState('oracle_test');
  const [result, setResult] = useState<ChangeReadiness | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [checking, setChecking] = useState(false);
  const [copied, setCopied] = useState(false);

  const run = async () => {
    setChecking(true);
    setError(null);
    setResult(null);
    try {
      setResult(await checkChangeReadiness(appId, sourceSlot, manifest.id));
    } catch (e: any) {
      setError(e.message);
    } finally {
      setChecking(false);
    }
  };

  const fixSql = result
    ? [...result.items.filter((i) => !i.ok && i.fixSql).map((i) => i.fixSql!), ...result.tables.flatMap((t) => t.fixSql)].join('\n')
    : '';

  const copySql = async () => {
    try {
      await navigator.clipboard.writeText(fixSql);
      setCopied(true);
      setTimeout(() => setCopied(false), 2000);
    } catch { /* clipboard unavailable - the SQL is on screen to select */ }
  };

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/70 p-4 backdrop-blur-sm">
      <div className="card w-full max-w-3xl max-h-[90vh] overflow-y-auto" role="dialog" aria-labelledby="readiness-title">
        <h3 id="readiness-title" className="m-0 flex items-center gap-2 text-xl font-semibold">
          <ListChecks size={20} className="text-sky-300" />
          Check change tracking
        </h3>
        <p className="mt-2 text-sm text-slate-400">
          Looks at the source database for everything <strong>Copy changes</strong> needs, for the ticked tables of
          “{manifest.name || `Table selection v${manifest.version}`}”. It only reads; it changes nothing.
        </p>

        <div className="mt-4 flex flex-wrap items-end gap-3">
          <div>
            <label className="label" htmlFor="readiness-source">Source</label>
            <select id="readiness-source" className="input" value={sourceSlot} onChange={(e) => setSourceSlot(e.target.value)}>
              {SLOTS.filter((s) => s.kind === 0).map((s) => (
                <option key={s.key} value={s.key}>{s.label}</option>
              ))}
            </select>
          </div>
          <button className="btn" onClick={run} disabled={checking}>
            {checking ? <RefreshCw size={16} className="spin" /> : <ListChecks size={16} />}
            {checking ? 'Checking…' : 'Check'}
          </button>
        </div>

        {error && <div className="mt-4 rounded-lg border border-rose-500/30 bg-rose-500/10 p-3 text-sm text-rose-200">{error}</div>}

        {result && (
          <div className="mt-5 flex flex-col gap-4" data-testid="readiness-result">
            <div className={`rounded-lg border p-3 text-sm ${result.ready ? 'border-emerald-500/30 bg-emerald-500/10 text-emerald-200' : 'border-amber-500/30 bg-amber-500/10 text-amber-200'}`}>
              {result.ready
                ? 'Everything is in place. Tables copied from now on can have their changes copied.'
                : 'Some things are missing. They are listed below with the SQL for your DBA.'}
              <div className="mt-1 text-slate-300">
                The source is {result.layout}{result.version ? `, Oracle ${result.version}` : ''}.
                {result.oldestHistory && ` Its change history goes back to ${new Date(result.oldestHistory).toLocaleString()}.`}
              </div>
            </div>

            <ul className="ct-checks">
              {result.items.map((item) => (
                <li key={item.name} data-ok={item.ok}>
                  {item.ok ? <CheckCircle2 size={16} className="text-emerald-400" /> : <XCircle size={16} className="text-rose-400" />}
                  <div>
                    <div className="font-semibold">{item.name}</div>
                    <div className="text-sm text-slate-400">{item.detail}</div>
                  </div>
                </li>
              ))}
            </ul>

            {result.tables.length > 0 && (
              <div>
                <h4 className="m-0 mb-2 text-base font-semibold">Tables</h4>
                <ul className="ct-checks">
                  {result.tables.map((t) => (
                    <li key={`${t.owner}.${t.table}`} data-ok={t.ok} data-table={t.table}>
                      {t.ok ? <CheckCircle2 size={16} className="text-emerald-400" /> : <XCircle size={16} className="text-rose-400" />}
                      <div>
                        <div className="font-mono">{t.owner}.{t.table}</div>
                        {t.problems.map((p) => <div key={p} className="text-sm text-slate-400">{p}</div>)}
                      </div>
                    </li>
                  ))}
                </ul>
              </div>
            )}

            {fixSql && (
              <div>
                <div className="mb-2 flex items-center justify-between gap-2">
                  <h4 className="m-0 text-base font-semibold">For your DBA</h4>
                  <button className="btn btn-secondary px-3 text-sm" onClick={copySql}>
                    <Copy size={14} />
                    {copied ? 'Copied' : 'Copy SQL'}
                  </button>
                </div>
                <pre className="ct-sql" data-testid="fix-sql">{fixSql}</pre>
              </div>
            )}
          </div>
        )}

        <div className="mt-6 flex justify-end">
          <button className="btn btn-secondary" onClick={onClose}>Close</button>
        </div>
      </div>
    </div>
  );
}
