import { useState, useEffect } from 'react';
import { useParams, Link, useNavigate } from 'react-router-dom';
import {
  ArrowLeft,
  Trash2,
  RefreshCw,
  CheckCircle2,
  AlertCircle,
  ChevronDown,
  ChevronUp,
  Ban,
  RotateCcw,
  Pause,
  Play
} from 'lucide-react';
import { commandJob, deleteJob, fetchJob, fetchMetrics, fetchValidation, hasRole } from '../api';
import { commandLabel, statusColor, statusLabel } from '../labels';
import { useWorkerStatus } from '../useWorkerStatus';
import { WorkerBanner } from '../components/WorkerStatus';

export default function JobDetails() {
  const { id } = useParams();
  const jobId = Number(id);
  const worker = useWorkerStatus();
  const navigate = useNavigate();

  const [job, setJob] = useState<any>(null);
  const [metrics, setMetrics] = useState<any[]>([]);
  const [validations, setValidations] = useState<any[]>([]);
  const [loading, setLoading] = useState(true);
  const [fetchError, setFetchError] = useState<string | null>(null);
  const [expandedTableId, setExpandedTableId] = useState<number | null>(null);
  const [actionBusy, setActionBusy] = useState<string | null>(null);
  const [actionMsg, setActionMsg] = useState<string | null>(null);

  const canControl = hasRole('Admin') || hasRole('Operator');

  useEffect(() => {
    loadAll();
    const timer = setInterval(() => {
      pollProgress();
    }, 2000);
    return () => clearInterval(timer);
  }, [jobId]);

  const loadAll = async () => {
    try {
      const [jData, mData, vData] = await Promise.all([
        fetchJob(jobId),
        fetchMetrics(jobId).catch(() => []),
        fetchValidation(jobId).catch(() => [])
      ]);
      setJob(jData);
      setMetrics(mData);
      setValidations(vData);
      setFetchError(null);
    } catch (err: any) {
      console.error(err);
      setFetchError(err.message || 'Failed to load migration job details.');
    } finally {
      setLoading(false);
    }
  };

  const pollProgress = async () => {
    try {
      const [jData, mData, vData] = await Promise.all([
        fetchJob(jobId),
        fetchMetrics(jobId).catch(() => []),
        fetchValidation(jobId).catch(() => [])
      ]);
      setJob(jData);
      setMetrics(mData);
      setValidations(vData);
    } catch (err: any) {
      console.error('Polling error', err);
    }
  };

  const runCommand = async (command: string, confirmText?: string) => {
    if (confirmText && !window.confirm(confirmText)) return;
    setActionBusy(command);
    setActionMsg(null);
    try {
      await commandJob(jobId, command);
      setActionMsg(
        command === 'cancel'
          ? 'Cancelling — the run will stop shortly.'
          : command === 'retry_failed'
            ? 'Retrying — failed and cancelled work has been queued again.'
            : command === 'pause'
              ? 'Pausing — no new batches will be started.'
              : 'Resuming — the run is going again.'
      );
      await pollProgress();
    } catch (err: any) {
      setActionMsg(err.message || `Couldn't ${commandLabel(command)}.`);
    } finally {
      setActionBusy(null);
    }
  };

  const removeRun = async () => {
    if (!window.confirm(
      `Delete run #${jobId}?\n\nIts history (tables, batches, checks and events) is removed for good. ` +
      'Data already copied to the destination is not touched.'
    )) return;
    setActionBusy('delete');
    setActionMsg(null);
    try {
      await deleteJob(jobId);
      navigate('/jobs');
    } catch (err: any) {
      setActionMsg(err.message || "Couldn't delete the run.");
      setActionBusy(null);
    }
  };

  if (loading) {
    return <div className="card" style={{ textAlign: 'center', padding: '48px' }}><RefreshCw size={24} className="spin" /></div>;
  }

  if (fetchError || !job) {
    return (
      <div className="card" style={{ textAlign: 'center', padding: '48px', display: 'flex', flexDirection: 'column', gap: '16px', alignItems: 'center' }}>
        <h3 style={{ color: '#ef4444', margin: 0 }}>Could not load this run</h3>
        <p style={{ color: 'var(--text-secondary)', margin: 0 }}>{fetchError || 'Run not found.'}</p>
        <Link to="/jobs" className="btn btn-secondary" style={{ padding: '8px 16px', fontSize: '0.9rem' }}>
          Back to Runs
        </Link>
      </div>
    );
  }

  const latestMetric = metrics[metrics.length - 1];
  const totalRowsMigrated = job.tableRuns?.reduce((acc: number, t: any) => acc + (t.rowsMigrated || 0), 0) || 0;
  const totalBytesMigrated = job.tableRuns?.reduce((acc: number, t: any) => acc + (t.bytesMigrated || 0), 0) || 0;

  const status = job.status as string;
  const canCancel = ['Running', 'Queued', 'Paused'].includes(status);
  const canDelete = !canCancel;
  const canPause = status === 'Running';
  const canResume = status === 'Paused';
  const canRetry = ['Failed', 'Cancelled', 'CompletedWithErrors'].includes(status)
    || job.tableRuns?.some((t: any) => t.status === 'Failed' || t.status === 'CompletedWithErrors'
      || t.chunks?.some((c: any) => c.status === 'Failed' || c.status === 'Cancelled'));

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: '24px' }}>
      <div style={{ display: 'flex', gap: '16px', alignItems: 'center' }}>
        <Link to="/jobs" className="btn btn-secondary" style={{ padding: '8px' }}>
          <ArrowLeft size={18} />
        </Link>
        <div style={{ display: 'flex', flex: 1, justifyContent: 'space-between', alignItems: 'center', gap: '16px', flexWrap: 'wrap' }}>
          <div>
            <h1 className="text-gradient" style={{ margin: 0 }}>Run #{jobId}</h1>
            <p style={{ margin: '4px 0 0 0', color: 'var(--text-secondary)', fontSize: '0.9rem' }}>
              Migration: <strong>{job.application?.name}</strong> • Destination schema: <strong>{job.targetSchema}</strong>
            </p>
          </div>
          <div style={{ display: 'flex', gap: '10px', alignItems: 'center', flexWrap: 'wrap' }}>
            <span
              style={{
                padding: '6px 12px',
                borderRadius: '20px',
                fontSize: '0.8rem',
                fontWeight: 'bold',
                background: status === 'Running' ? 'rgba(59, 130, 246, 0.1)' : 'rgba(255,255,255,0.05)',
                color: statusColor(status)
              }}
            >
              {statusLabel(status)}
            </span>

            {canControl && canPause && (
              <button
                type="button"
                className="btn btn-secondary"
                disabled={!!actionBusy}
                onClick={() => runCommand('pause')}
                style={{ display: 'flex', alignItems: 'center', gap: '6px', fontSize: '0.85rem' }}
              >
                <Pause size={15} />
                {actionBusy === 'pause' ? '…' : 'Pause'}
              </button>
            )}
            {canControl && canResume && (
              <button
                type="button"
                className="btn btn-secondary"
                disabled={!!actionBusy}
                onClick={() => runCommand('resume')}
                style={{ display: 'flex', alignItems: 'center', gap: '6px', fontSize: '0.85rem' }}
              >
                <Play size={15} />
                {actionBusy === 'resume' ? '…' : 'Resume'}
              </button>
            )}
            {canControl && canRetry && (
              <button
                type="button"
                className="btn btn-secondary"
                disabled={!!actionBusy}
                onClick={() => runCommand(
                  'retry_failed',
                  'Retry the failed and cancelled tables in this run?'
                )}
                style={{ display: 'flex', alignItems: 'center', gap: '6px', fontSize: '0.85rem', color: '#60a5fa', borderColor: 'rgba(96,165,250,0.45)' }}
              >
                <RotateCcw size={15} />
                {actionBusy === 'retry_failed' ? '…' : 'Retry failed'}
              </button>
            )}
            {canControl && canCancel && (
              <button
                type="button"
                className="btn btn-secondary"
                disabled={!!actionBusy}
                onClick={() => runCommand(
                  'cancel',
                  'Cancel this run?\n\nBatches that are already running will stop taking new work. Destination tables are not deleted.'
                )}
                style={{ display: 'flex', alignItems: 'center', gap: '6px', fontSize: '0.85rem', color: '#f87171', borderColor: 'rgba(248,113,113,0.4)' }}
              >
                <Ban size={15} />
                {actionBusy === 'cancel' ? '…' : 'Cancel run'}
              </button>
            )}
            {canControl && canDelete && (
              <button
                type="button"
                className="btn btn-secondary"
                disabled={!!actionBusy}
                onClick={removeRun}
                title="Remove this run and its history"
                style={{ display: 'flex', alignItems: 'center', gap: '6px', fontSize: '0.85rem', color: '#f87171', borderColor: 'rgba(248,113,113,0.4)' }}
              >
                <Trash2 size={15} />
                {actionBusy === 'delete' ? '…' : 'Delete run'}
              </button>
            )}
          </div>
        </div>
      </div>

      <WorkerBanner status={worker} waitingRuns={['Queued', 'Running', 'Paused'].includes(status) ? 1 : 0} />

      {status === 'Failed' && (job.tableRuns?.length ?? 0) === 0 && (
        <div className="card" style={{ padding: '12px 16px', color: '#fca5a5', fontSize: '0.9rem' }}>
          No tables were selected for this run, so nothing was copied. Open the table selection, tick the tables you
          want, save it, and start a new run.
        </div>
      )}

      {actionMsg && (
        <div className="card" style={{ padding: '12px 16px', color: 'var(--text-secondary)', fontSize: '0.9rem' }}>
          {actionMsg}
        </div>
      )}

      <div style={{ display: 'grid', gridTemplateColumns: 'repeat(4, 1fr)', gap: '16px' }}>
        <div className="card" style={{ padding: '20px' }}>
          <span style={{ fontSize: '0.8rem', color: 'var(--text-secondary)', textTransform: 'uppercase' }}>Rows Copied</span>
          <h2 style={{ margin: '8px 0 0 0', fontSize: '1.8rem' }}>{totalRowsMigrated.toLocaleString()}</h2>
        </div>
        <div className="card" style={{ padding: '20px' }}>
          <span style={{ fontSize: '0.8rem', color: 'var(--text-secondary)', textTransform: 'uppercase' }}>Data Transferred</span>
          <h2 style={{ margin: '8px 0 0 0', fontSize: '1.8rem' }}>{(totalBytesMigrated / (1024 * 1024)).toFixed(2)} MB</h2>
        </div>
        <div className="card" style={{ padding: '20px' }}>
          <span style={{ fontSize: '0.8rem', color: 'var(--text-secondary)', textTransform: 'uppercase' }}>Current Speed</span>
          <h2 style={{ margin: '8px 0 0 0', fontSize: '1.8rem', color: '#60a5fa' }}>
            {latestMetric ? `${latestMetric.rowsPerSecond.toLocaleString()} rows/sec` : '0 rows/sec'}
          </h2>
        </div>
        <div className="card" style={{ padding: '20px' }}>
          <span style={{ fontSize: '0.8rem', color: 'var(--text-secondary)', textTransform: 'uppercase' }}>Batches Running</span>
          <h2 style={{ margin: '8px 0 0 0', fontSize: '1.8rem', color: '#34d399' }}>
            {latestMetric ? latestMetric.activeChunkWorkers : 0} / 16
          </h2>
        </div>
      </div>

      <div className="card">
        <h3 style={{ margin: '0 0 16px 0' }}>Tables in this run</h3>

        <div style={{ display: 'flex', flexDirection: 'column', gap: '16px' }}>
          {job.tableRuns?.map((t: any) => {
            const isExpanded = expandedTableId === t.id;
            const validation = validations.find(v => v.tableRunId === t.id);

            const totalChunks = t.chunks?.length || 0;
            const completedChunks = t.chunks?.filter((c: any) => c.status === 'Done').length || 0;
            const pctChunks = totalChunks > 0 ? Math.round((completedChunks / totalChunks) * 100) : 0;

            const tableStatusColor = statusColor(t.status);

            return (
              <div
                key={t.id}
                className="card"
                style={{
                  background: 'rgba(255, 255, 255, 0.01)',
                  border: '1px solid rgba(255, 255, 255, 0.04)',
                  padding: '16px',
                  display: 'flex',
                  flexDirection: 'column',
                  gap: '12px'
                }}
              >
                <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center' }}>
                  <div style={{ display: 'flex', gap: '16px', alignItems: 'center' }}>
                    <button
                      type="button"
                      style={{ background: 'none', border: 'none', color: 'var(--text-secondary)', cursor: 'pointer', display: 'flex' }}
                      onClick={() => setExpandedTableId(isExpanded ? null : t.id)}
                    >
                      {isExpanded ? <ChevronUp size={18} /> : <ChevronDown size={18} />}
                    </button>
                    <div>
                      <h4 style={{ margin: 0, fontSize: '1.05rem' }}>{t.manifestTable?.tableName}</h4>
                      <span style={{ fontSize: '0.75rem', color: 'var(--text-secondary)' }}>
                        Destination: <span style={{ fontFamily: 'monospace' }}>{t.targetTableName}</span>
                        {t.targetTablePreExisted != null && (
                          <span style={{ marginLeft: '8px', fontSize: '0.7rem', color: t.targetTablePreExisted ? '#60a5fa' : '#10b981' }}>
                            {t.targetTablePreExisted ? 'reused the existing table' : 'created the table'}
                          </span>
                        )}
                      </span>
                    </div>
                  </div>

                  <div style={{ display: 'flex', gap: '24px', alignItems: 'center' }}>
                    <div style={{ width: '120px' }}>
                      <div style={{ display: 'flex', justifyContent: 'space-between', fontSize: '0.75rem', marginBottom: '4px' }}>
                        <span>Batches</span>
                        <span>{pctChunks}%</span>
                      </div>
                      <div style={{ height: '6px', background: 'rgba(255,255,255,0.05)', borderRadius: '3px', overflow: 'hidden' }}>
                        <div style={{ width: `${pctChunks}%`, height: '100%', background: '#10b981' }} />
                      </div>
                    </div>

                    <div style={{ width: '100px', textAlign: 'right' }}>
                      <span style={{ fontSize: '0.85rem', color: tableStatusColor, fontWeight: 'bold' }}>
                        {statusLabel(t.status)}
                      </span>
                    </div>
                  </div>
                </div>

                {isExpanded && (
                  <div style={{ borderTop: '1px solid rgba(255,255,255,0.05)', paddingTop: '16px', marginTop: '4px', display: 'flex', flexDirection: 'column', gap: '16px' }}>
                    <div>
                      <span style={{ display: 'block', fontSize: '0.8rem', color: 'var(--text-secondary)', marginBottom: '8px' }}>
                        Batch progress ({completedChunks} of {totalChunks} batches done)
                      </span>
                      <div style={{ display: 'flex', flexWrap: 'wrap', gap: '4px' }}>
                        {t.chunks?.map((chunk: any) => {
                          const color = chunk.status === 'Pending'
                            ? 'rgba(255, 255, 255, 0.05)'
                            : statusColor(chunk.status);

                          return (
                            <div
                              key={chunk.id}
                              style={{
                                width: '14px',
                                height: '14px',
                                borderRadius: '3px',
                                background: color,
                                border: '1px solid rgba(255,255,255,0.02)'
                              }}
                              title={`Batch ${chunk.chunkIndex ?? chunk.id}: ${statusLabel(chunk.status)}${chunk.errorMessage ? ` — ${chunk.errorMessage}` : ''}`}
                            />
                          );
                        })}
                      </div>
                    </div>

                    <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: '24px' }}>
                      <div className="card" style={{ background: 'rgba(255,255,255,0.01)', border: '1px solid rgba(255,255,255,0.03)', padding: '12px' }}>
                        <span style={{ fontSize: '0.8rem', color: 'var(--text-secondary)', textTransform: 'uppercase' }}>Metrics</span>
                        <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: '8px', fontSize: '0.85rem', marginTop: '8px' }}>
                          <div>Rows copied: {t.rowsMigrated?.toLocaleString() || 0}</div>
                          <div>Data copied: {((t.bytesMigrated || 0) / 1024).toFixed(2)} KB</div>
                        </div>
                        {t.errorMessage && (
                          // pre-wrap matters: a target-schema mismatch lists one problem per line,
                          // and the default white-space would collapse it into one unreadable run.
                          <p style={{ margin: '8px 0 0', fontSize: '0.75rem', color: '#f87171', whiteSpace: 'pre-wrap' }}>{t.errorMessage}</p>
                        )}
                      </div>

                      <div className="card" style={{ background: 'rgba(255,255,255,0.01)', border: '1px solid rgba(255,255,255,0.03)', padding: '12px' }}>
                        <span style={{ fontSize: '0.8rem', color: 'var(--text-secondary)', textTransform: 'uppercase' }}>Row count check</span>
                        {validation ? (
                          <div style={{ display: 'flex', flexDirection: 'column', gap: '6px', fontSize: '0.85rem', marginTop: '8px' }}>
                            <div style={{ display: 'flex', alignItems: 'center', gap: '6px', color: validation.passed ? '#10b981' : '#f87171' }}>
                              {validation.passed ? <CheckCircle2 size={16} /> : <AlertCircle size={16} />}
                              <span>{validation.passed ? 'Row counts match' : 'Row counts do not match'}</span>
                            </div>
                            <div style={{ fontSize: '0.75rem', color: 'var(--text-secondary)', fontFamily: 'monospace' }}>
                              Source: {validation.sourceValue?.toLocaleString()} | Destination: {validation.targetValue?.toLocaleString()}
                            </div>
                          </div>
                        ) : (
                          <p style={{ fontSize: '0.8rem', color: 'var(--text-secondary)', margin: '8px 0 0 0', fontStyle: 'italic' }}>
                            The row count check runs once the data has finished copying.
                          </p>
                        )}
                      </div>
                    </div>
                  </div>
                )}
              </div>
            );
          })}
        </div>
      </div>
    </div>
  );
}
