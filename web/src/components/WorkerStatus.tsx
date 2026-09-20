import { AlertTriangle } from 'lucide-react';
import type { WorkerStatus } from '../api';

/** A small always-visible indicator: green when one copier is running, red when none, amber when several. */
export function WorkerChip({ status }: { status: WorkerStatus | null }) {
  if (!status) return null;

  const tone = !status.running
    ? { color: '#f87171', bg: 'rgba(248, 113, 113, 0.1)', label: 'Copier not running' }
    : status.multiple
      ? { color: '#fbbf24', bg: 'rgba(251, 191, 36, 0.1)', label: `${status.count} copiers running` }
      : { color: '#34d399', bg: 'rgba(52, 211, 153, 0.1)', label: 'Copier running' };

  return (
    <span
      title={
        status.workers.length > 0
          ? status.workers.map((w) => `${w.host} (pid ${w.processId})`).join('\n')
          : 'No copier has reported in recently.'
      }
      style={{
        display: 'inline-flex',
        alignItems: 'center',
        gap: '6px',
        padding: '4px 10px',
        borderRadius: '999px',
        fontSize: '0.75rem',
        fontWeight: 600,
        color: tone.color,
        background: tone.bg,
      }}
    >
      <span style={{ width: 7, height: 7, borderRadius: '50%', background: tone.color }} />
      {tone.label}
    </span>
  );
}

/**
 * The explanation for a run that just says "Waiting". Renders nothing unless something is actually
 * wrong AND it matters right now: no copier while runs are waiting, or more than one copier at once.
 *
 * @param waitingRuns how many runs are queued, running or paused - i.e. how many are affected.
 */
export function WorkerBanner({ status, waitingRuns }: { status: WorkerStatus | null; waitingRuns: number }) {
  if (!status) return null;

  if (!status.running && waitingRuns > 0) {
    return (
      <Banner tone="danger">
        <strong>The copier is not running</strong>, so {waitingRuns === 1 ? 'this run is' : `these ${waitingRuns} runs are`} waiting
        and will not start. Start the Worker program on the server, and they will begin on their own. You can still
        cancel a run that has not started.
      </Banner>
    );
  }

  if (status.multiple) {
    return (
      <Banner tone="warning">
        <strong>{status.count} copiers are running.</strong> Only one should. A second copier restarts the batches the
        first is copying, so stop the extra one
        {status.workers.length > 0 && ` (${status.workers.map((w) => `${w.host}, pid ${w.processId}`).join('; ')})`}.
      </Banner>
    );
  }

  return null;
}

function Banner({ tone, children }: { tone: 'danger' | 'warning'; children: React.ReactNode }) {
  const color = tone === 'danger' ? '#fecaca' : '#fde68a';
  const border = tone === 'danger' ? 'rgba(248, 113, 113, 0.35)' : 'rgba(251, 191, 36, 0.35)';
  const bg = tone === 'danger' ? 'rgba(248, 113, 113, 0.08)' : 'rgba(251, 191, 36, 0.08)';
  return (
    <div
      role="alert"
      style={{
        display: 'flex',
        gap: '12px',
        alignItems: 'flex-start',
        padding: '12px 16px',
        borderRadius: '10px',
        border: `1px solid ${border}`,
        background: bg,
        color,
        fontSize: '0.9rem',
        lineHeight: 1.5,
      }}
    >
      <AlertTriangle size={18} style={{ flex: 'none', marginTop: 2 }} />
      <div>{children}</div>
    </div>
  );
}
