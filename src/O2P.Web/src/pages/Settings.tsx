import { useState, useEffect } from 'react';
import { Save, CheckCircle2, Sliders, Shield, Activity } from 'lucide-react';

export default function Settings() {
  const [maxConcurrentTables, setMaxConcurrentTables] = useState(4);
  const [maxChunksPerTable, setMaxChunksPerTable] = useState(16);
  const [maxOracleSessions, setMaxOracleSessions] = useState(8);
  
  const [metricsInterval, setMetricsInterval] = useState(2);
  const [retentionDays, setRetentionDays] = useState(7);
  
  const [enableCountVerify, setEnableCountVerify] = useState(true);
  const [sampleHashLimit, setSampleHashLimit] = useState(100000);

  const [saved, setSaved] = useState(false);

  useEffect(() => {
    // Load config from localStorage
    const cfgStr = localStorage.getItem('o2p_global_config');
    if (cfgStr) {
      try {
        const cfg = JSON.parse(cfgStr);
        setMaxConcurrentTables(cfg.maxConcurrentTables || 4);
        setMaxChunksPerTable(cfg.maxChunksPerTable || 16);
        setMaxOracleSessions(cfg.maxOracleSessions || 8);
        setMetricsInterval(cfg.metricsInterval || 2);
        setRetentionDays(cfg.retentionDays || 7);
        setEnableCountVerify(cfg.enableCountVerify !== false);
        setSampleHashLimit(cfg.sampleHashLimit || 100000);
      } catch (e) {
        console.error(e);
      }
    }
  }, []);

  const handleSave = (e: React.FormEvent) => {
    e.preventDefault();
    const config = {
      maxConcurrentTables,
      maxChunksPerTable,
      maxOracleSessions,
      metricsInterval,
      retentionDays,
      enableCountVerify,
      sampleHashLimit
    };
    localStorage.setItem('o2p_global_config', JSON.stringify(config));
    setSaved(true);
    setTimeout(() => setSaved(false), 3000);
  };

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: '24px' }}>
      <div>
        <h1 className="text-gradient" style={{ margin: 0 }}>Global Settings</h1>
        <p style={{ margin: '4px 0 0 0', color: 'var(--text-secondary)', fontSize: '0.9rem' }}>
          Configure orchestrator throttling limits, telemetry sampling, and post-migration validation rules.
        </p>
      </div>

      <form onSubmit={handleSave} style={{ display: 'flex', flexDirection: 'column', gap: '24px' }}>
        {saved && (
          <div className="card" style={{ border: '1px solid rgba(16, 185, 129, 0.2)', background: 'rgba(16, 185, 129, 0.05)', display: 'flex', alignItems: 'center', gap: '10px', color: '#34d399', padding: '16px' }}>
            <CheckCircle2 size={18} />
            <span>Settings saved successfully! Parameters updated in the orchestrator profile.</span>
          </div>
        )}

        <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: '24px' }}>
          {/* Concurrency Card */}
          <div className="card">
            <h3 style={{ margin: '0 0 20px 0', display: 'flex', alignItems: 'center', gap: '8px' }}>
              <Sliders size={18} style={{ color: '#3b82f6' }} />
              <span>Concurrency & Throttling</span>
            </h3>

            <div style={{ display: 'flex', flexDirection: 'column', gap: '16px' }}>
              <div>
                <label className="label">Max Concurrent Tables</label>
                <input
                  className="input"
                  type="number"
                  min="1"
                  max="16"
                  value={maxConcurrentTables}
                  onChange={e => setMaxConcurrentTables(Number(e.target.value))}
                />
                <span style={{ fontSize: '0.75rem', color: 'var(--text-secondary)' }}>
                  Number of tables processed in parallel per worker instance.
                </span>
              </div>

              <div>
                <label className="label">Max Chunk Workers Per Table</label>
                <input
                  className="input"
                  type="number"
                  min="2"
                  max="64"
                  value={maxChunksPerTable}
                  onChange={e => setMaxChunksPerTable(Number(e.target.value))}
                />
                <span style={{ fontSize: '0.75rem', color: 'var(--text-secondary)' }}>
                  Maximum parallel execution threads allocated per individual table copy.
                </span>
              </div>

              <div>
                <label className="label">Global Oracle Active Session Ceiling</label>
                <input
                  className="input"
                  type="number"
                  min="4"
                  max="128"
                  value={maxOracleSessions}
                  onChange={e => setMaxOracleSessions(Number(e.target.value))}
                />
                <span style={{ fontSize: '0.75rem', color: 'var(--text-secondary)' }}>
                  Limits total concurrent sessions established on the source Oracle DBMS.
                </span>
              </div>
            </div>
          </div>

          {/* Validation & Telemetry Card */}
          <div style={{ display: 'flex', flexDirection: 'column', gap: '24px' }}>
            <div className="card">
              <h3 style={{ margin: '0 0 20px 0', display: 'flex', alignItems: 'center', gap: '8px' }}>
                <Shield size={18} style={{ color: '#8b5cf6' }} />
                <span>Verification & Validation</span>
              </h3>

              <div style={{ display: 'flex', flexDirection: 'column', gap: '16px' }}>
                <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center' }}>
                  <div>
                    <label className="label" style={{ margin: 0 }}>Automated Count Verification</label>
                    <span style={{ fontSize: '0.75rem', color: 'var(--text-secondary)' }}>
                      Enforce count audits against source and targets upon load completion.
                    </span>
                  </div>
                  <input
                    type="checkbox"
                    checked={enableCountVerify}
                    onChange={e => setEnableCountVerify(e.target.checked)}
                    style={{ width: '20px', height: '20px', cursor: 'pointer' }}
                  />
                </div>

                <div>
                  <label className="label">Sampled Row-Hash Threshold</label>
                  <input
                    className="input"
                    type="number"
                    step="10000"
                    value={sampleHashLimit}
                    onChange={e => setSampleHashLimit(Number(e.target.value))}
                  />
                  <span style={{ fontSize: '0.75rem', color: 'var(--text-secondary)' }}>
                    Row count ceiling above which bisection hash verification is triggered.
                  </span>
                </div>
              </div>
            </div>

            <div className="card">
              <h3 style={{ margin: '0 0 20px 0', display: 'flex', alignItems: 'center', gap: '8px' }}>
                <Activity size={18} style={{ color: '#10b981' }} />
                <span>Telemetry & Storage</span>
              </h3>

              <div style={{ display: 'flex', flexDirection: 'column', gap: '16px' }}>
                <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: '12px' }}>
                  <div>
                    <label className="label">Sampling Interval (sec)</label>
                    <input
                      className="input"
                      type="number"
                      min="1"
                      max="10"
                      value={metricsInterval}
                      onChange={e => setMetricsInterval(Number(e.target.value))}
                    />
                  </div>
                  <div>
                    <label className="label">Retention period (days)</label>
                    <input
                      className="input"
                      type="number"
                      min="1"
                      max="30"
                      value={retentionDays}
                      onChange={e => setRetentionDays(Number(e.target.value))}
                    />
                  </div>
                </div>
              </div>
            </div>
          </div>
        </div>

        <button className="btn" type="submit" style={{ alignSelf: 'flex-end' }}>
          <Save size={18} />
          <span>Save Configuration</span>
        </button>
      </form>
    </div>
  );
}
