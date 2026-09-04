import { Activity, Database, ShieldCheck, Zap } from 'lucide-react';

export default function Dashboard() {
  const stats = [
    { label: 'Active Migrations', value: '3', icon: <Activity className="text-gradient" size={24} /> },
    { label: 'Total Data Migrated', value: '4.2 TB', icon: <Database className="text-gradient" size={24} /> },
    { current: true, label: 'Cluster Throughput', value: '850 MB/s', icon: <Zap className="text-gradient" size={24} /> },
    { label: 'Validations Passed', value: '99.9%', icon: <ShieldCheck className="text-gradient" size={24} /> },
  ];

  return (
    <div className="flex-col gap-6">
      <header>
        <h1>System Dashboard</h1>
        <p>Real-time overview of the O2P migration cluster.</p>
      </header>

      <div style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fit, minmax(240px, 1fr))', gap: '24px', marginTop: '16px' }}>
        {stats.map(s => (
          <div key={s.label} className="glass-panel flex-col gap-2" style={{ border: s.current ? '1px solid var(--accent-blue)' : undefined }}>
            <div className="flex-row justify-between items-center">
              <span style={{ color: 'var(--text-secondary)' }}>{s.label}</span>
              {s.icon}
            </div>
            <div style={{ fontSize: '2rem', fontWeight: 700, marginTop: '8px' }}>{s.value}</div>
          </div>
        ))}
      </div>

      <div className="glass-panel mt-8" style={{ minHeight: '300px' }}>
        <h3>Recent Activity</h3>
        <p style={{ marginTop: '16px' }}>No recent activity to display. Connect your source database to begin discovery.</p>
      </div>
    </div>
  );
}
