import { useState, useEffect } from 'react';
import { useParams, useNavigate } from 'react-router-dom';
import { ArrowLeft, Save, Table2, Sliders, CheckSquare, Square, RefreshCw } from 'lucide-react';
import { fetchManifest, updateManifestTables } from '../api';

export default function ManifestBuilder() {
  const { appId, manifestId } = useParams();
  const navigate = useNavigate();

  const [manifest, setManifest] = useState<any>(null);
  const [tables, setTables] = useState<any[]>([]);
  const [loading, setLoading] = useState(true);
  const [saving, setSaving] = useState(false);

  // Column Excludes drawer/modal state
  const [activeTableIdx, setActiveTableIdx] = useState<number | null>(null);

  useEffect(() => {
    loadManifest();
  }, [manifestId]);

  const loadManifest = async () => {
    setLoading(true);
    try {
      const data = await fetchManifest(Number(manifestId));
      setManifest(data);
      setTables(data.tables || []);
    } catch (err: any) {
      console.error(err);
    } finally {
      setLoading(false);
    }
  };

  const handleToggleInclude = (index: number) => {
    const updated = [...tables];
    updated[index].included = !updated[index].included;
    setTables(updated);
  };

  const handleWhereClauseChange = (index: number, val: string) => {
    const updated = [...tables];
    updated[index].whereClause = val;
    setTables(updated);
  };

  const handleToggleColumnExclude = (colName: string) => {
    if (activeTableIdx === null) return;
    const updated = [...tables];
    const table = updated[activeTableIdx];
    
    // Column exclusion toggle logic
    table.columns = table.columns.map((c: any) => {
      if (c.columnName === colName) {
        return { ...c, isExcluded: !c.isExcluded };
      }
      return c;
    });

    // Update excludedColumns serialized/comma-separated string snapshot
    const excludedList = table.columns
      .filter((c: any) => c.isExcluded)
      .map((c: any) => c.columnName);
    table.excludedColumns = excludedList.join(',');

    setTables(updated);
  };

  const handleSave = async () => {
    setSaving(true);
    try {
      await updateManifestTables(Number(manifestId), tables);
      navigate(`/applications/${appId}`);
    } catch (err: any) {
      alert(err.message || 'Failed to save manifest updates.');
    } finally {
      setSaving(false);
    }
  };

  if (loading || !manifest) {
    return <div className="card" style={{ textAlign: 'center', padding: '48px' }}><RefreshCw size={24} className="spin" /></div>;
  }

  const activeTable = activeTableIdx !== null ? tables[activeTableIdx] : null;

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: '24px' }}>
      {/* Header */}
      <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center' }}>
        <div style={{ display: 'flex', gap: '16px', alignItems: 'center' }}>
          <button className="btn btn-secondary" style={{ padding: '8px' }} onClick={() => navigate(`/applications/${appId}`)}>
            <ArrowLeft size={18} />
          </button>
          <div>
            <h1 className="text-gradient" style={{ margin: 0 }}>Manifest Builder</h1>
            <p style={{ margin: '4px 0 0 0', color: 'var(--text-secondary)', fontSize: '0.9rem' }}>
              Version {manifest.version} • Select tables, add filter clauses, and exclude specific columns.
            </p>
          </div>
        </div>

        <button className="btn" onClick={handleSave} disabled={saving} style={{ background: '#3b82f6', color: 'white' }}>
          {saving ? <RefreshCw size={18} className="spin" /> : <Save size={18} />}
          <span>Save Manifest</span>
        </button>
      </div>

      {/* Manifest Table Selector */}
      <div className="card" style={{ padding: 0, overflow: 'hidden' }}>
        <table style={{ width: '100%', borderCollapse: 'collapse', textAlign: 'left' }}>
          <thead>
            <tr style={{ background: 'rgba(255,255,255,0.02)', borderBottom: '1px solid rgba(255,255,255,0.08)', color: 'var(--text-secondary)', fontSize: '0.85rem' }}>
              <th style={{ padding: '16px', width: '60px' }}>Include</th>
              <th style={{ padding: '16px' }}>Owner / Table Name</th>
              <th style={{ padding: '16px' }}>Custom SQL WHERE Filter</th>
              <th style={{ padding: '16px', width: '160px' }}>Excluded Columns</th>
              <th style={{ padding: '16px', width: '100px', textAlign: 'right' }}>Actions</th>
            </tr>
          </thead>
          <tbody>
            {tables.map((t, idx) => (
              <tr
                key={idx}
                style={{
                  borderBottom: '1px solid rgba(255,255,255,0.04)',
                  background: t.included ? 'none' : 'rgba(0, 0, 0, 0.2)',
                  opacity: t.included ? 1 : 0.6,
                  transition: 'all 0.2s'
                }}
              >
                <td style={{ padding: '16px', textAlign: 'center' }}>
                  <button
                    type="button"
                    style={{ background: 'none', border: 'none', color: t.included ? '#3b82f6' : 'var(--text-secondary)', cursor: 'pointer', display: 'flex', margin: 'auto' }}
                    onClick={() => handleToggleInclude(idx)}
                  >
                    {t.included ? <CheckSquare size={20} /> : <Square size={20} />}
                  </button>
                </td>
                <td style={{ padding: '16px' }}>
                  <div style={{ display: 'flex', alignItems: 'center', gap: '10px' }}>
                    <Table2 size={16} style={{ color: 'var(--text-secondary)' }} />
                    <div>
                      <div style={{ fontWeight: 'bold', fontSize: '0.95rem' }}>{t.tableName}</div>
                      <span style={{ fontSize: '0.75rem', color: 'var(--text-secondary)', textTransform: 'uppercase' }}>
                        {t.owner}
                      </span>
                    </div>
                  </div>
                </td>
                <td style={{ padding: '16px' }}>
                  <input
                    className="input"
                    type="text"
                    placeholder="e.g. ID > 1000 AND STATUS = 'ACTIVE'"
                    value={t.whereClause || ''}
                    onChange={e => handleWhereClauseChange(idx, e.target.value)}
                    disabled={!t.included}
                    style={{ fontSize: '0.85rem', padding: '6px 12px' }}
                  />
                </td>
                <td style={{ padding: '16px', fontSize: '0.8rem', color: 'var(--text-secondary)' }}>
                  {t.excludedColumns ? (
                    <span style={{ color: '#f87171' }}>{t.excludedColumns.split(',').length} excluded</span>
                  ) : (
                    'None'
                  )}
                </td>
                <td style={{ padding: '16px', textAlign: 'right' }}>
                  <button
                    className="btn btn-secondary"
                    style={{ padding: '6px 10px', fontSize: '0.8rem' }}
                    onClick={() => setActiveTableIdx(idx)}
                    disabled={!t.included}
                  >
                    <Sliders size={14} />
                    <span>Columns</span>
                  </button>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>

      {/* Column Exclude Modal Drawer */}
      {activeTable && (
        <div style={{ position: 'fixed', inset: 0, background: 'rgba(0,0,0,0.6)', display: 'flex', alignItems: 'center', justifyContent: 'flex-end', zIndex: 100 }}>
          <div className="card" style={{ maxWidth: '450px', width: '100%', height: '100vh', borderRadius: 0, borderLeft: '1px solid rgba(255,255,255,0.08)', display: 'flex', flexDirection: 'column', gap: '20px', padding: '32px' }}>
            <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center' }}>
              <div>
                <h3 style={{ margin: 0 }}>Exclude Columns</h3>
                <span style={{ fontSize: '0.8rem', color: 'var(--text-secondary)' }}>{activeTable.tableName}</span>
              </div>
              <button className="btn btn-secondary" style={{ padding: '4px 8px' }} onClick={() => setActiveTableIdx(null)}>
                Done
              </button>
            </div>

            <p style={{ fontSize: '0.85rem', color: 'var(--text-secondary)', margin: 0 }}>
              Uncheck columns that you wish to exclude from this table migration. Excluded columns will not appear in the generated DDL or data load.
            </p>

            <div style={{ flex: 1, overflowY: 'auto', display: 'flex', flexDirection: 'column', gap: '10px', paddingRight: '8px' }}>
              {activeTable.columns?.map((col: any) => (
                <div
                  key={col.columnName}
                  style={{
                    display: 'flex',
                    justifyContent: 'space-between',
                    alignItems: 'center',
                    padding: '10px 14px',
                    borderRadius: '8px',
                    background: col.isExcluded ? 'rgba(239, 68, 68, 0.03)' : 'rgba(255,255,255,0.02)',
                    border: col.isExcluded ? '1px solid rgba(239, 68, 68, 0.1)' : '1px solid rgba(255,255,255,0.04)',
                    transition: 'all 0.2s'
                  }}
                >
                  <div>
                    <div style={{ fontWeight: col.isExcluded ? 'normal' : 'bold', fontSize: '0.9rem', color: col.isExcluded ? 'var(--text-secondary)' : 'var(--text-primary)' }}>
                      {col.columnName}
                    </div>
                    <span style={{ fontSize: '0.75rem', color: 'var(--text-secondary)', fontFamily: 'monospace' }}>
                      {col.oracleDataType} → {col.postgresDataType}
                    </span>
                  </div>

                  <button
                    type="button"
                    style={{ background: 'none', border: 'none', color: col.isExcluded ? 'var(--text-secondary)' : '#10b981', cursor: 'pointer' }}
                    onClick={() => handleToggleColumnExclude(col.columnName)}
                  >
                    {col.isExcluded ? <Square size={18} /> : <CheckSquare size={18} />}
                  </button>
                </div>
              ))}
            </div>
          </div>
        </div>
      )}
    </div>
  );
}
