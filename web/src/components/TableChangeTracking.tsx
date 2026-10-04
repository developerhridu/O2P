import { useEffect, useRef, useState } from 'react';
import { Link } from 'react-router-dom';
import { copyTableChanges, type DashboardTrackedTable, type ReadinessTable, type TablePair } from '../api';
import { formatAgo } from '../format';
import { trackingLabel } from '../labels';

export function TableTrackingCells({ pair, canCopy, onCheck, onCopy, sourceConnectionId, sourceOwner }: {
  pair: TablePair; canCopy: boolean; onCheck: (table: ReadinessTable) => void;
  onCopy: (table: DashboardTrackedTable) => void; sourceConnectionId: number; sourceOwner: string;
}) {
  const t = pair.tracking;
  const status = t ? trackingLabel(t.status) : null;
  const reason = !t ? 'Run a bulk copy with a ready source to establish tracking.'
    : t.activeJobRunId ? 'A copy is already queued or running for this table.'
    : !['ready', 'needs_first_sync'].includes(t.status) ? t.lastError || 'Run a new bulk copy before copying changes.' : '';
  return <>
    <td className="dash-tracking">
      <span className="ct-status" style={{ color: status?.color }} title={status?.hint}>
        {t?.activeJobRunId ? (t.activeStatus === 'Queued' ? 'Queued' : 'Copying') : status?.label ?? 'Not tracked'}
      </span>
      {t?.activeJobRunId && <div><Link to={`/jobs/${t.activeJobRunId}`}>Run #{t.activeJobRunId}</Link></div>}
      <details>
        <summary>Details</summary>
        <p>{reason || 'Copy changes applies inserts, updates and deletes since the last successful copy.'}</p>
        {t?.lastError && <p className="dash-bad">{t.lastError}</p>}
        {!!t?.heldBackBy?.length && <p>Held back by {t.heldBackBy.length} open Oracle transaction(s).
          {' '}{t.heldBackBy.map(x => [x.username, x.program].filter(Boolean).join(' / ')).filter(Boolean).join(', ')}</p>}
        {t?.lastScn && <p>Resume point: {t.lastScn}</p>}
        <p>Matching row counts alone do not establish that data is synchronized.</p>
      </details>
    </td>
    <td title={t?.lastSyncedAt ? new Date(t.lastSyncedAt).toLocaleString() : undefined}>
      {t?.lastSyncedAt ? formatAgo(t.lastSyncedAt) : 'Not yet'}
    </td>
    <td><div className="dash-row-actions">
      {canCopy && pair.source && <button className="btn btn-secondary dash-btn" onClick={() => onCheck({
        sourceConnectionId, sourceOwner, sourceTable: pair.source!.table,
      })}>Check readiness</button>}
      {canCopy && <button className="btn dash-btn" disabled={!!reason} title={reason || 'Copy changes for this table'}
        onClick={() => t && onCopy(t)}>Copy changes</button>}
      {!canCopy && <span className="dash-muted">View only</span>}
    </div></td>
  </>;
}

export function TableCopyDialog({ table, targetName, onClose, onStarted }: {
  table: DashboardTrackedTable; targetName: string; onClose: () => void; onStarted: (id: number) => void;
}) {
  const dialog = useRef<HTMLDialogElement>(null);
  const [phrase, setPhrase] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const expected = `COPY ${targetName}/${table.targetSchema}.${table.targetTableName}`;
  useEffect(() => { dialog.current?.showModal(); }, []);
  return <dialog ref={dialog} className="dash-copy-dialog card" aria-labelledby="copy-table-title"
    onCancel={e => { e.preventDefault(); if (!busy) onClose(); }}>
    <h3 id="copy-table-title">Copy changes</h3>
    <p><strong>{table.sourceOwner}.{table.sourceTable}</strong> → <strong>{targetName}/{table.targetSchema}.{table.targetTableName}</strong></p>
    <p>Apply new, changed and deleted rows since the last successful copy.</p>
    <form onSubmit={async e => {
      e.preventDefault();
      if (busy || phrase !== expected) return;
      setBusy(true); setError(null);
      try { const result = await copyTableChanges(table.id, phrase); onStarted(result.id); }
      catch (err) { setError(err instanceof Error ? err.message : 'Could not start copying changes.'); }
      finally { setBusy(false); }
    }}>
      <label htmlFor="copy-table-confirmation">Confirm the destination by typing <strong>{expected}</strong></label>
      <input id="copy-table-confirmation" autoFocus autoComplete="off" value={phrase}
        disabled={busy} onChange={e => setPhrase(e.target.value)} />
      {error && <p role="alert" className="dash-bad">{error}</p>}
      <div className="dash-row-actions">
        <button type="button" className="btn btn-secondary" disabled={busy} onClick={onClose}>Cancel</button>
        <button className="btn" type="submit" disabled={busy || phrase !== expected}>{busy ? 'Queuing…' : 'Copy changes'}</button>
      </div>
    </form>
  </dialog>;
}
