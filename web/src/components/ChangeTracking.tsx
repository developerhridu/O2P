import { useState } from 'react';
import { CheckCircle2, Copy, ListChecks, RefreshCw, XCircle } from 'lucide-react';
import { checkChangeReadiness, checkTableReadiness, type ReadinessTable, type ChangeReadiness } from '../api';
import { SLOTS } from '../labels';
import './ChangeTracking.css';

/** Read-only check of the source, with the SQL a DBA would run for anything missing. */
type ReadinessProps = { onClose: () => void } & (
  { appId: number; manifest: any; table?: never } |
  { appId?: never; manifest?: never; table: ReadinessTable }
);
export function ReadinessDialog({ appId, manifest, table, onClose }: ReadinessProps) {
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
      setResult(table ? await checkTableReadiness(table) : await checkChangeReadiness(appId!, sourceSlot, manifest.id));
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
      <div className="card w-full max-w-3xl max-h-[90vh] overflow-y-auto" role="dialog" aria-modal="true" aria-labelledby="readiness-title">
        <h3 id="readiness-title" className="m-0 flex items-center gap-2 text-xl font-semibold">
          <ListChecks size={20} className="text-sky-300" />
          Check change tracking
        </h3>
        <p className="mt-2 text-sm text-slate-400">
          Checks what Copy changes needs for {table ? `${table.sourceOwner}.${table.sourceTable}` :
            `the selected tables in ${manifest.name || `Table selection v${manifest.version}`}`}.
          This check only reads the source; it changes nothing.
        </p>

        <div className="mt-4 flex flex-wrap items-end gap-3">
          {!table && <div>
            <label className="label" htmlFor="readiness-source">Source</label>
            <select id="readiness-source" className="input" value={sourceSlot} onChange={(e) => setSourceSlot(e.target.value)}>
              {SLOTS.filter((s) => s.kind === 0).map((s) => (
                <option key={s.key} value={s.key}>{s.label}</option>
              ))}
            </select>
          </div>}
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
