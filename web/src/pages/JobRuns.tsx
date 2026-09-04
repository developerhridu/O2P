import { useState, useEffect } from 'react';
import { Link } from 'react-router-dom';
import { Activity, RefreshCw, Power } from 'lucide-react';
import { fetchJobs, cancelAllAndRestartWorker, hasRole } from '../api';

export default function JobRuns() {
  const [jobs, setJobs] = useState<any[]>([]);
  const [loading, setLoading] = useState(true);
  const [resetting, setResetting] = useState(false);

  useEffect(() => {
    loadJobs();
  }, []);

  const loadJobs = async () => {
    try {
      const data = await fetchJobs();
      setJobs(data);
    } catch (err: any) {
      console.error('Failed to load jobs', err);
    } finally {
      setLoading(false);
    }
  };

  const handleCancelAllAndRestart = async () => {
    if (!window.confirm(
      'Cancel ALL running / queued / paused jobs and restart the worker?\n\n' +
      'In-flight chunks will be stopped. Target tables are NOT dropped. This cannot be undone.'
    )) return;

    setResetting(true);
    try {
      const r = await cancelAllAndRestartWorker();
      alert(`Cancelled ${r.cancelledJobs} job(s), ${r.cancelledTables} table(s), ${r.cancelledChunks} chunk(s).\nWorker restart requested.`);
      await loadJobs();
    } catch (err: any) {
      alert(err.message || 'Failed to cancel jobs / restart worker');
    } finally {
      setResetting(false);
    }
  };

  if (loading) {
    return <div className="card" style={{ textAlign: 'center', padding: '48px' }}><RefreshCw size={24} className="spin" /></div>;
  }

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: '24px' }}>
      <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'flex-start', gap: '16px' }}>
        <div>
          <h1 className="text-gradient" style={{ margin: 0 }}>Job Runs</h1>
          <p style={{ margin: '4px 0 0 0', color: 'var(--text-secondary)', fontSize: '0.9rem' }}>
            Monitor active and historical migration executions.
          </p>
        </div>
        {hasRole('Admin') && (
          <button
            className="btn btn-secondary"
            onClick={handleCancelAllAndRestart}
            disabled={resetting}
            title="Cancel every running/queued/paused job and restart the worker"
            style={{ display: 'flex', alignItems: 'center', gap: '8px', whiteSpace: 'nowrap', color: '#f87171', borderColor: 'rgba(248,113,113,0.4)' }}
          >
            <Power size={16} />
            {resetting ? 'Working…' : 'Cancel All & Restart Worker'}
          </button>
        )}
      </div>

      <div className="card">
        <h3 style={{ margin: '0 0 16px 0' }}>Migration Runs</h3>
        
        <div style={{ display: 'flex', flexDirection: 'column', gap: '12px' }}>
          {jobs.map(job => (
            <div
              key={job.id}
              className="card"
              style={{
                background: 'rgba(255, 255, 255, 0.02)',
                border: '1px solid rgba(255, 255, 255, 0.05)',
                display: 'flex',
                justifyContent: 'space-between',
                alignItems: 'center',
                padding: '16px'
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
                  <h4 style={{ margin: 0 }}>Run #{job.id}</h4>
                  <span style={{ fontSize: '0.8rem', color: 'var(--text-secondary)' }}>
                    App: {job.application?.name} • Target Schema: {job.targetSchema}
                  </span>
                </div>
              </div>

              <div style={{ display: 'flex', gap: '24px', alignItems: 'center' }}>
                <div style={{ textAlign: 'right' }}>
                  <span
                    style={{
                      padding: '4px 10px',
                      borderRadius: '12px',
                      fontSize: '0.75rem',
                      fontWeight: 'bold',
                      background: job.status === 'Running' ? 'rgba(59, 130, 246, 0.1)' : 'rgba(255, 255, 255, 0.05)',
                      color: job.status === 'Running' ? '#60a5fa' : 'var(--text-secondary)'
                    }}
                  >
                    {job.status}
                  </span>
                </div>
                <Link to={`/jobs/${job.id}`} className="btn btn-secondary" style={{ padding: '6px 12px', fontSize: '0.85rem' }}>
                  View Details
                </Link>
              </div>
            </div>
          ))}

          {jobs.length === 0 && (
            <div style={{ textAlign: 'center', color: 'var(--text-secondary)', padding: '32px 0' }}>
              No migration jobs have been executed yet. Go to your Application to launch one.
            </div>
          )}
        </div>
      </div>
    </div>
  );
}
