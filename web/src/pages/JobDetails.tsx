import { useState, useEffect } from 'react';
import { useParams, Link } from 'react-router-dom';
import {
  ArrowLeft,
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
import { commandJob, fetchJob, fetchMetrics, fetchValidation, hasRole } from '../api';

export default function JobDetails() {
  const { id } = useParams();
  const jobId = Number(id);

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
          ? 'Cancel requested — worker will stop this job shortly.'
          : command === 'retry_failed'
            ? 'Retry requested — failed/cancelled work will be re-queued.'
            : command === 'pause'
              ? 'Pause requested — no new chunks will be claimed.'
              : 'Resume requested — job set back to Running.'
      );
      await pollProgress();
    } catch (err: any) {
      setActionMsg(err.message || `Failed to ${command}`);
    } finally {
      setActionBusy(null);
    }
  };

  if (loading) {
    return <div className="card" style={{ textAlign: 'center', padding: '48px' }}><RefreshCw size={24} className="spin" /></div>;
  }

  if (fetchError || !job) {
    return (
      <div className="card" style={{ textAlign: 'center', padding: '48px', display: 'flex', flexDirection: 'column', gap: '16px', alignItems: 'center' }}>
        <h3 style={{ color: '#ef4444', margin: 0 }}>Error Loading Job</h3>
        <p style={{ color: 'var(--text-secondary)', margin: 0 }}>{fetchError || 'Job run not found.'}</p>
        <Link to="/jobs" className="btn btn-secondary" style={{ padding: '8px 16px', fontSize: '0.9rem' }}>
          Back to Job Runs
        </Link>
      </div>
    );
  }

  const latestMetric = metrics[metrics.length - 1];
  const totalRowsMigrated = job.tableRuns?.reduce((acc: number, t: any) => acc + (t.rowsMigrated || 0), 0) || 0;
  const totalBytesMigrated = job.tableRuns?.reduce((acc: number, t: any) => acc + (t.bytesMigrated || 0), 0) || 0;

  const status = job.status as string;
  const canCancel = ['Running', 'Queued', 'Paused'].includes(status);
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
            <h1 className="text-gradient" style={{ margin: 0 }}>Job Execution #{jobId}</h1>
            <p style={{ margin: '4px 0 0 0', color: 'var(--text-secondary)', fontSize: '0.9rem' }}>
              App: <strong>{job.application?.name}</strong> • Schema: <strong>{job.targetSchema}</strong>
            </p>
          </div>
          <div style={{ display: 'flex', gap: '10px', alignItems: 'center', flexWrap: 'wrap' }}>
            <span
              style={{
                padding: '6px 12px',
                borderRadius: '20px',
                fontSize: '0.8rem',
                fontWeight: 'bold',
                textTransform: 'uppercase',
                background: status === 'Running' ? 'rgba(59, 130, 246, 0.1)' : 'rgba(255,255,255,0.05)',
                color: status === 'Running' ? '#60a5fa' : 'var(--text-secondary)'
              }}
            >
              {job.status}
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
                  'Retry failed/cancelled tables and chunks for this job?'
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
                  'Cancel this job?\n\nIn-flight chunks will stop claiming new work. Target tables are not dropped.'
                )}
                style={{ display: 'flex', alignItems: 'center', gap: '6px', fontSize: '0.85rem', color: '#f87171', borderColor: 'rgba(248,113,113,0.4)' }}
              >
                <Ban size={15} />
                {actionBusy === 'cancel' ? '…' : 'Cancel job'}
              </button>
            )}
          </div>
        </div>
      </div>

      {actionMsg && (
        <div className="card" style={{ padding: '12px 16px', color: 'var(--text-secondary)', fontSize: '0.9rem' }}>
          {actionMsg}
        </div>
      )}

      <div style={{ display: 'grid', gridTemplateColumns: 'repeat(4, 1fr)', gap: '16px' }}>
        <div className="card" style={{ padding: '20px' }}>
          <span style={{ fontSize: '0.8rem', color: 'var(--text-secondary)', textTransform: 'uppercase' }}>Rows Migrated</span>
          <h2 style={{ margin: '8px 0 0 0', fontSize: '1.8rem' }}>{totalRowsMigrated.toLocaleString()}</h2>
        </div>
        <div className="card" style={{ padding: '20px' }}>
          <span style={{ fontSize: '0.8rem', color: 'var(--text-secondary)', textTransform: 'uppercase' }}>Data Transferred</span>
          <h2 style={{ margin: '8px 0 0 0', fontSize: '1.8rem' }}>{(totalBytesMigrated / (1024 * 1024)).toFixed(2)} MB</h2>
        </div>
        <div className="card" style={{ padding: '20px' }}>
          <span style={{ fontSize: '0.8rem', color: 'var(--text-secondary)', textTransform: 'uppercase' }}>Active Rate</span>
          <h2 style={{ margin: '8px 0 0 0', fontSize: '1.8rem', color: '#60a5fa' }}>
            {latestMetric ? `${latestMetric.rowsPerSecond.toLocaleString()} R/s` : '0 R/s'}
          </h2>
        </div>
        <div className="card" style={{ padding: '20px' }}>
          <span style={{ fontSize: '0.8rem', color: 'var(--text-secondary)', textTransform: 'uppercase' }}>Active Workers</span>
          <h2 style={{ margin: '8px 0 0 0', fontSize: '1.8rem', color: '#34d399' }}>
            {latestMetric ? latestMetric.activeChunkWorkers : 0} / 16
          </h2>
        </div>
      </div>

      <div className="card">
        <h3 style={{ margin: '0 0 16px 0' }}>Table-wise Migration Progress</h3>

        <div style={{ display: 'flex', flexDirection: 'column', gap: '16px' }}>
          {job.tableRuns?.map((t: any) => {
            const isExpanded = expandedTableId === t.id;
            const validation = validations.find(v => v.tableRunId === t.id);

            const totalChunks = t.chunks?.length || 0;
            const completedChunks = t.chunks?.filter((c: any) => c.status === 'Done').length || 0;
            const pctChunks = totalChunks > 0 ? Math.round((completedChunks / totalChunks) * 100) : 0;

            let statusColor = 'var(--text-secondary)';
            if (t.status === 'Loading') statusColor = '#3b82f6';
            if (t.status === 'Validating') statusColor = '#fbbf24';
            if (t.status === 'Completed') statusColor = '#10b981';
            if (t.status === 'CompletedWithErrors') statusColor = '#f59e0b';
            if (t.status === 'Failed' || t.status === 'Cancelled') statusColor = '#ef4444';

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
                        Target: <span style={{ fontFamily: 'monospace' }}>{t.targetTableName}</span>
                      </span>
                    </div>
                  </div>

                  <div style={{ display: 'flex', gap: '24px', alignItems: 'center' }}>
                    <div style={{ width: '120px' }}>
                      <div style={{ display: 'flex', justifyContent: 'space-between', fontSize: '0.75rem', marginBottom: '4px' }}>
                        <span>Chunks</span>
                        <span>{pctChunks}%</span>
                      </div>
                      <div style={{ height: '6px', background: 'rgba(255,255,255,0.05)', borderRadius: '3px', overflow: 'hidden' }}>
                        <div style={{ width: `${pctChunks}%`, height: '100%', background: '#10b981' }} />
                      </div>
                    </div>

                    <div style={{ width: '100px', textAlign: 'right' }}>
                      <span style={{ fontSize: '0.85rem', color: statusColor, fontWeight: 'bold' }}>
                        {t.status}
                      </span>
                    </div>
                  </div>
                </div>

                {isExpanded && (
                  <div style={{ borderTop: '1px solid rgba(255,255,255,0.05)', paddingTop: '16px', marginTop: '4px', display: 'flex', flexDirection: 'column', gap: '16px' }}>
                    <div>
                      <span style={{ display: 'block', fontSize: '0.8rem', color: 'var(--text-secondary)', marginBottom: '8px' }}>
                        Chunk Heatmap ({completedChunks} / {totalChunks} chunks done)
                      </span>
                      <div style={{ display: 'flex', flexWrap: 'wrap', gap: '4px' }}>
                        {t.chunks?.map((chunk: any) => {
                          let color = 'rgba(255, 255, 255, 0.05)';
                          if (chunk.status === 'Running') color = '#3b82f6';
                          if (chunk.status === 'Done') color = '#10b981';
                          if (chunk.status === 'Failed' || chunk.status === 'Cancelled') color = '#ef4444';

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
                              title={`Chunk ID ${chunk.id}: ${chunk.status} ${chunk.errorMessage || ''}`}
                            />
                          );
                        })}
                      </div>
                    </div>

                    <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: '24px' }}>
                      <div className="card" style={{ background: 'rgba(255,255,255,0.01)', border: '1px solid rgba(255,255,255,0.03)', padding: '12px' }}>
                        <span style={{ fontSize: '0.8rem', color: 'var(--text-secondary)', textTransform: 'uppercase' }}>Metrics</span>
                        <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: '8px', fontSize: '0.85rem', marginTop: '8px' }}>
                          <div>Rows Load: {t.rowsMigrated?.toLocaleString() || 0}</div>
                          <div>Data Load: {((t.bytesMigrated || 0) / 1024).toFixed(2)} KB</div>
                        </div>
                        {t.errorMessage && (
                          <p style={{ margin: '8px 0 0', fontSize: '0.75rem', color: '#f87171' }}>{t.errorMessage}</p>
                        )}
                      </div>

                      <div className="card" style={{ background: 'rgba(255,255,255,0.01)', border: '1px solid rgba(255,255,255,0.03)', padding: '12px' }}>
                        <span style={{ fontSize: '0.8rem', color: 'var(--text-secondary)', textTransform: 'uppercase' }}>Post-Migration Verification</span>
                        {validation ? (
                          <div style={{ display: 'flex', flexDirection: 'column', gap: '6px', fontSize: '0.85rem', marginTop: '8px' }}>
                            <div style={{ display: 'flex', alignItems: 'center', gap: '6px', color: validation.passed ? '#10b981' : '#f87171' }}>
                              {validation.passed ? <CheckCircle2 size={16} /> : <AlertCircle size={16} />}
                              <span>{validation.passed ? 'Count verification passed' : 'Count mismatch or validation failed'}</span>
                            </div>
                            <div style={{ fontSize: '0.75rem', color: 'var(--text-secondary)', fontFamily: 'monospace' }}>
                              Oracle Src: {validation.sourceValue?.toLocaleString()} | PG Target: {validation.targetValue?.toLocaleString()}
                            </div>
                          </div>
                        ) : (
                          <p style={{ fontSize: '0.8rem', color: 'var(--text-secondary)', margin: '8px 0 0 0', fontStyle: 'italic' }}>
                            Validation checks pending data load completion.
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
