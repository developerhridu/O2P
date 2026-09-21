import { useState, useEffect } from 'react';
import { Link } from 'react-router-dom';
import { Activity, RefreshCw, Power, Ban, RotateCcw, Trash2 } from 'lucide-react';
import { fetchJobs, cancelAllAndRestartWorker, commandJob, deleteJob, hasRole } from '../api';
import { commandLabel, runKindLabel, statusColor, statusLabel } from '../labels';
import { useWorkerStatus } from '../useWorkerStatus';
import { WorkerBanner, WorkerChip } from '../components/WorkerStatus';
import '../components/ChangeTracking.css';

export default function JobRuns() {
  const [jobs, setJobs] = useState<any[]>([]);
  const [loading, setLoading] = useState(true);
  const [resetting, setResetting] = useState(false);
  const [busyJobId, setBusyJobId] = useState<number | null>(null);

  const canControl = hasRole('Admin') || hasRole('Operator');
  const worker = useWorkerStatus();

  useEffect(() => {
    loadJobs();
    const timer = setInterval(() => { loadJobs(); }, 5000);
    return () => clearInterval(timer);
  }, []);

  const loadJobs = async () => {
    try {
      const data = await fetchJobs();
      setJobs(data);
    } catch (err: any) {
      console.error('Could not load runs', err);
    } finally {
      setLoading(false);
    }
  };

  const handleCancelAllAndRestart = async () => {
    if (!window.confirm(
      'Cancel every waiting, running and paused run, and restart the copier?\n\n' +
      'Batches that are already running will be stopped. Destination tables are NOT deleted. This cannot be undone.'
    )) return;

    setResetting(true);
    try {
      const r = await cancelAllAndRestartWorker();
      alert(`Cancelled ${r.cancelledJobs} run(s), ${r.cancelledTables} table(s), ${r.cancelledChunks} batch(es).\nThe copier is restarting.`);
      await loadJobs();
    } catch (err: any) {
      alert(err.message || "Couldn't cancel the runs or restart the copier.");
    } finally {
      setResetting(false);
    }
  };

  const runJobCommand = async (jobId: number, command: string, confirmText: string) => {
    if (!window.confirm(confirmText)) return;
    setBusyJobId(jobId);
    try {
      await commandJob(jobId, command);
      await loadJobs();
    } catch (err: any) {
      alert(err.message || `Couldn't ${commandLabel(command)}.`);
    } finally {
      setBusyJobId(null);
    }
  };

  const removeRun = async (jobId: number) => {
    if (!window.confirm(
      `Delete run #${jobId}?\n\nIts history (tables, batches, checks and events) is removed for good. ` +
      'Data already copied to the destination is not touched.'
    )) return;
    setBusyJobId(jobId);
    try {
      await deleteJob(jobId);
      await loadJobs();
    } catch (err: any) {
      alert(err.message || "Couldn't delete the run.");
    } finally {
      setBusyJobId(null);
    }
  };

  if (loading) {
    return <div className="card" style={{ textAlign: 'center', padding: '48px' }}><RefreshCw size={24} className="spin" /></div>;
  }

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: '24px' }}>
      <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'flex-start', gap: '16px' }}>
        <div>
          <h1 className="text-gradient" style={{ margin: 0 }}>Runs</h1>
          <p style={{ margin: '4px 0 0 0', color: 'var(--text-secondary)', fontSize: '0.9rem' }}>
            Watch runs in progress and review past ones.
          </p>
          <div style={{ marginTop: '10px' }}><WorkerChip status={worker} /></div>
        </div>
        {hasRole('Admin') && (
          <button
            className="btn btn-secondary"
            onClick={handleCancelAllAndRestart}
            disabled={resetting}
            title="Cancel every waiting, running and paused run, and restart the copier"
            style={{ display: 'flex', alignItems: 'center', gap: '8px', whiteSpace: 'nowrap', color: '#f87171', borderColor: 'rgba(248,113,113,0.4)' }}
          >
            <Power size={16} />
            {resetting ? 'Working…' : 'Cancel All & Restart Copier'}
          </button>
        )}
      </div>

      <WorkerBanner
        status={worker}
        waitingRuns={jobs.filter((j) => ['Queued', 'Running', 'Paused'].includes(j.status)).length}
      />

      <div className="card">
        <h3 style={{ margin: '0 0 16px 0' }}>All runs</h3>

        <div style={{ display: 'flex', flexDirection: 'column', gap: '12px' }}>
          {jobs.map(job => {
            const canCancel = ['Running', 'Queued', 'Paused'].includes(job.status);
            // A change copy is never retried the bulk way (that would empty its tables); the next
            // Copy changes simply picks up where the last good one left off.
            const canRetry = job.kind !== 'changes' && ['Failed', 'Cancelled', 'CompletedWithErrors'].includes(job.status);
            const busy = busyJobId === job.id;

            return (
              <div
                key={job.id}
                className="card"
                style={{
                  background: 'rgba(255, 255, 255, 0.02)',
                  border: '1px solid rgba(255, 255, 255, 0.05)',
                  display: 'flex',
                  justifyContent: 'space-between',
                  alignItems: 'center',
                  padding: '16px',
                  gap: '12px',
                  flexWrap: 'wrap'
                }}
              >
                <div style={{ display: 'flex', gap: '16px', alignItems: 'center' }}>
                  <div
                    style={{
                      padding: '10px',
                      borderRadius: '8px',
                      background: job.status === 'Running' ? 'rgba(59, 130, 246, 0.1)' : 'rgba(255, 255, 255, 0.05)',
                      color: job.status === 'Running' ? '#60a5fa' : 'var(--text-secondary)',
                      display: 'flex',
                      alignItems: 'center'
                    }}
                  >
                    <Activity size={20} />
                  </div>
                  <div>
                    <h4 style={{ margin: 0 }}>
                      Run #{job.id}{' '}
                      <span className="run-kind" data-kind={job.kind}>{runKindLabel(job.kind)}</span>
                    </h4>
                    <span style={{ fontSize: '0.8rem', color: 'var(--text-secondary)' }}>
                      Migration: {job.application?.name} • Destination schema: {job.targetSchema}
                    </span>
                  </div>
                </div>

                <div style={{ display: 'flex', gap: '10px', alignItems: 'center', flexWrap: 'wrap' }}>
                  <span
                    style={{
                      padding: '4px 10px',
                      borderRadius: '12px',
                      fontSize: '0.75rem',
                      fontWeight: 'bold',
                      background: job.status === 'Running' ? 'rgba(59, 130, 246, 0.1)' : 'rgba(255, 255, 255, 0.05)',
                      color: statusColor(job.status)
                    }}
                  >
                    {statusLabel(job.status)}
                  </span>

                  {canControl && canRetry && (
                    <button
                      type="button"
                      className="btn btn-secondary"
                      disabled={busy}
                      onClick={() => runJobCommand(
                        job.id,
                        'retry_failed',
                        `Retry the failed and cancelled tables in run #${job.id}?`
                      )}
                      style={{ padding: '6px 10px', fontSize: '0.8rem', display: 'flex', alignItems: 'center', gap: '4px', color: '#60a5fa', borderColor: 'rgba(96,165,250,0.45)' }}
                    >
                      <RotateCcw size={14} />
                      Retry
                    </button>
                  )}

                  {canControl && canCancel && (
                    <button
                      type="button"
                      className="btn btn-secondary"
                      disabled={busy}
                      onClick={() => runJobCommand(
                        job.id,
                        'cancel',
                        `Cancel run #${job.id}? Destination tables are not deleted.`
                      )}
                      style={{ padding: '6px 10px', fontSize: '0.8rem', display: 'flex', alignItems: 'center', gap: '4px', color: '#f87171', borderColor: 'rgba(248,113,113,0.4)' }}
                    >
                      <Ban size={14} />
                      Cancel run
                    </button>
                  )}

                  <Link to={`/jobs/${job.id}`} className="btn btn-secondary" style={{ padding: '6px 12px', fontSize: '0.85rem' }}>
                    View details
                  </Link>

                  {canControl && !canCancel && (
                    <button
                      type="button"
                      className="btn btn-secondary"
                      disabled={busy}
                      onClick={() => removeRun(job.id)}
                      title="Remove this run and its history"
                      style={{ padding: '6px 10px', fontSize: '0.8rem', display: 'flex', alignItems: 'center', gap: '4px', color: '#f87171', borderColor: 'rgba(248,113,113,0.4)' }}
                    >
                      <Trash2 size={14} />
                      Delete
                    </button>
                  )}
                </div>
              </div>
            );
          })}

          {jobs.length === 0 && (
            <div style={{ textAlign: 'center', color: 'var(--text-secondary)', padding: '32px 0' }}>
              Nothing has run yet. Open a migration to start one.
            </div>
          )}
        </div>
      </div>
    </div>
  );
}
