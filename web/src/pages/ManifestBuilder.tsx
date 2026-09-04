import { useEffect, useMemo, useState } from 'react';
import { useParams, useNavigate, Link } from 'react-router-dom';
import { ArrowLeft, Search, Save, RefreshCw, Plus } from 'lucide-react';
import {
  createManifest,
  fetchConnections,
  fetchManifest,
  refreshDiscovery,
  updateManifestTables,
} from '../api';

type BuilderColumn = {
  columnName: string;
  oracleDataType: string;
  postgresDataType: string;
  isNullable: boolean;
  isPrimaryKey: boolean;
  isExcluded: boolean;
};

type BuilderTable = {
  key: string; // owner.tableName, used as a stable local row id
  owner: string;
  tableName: string;
  included: boolean;
  whereClause: string;
  estRows: number | null;
  estBytes: number | null;
  hasLobs: boolean;
  isPartitioned: boolean;
  isIot: boolean;
  columns: BuilderColumn[];
};

const rowKey = (owner: string, tableName: string) => `${owner.toUpperCase()}.${tableName.toUpperCase()}`;

function toBuilderTable(raw: any): BuilderTable {
  return {
    key: rowKey(raw.owner, raw.tableName),
    owner: raw.owner,
    tableName: raw.tableName,
    included: raw.included ?? true,
    whereClause: raw.whereClause || '',
    estRows: raw.estRows ?? null,
    estBytes: raw.estBytes ?? null,
    hasLobs: !!raw.hasLobs,
    isPartitioned: !!raw.isPartitioned,
    isIot: !!raw.isIot,
    columns: (raw.columns || []).map((c: any) => ({
      columnName: c.columnName,
      oracleDataType: c.oracleDataType,
      postgresDataType: c.postgresDataType,
      isNullable: !!c.isNullable,
      isPrimaryKey: !!c.isPrimaryKey,
      isExcluded: !!c.isExcluded,
    })),
  };
}

function mergeTables(existing: BuilderTable[], incoming: BuilderTable[]): BuilderTable[] {
  const byKey = new Map(existing.map((t) => [t.key, t]));
  for (const table of incoming) {
    const prior = byKey.get(table.key);
    // Preserve a user's include/where edits if the table was already in the list.
    byKey.set(table.key, prior ? { ...table, included: prior.included, whereClause: prior.whereClause } : table);
  }
  return Array.from(byKey.values());
}

export default function ManifestBuilder() {
  const { appId, manifestId } = useParams();
  const navigate = useNavigate();
  const isNew = manifestId === 'new';
  const appIdNum = Number(appId);

  const [manifestName, setManifestName] = useState('');
  const [tables, setTables] = useState<BuilderTable[]>([]);
  const [search, setSearch] = useState('');
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  const [oracleConnections, setOracleConnections] = useState<any[]>([]);
  const [connectionId, setConnectionId] = useState<number | ''>('');
  const [owner, setOwner] = useState('');
  const [refreshing, setRefreshing] = useState(false);

  const [customTablesText, setCustomTablesText] = useState('');
  const [addingCustom, setAddingCustom] = useState(false);

  const [saving, setSaving] = useState(false);

  useEffect(() => {
    (async () => {
      setLoading(true);
      setError(null);
      try {
        const connections = await fetchConnections();
        const oracleOnly = connections.filter((c: any) => c.kind === 0);
        setOracleConnections(oracleOnly);
        if (oracleOnly.length > 0) setConnectionId(oracleOnly[0].id);

        if (!isNew) {
          const manifest = await fetchManifest(Number(manifestId));
          setManifestName(manifest.name || '');
          setTables((manifest.tables || []).map(toBuilderTable));
          if (manifest.tables?.[0]?.owner) setOwner(manifest.tables[0].owner);
        } else {
          setManifestName(`Custom Manifest ${new Date().toISOString().slice(0, 10)}`);
        }
      } catch (err: any) {
        setError(err.message || 'Failed to load manifest builder.');
      } finally {
        setLoading(false);
      }
    })();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [manifestId]);

  const toggleTable = (key: string) => {
    setTables((ts) => ts.map((t) => (t.key === key ? { ...t, included: !t.included } : t)));
  };

  const setWhereClause = (key: string, value: string) => {
    setTables((ts) => ts.map((t) => (t.key === key ? { ...t, whereClause: value } : t)));
  };

  const removeTable = (key: string) => {
    setTables((ts) => ts.filter((t) => t.key !== key));
  };

  const filteredTables = useMemo(
    () =>
      tables.filter(
        (t) =>
          t.tableName.toLowerCase().includes(search.toLowerCase()) ||
          t.owner.toLowerCase().includes(search.toLowerCase())
      ),
    [tables, search]
  );

  const handleRefreshDictionary = async () => {
    if (!connectionId || !owner.trim()) {
      alert('Select an Oracle connection and enter a schema/owner first.');
      return;
    }
    setRefreshing(true);
    setError(null);
    try {
      const result = await refreshDiscovery(Number(connectionId), owner.trim().toUpperCase());
      const discovered = (result.tables || []).map(toBuilderTable);
      setTables((prev) => mergeTables(prev, discovered));
    } catch (err: any) {
      setError(err.message || 'Failed to refresh discovery dictionary.');
    } finally {
      setRefreshing(false);
    }
  };

  const handleAddCustomTables = async () => {
    if (!connectionId || !owner.trim()) {
      alert('Select an Oracle connection and enter a schema/owner first.');
      return;
    }
    const names = customTablesText
      .split(/[\n,]/)
      .map((t) => t.trim())
      .filter(Boolean);
    if (names.length === 0) {
      alert('Type or paste one or more table names first.');
      return;
    }
    setAddingCustom(true);
    setError(null);
    try {
      const result = await refreshDiscovery(Number(connectionId), owner.trim().toUpperCase(), names);
      const discovered = (result.tables || []).map(toBuilderTable);
      if (discovered.length === 0) {
        setError(`None of the requested tables were found in ${owner.trim().toUpperCase()}.`);
      } else {
        setTables((prev) => mergeTables(prev, discovered));
        setCustomTablesText('');
      }
    } catch (err: any) {
      setError(err.message || 'Failed to add custom tables.');
    } finally {
      setAddingCustom(false);
    }
  };

  const handleSave = async () => {
    if (tables.length === 0) {
      alert('Add at least one table before saving.');
      return;
    }
    setSaving(true);
    setError(null);
    try {
      const payload = tables.map((t) => ({
        owner: t.owner,
        tableName: t.tableName,
        included: t.included,
        whereClause: t.whereClause || null,
        estRows: t.estRows,
        estBytes: t.estBytes,
        hasLobs: t.hasLobs,
        isPartitioned: t.isPartitioned,
        isIot: t.isIot,
        columns: t.columns.map((c) => ({
          columnName: c.columnName,
          oracleDataType: c.oracleDataType,
          postgresDataType: c.postgresDataType,
          isNullable: c.isNullable,
          isPrimaryKey: c.isPrimaryKey,
          isExcluded: c.isExcluded,
        })),
      }));

      if (isNew) {
        const created = await createManifest(appIdNum, {
          name: manifestName.trim() || 'Custom Manifest',
          version: 1,
        });
        await updateManifestTables(created.id, payload);
        navigate(`/applications/${appIdNum}/manifests/${created.id}/builder`, { replace: true });
      } else {
        await updateManifestTables(Number(manifestId), payload);
      }
    } catch (err: any) {
      setError(err.message || 'Failed to save manifest.');
    } finally {
      setSaving(false);
    }
  };

  if (loading) {
    return (
      <div className="card flex items-center justify-center gap-3 py-12 text-slate-300">
        <RefreshCw size={20} className="spin" />
        Loading manifest builder...
      </div>
    );
  }

  const includedCount = tables.filter((t) => t.included).length;
  const totalRows = tables.filter((t) => t.included).reduce((acc, t) => acc + (t.estRows || 0), 0);
  const totalBytes = tables.filter((t) => t.included).reduce((acc, t) => acc + (t.estBytes || 0), 0);

  return (
    <div className="flex flex-col h-[calc(100vh-8rem)]">
      <div className="flex items-center justify-between mb-6 flex-wrap gap-3">
        <div className="flex items-center gap-4">
          <Link to={`/applications/${appId}`} className="p-2 hover:bg-slate-800 rounded-lg text-slate-400 transition-colors">
            <ArrowLeft size={20} />
          </Link>
          <div>
            {isNew ? (
              <input
                value={manifestName}
                onChange={(e) => setManifestName(e.target.value)}
                className="bg-transparent text-2xl font-bold tracking-tight border-b border-transparent hover:border-slate-700 focus:border-blue-500 focus:outline-none"
              />
            ) : (
              <h1 className="text-2xl font-bold tracking-tight">{manifestName || 'Manifest Builder'}</h1>
            )}
            <p className="text-slate-400 text-sm mt-1">Select and configure tables to migrate</p>
          </div>
        </div>
        <div className="flex items-center gap-3">
          <button
            className="flex items-center gap-2 px-4 py-2 bg-slate-900 hover:bg-slate-800 border border-slate-700 text-slate-300 rounded-lg transition-colors font-medium disabled:opacity-50"
            onClick={handleRefreshDictionary}
            disabled={refreshing || !connectionId || !owner.trim()}
          >
            <RefreshCw size={16} className={refreshing ? 'animate-spin' : ''} />
            Refresh Dictionary
          </button>
          <button
            className="flex items-center gap-2 px-4 py-2 bg-blue-600 hover:bg-blue-700 text-white rounded-lg transition-colors font-medium shadow-lg shadow-blue-900/20 disabled:opacity-50"
            onClick={handleSave}
            disabled={saving || tables.length === 0}
          >
            <Save size={16} className={saving ? 'animate-spin' : ''} />
            Save Manifest
          </button>
        </div>
      </div>

      {error && (
        <div className="mb-4 rounded-lg border border-rose-500/30 bg-rose-500/10 p-3 text-sm text-rose-200">{error}</div>
      )}

      {/* Discovery source + custom table entry */}
      <div className="mb-6 bg-slate-950 p-4 rounded-xl border border-slate-800 flex flex-col gap-4">
        <div className="flex flex-wrap items-end gap-4">
          <div className="min-w-[220px]">
            <label className="block text-xs font-medium text-slate-400 mb-1">Oracle Connection</label>
            <select
              className="w-full bg-slate-900 border border-slate-700 rounded-lg py-2 px-3 text-sm focus:outline-none focus:border-blue-500"
              value={connectionId}
              onChange={(e) => setConnectionId(Number(e.target.value))}
            >
              <option value="">Select Oracle profile</option>
              {oracleConnections.map((c) => (
                <option key={c.id} value={c.id}>
                  {c.name}
                </option>
              ))}
            </select>
          </div>
          <div className="min-w-[160px]">
            <label className="block text-xs font-medium text-slate-400 mb-1">Schema / Owner</label>
            <input
              className="w-full bg-slate-900 border border-slate-700 rounded-lg py-2 px-3 text-sm focus:outline-none focus:border-blue-500"
              placeholder="HR"
              value={owner}
              onChange={(e) => setOwner(e.target.value.toUpperCase())}
            />
          </div>
        </div>

        <div className="flex flex-wrap items-end gap-3">
          <div className="flex-1 min-w-[260px]">
            <label className="block text-xs font-medium text-slate-400 mb-1">
              Add specific tables (dynamic - not limited to a full schema scan)
            </label>
            <textarea
              className="w-full bg-slate-900 border border-slate-700 rounded-lg py-2 px-3 text-sm focus:outline-none focus:border-blue-500 font-mono"
              rows={2}
              placeholder={'One table per line or comma-separated, e.g.\nEMPLOYEES, DEPARTMENTS\nINVOICES'}
              value={customTablesText}
              onChange={(e) => setCustomTablesText(e.target.value)}
            />
          </div>
          <button
            className="flex items-center gap-2 px-4 py-2 bg-slate-900 hover:bg-slate-800 border border-slate-700 text-slate-300 rounded-lg transition-colors font-medium disabled:opacity-50 h-fit"
            onClick={handleAddCustomTables}
            disabled={addingCustom || !connectionId || !owner.trim() || !customTablesText.trim()}
          >
            <Plus size={16} className={addingCustom ? 'animate-spin' : ''} />
            Add Tables
          </button>
        </div>
      </div>

      {/* Toolbar */}
      <div className="flex items-center gap-4 mb-6 bg-slate-950 p-4 rounded-xl border border-slate-800">
        <div className="relative flex-1">
          <Search className="absolute left-3 top-1/2 -translate-y-1/2 text-slate-500" size={18} />
          <input
            type="text"
            placeholder="Search tables..."
            className="w-full bg-slate-900 border border-slate-700 rounded-lg py-2 pl-10 pr-4 focus:outline-none focus:border-blue-500 focus:ring-1 focus:ring-blue-500 transition-all text-sm"
            value={search}
            onChange={(e) => setSearch(e.target.value)}
          />
        </div>
      </div>

      {/* Data Grid */}
      <div className="flex-1 bg-slate-950 border border-slate-800 rounded-xl overflow-hidden flex flex-col">
        <div className="overflow-x-auto flex-1">
          <table className="w-full text-left text-sm whitespace-nowrap">
            <thead className="bg-slate-900/80 text-slate-400 sticky top-0 z-10 shadow-sm border-b border-slate-800">
              <tr>
                <th className="p-4 w-12 text-center"></th>
                <th className="p-4 font-medium">Owner</th>
                <th className="p-4 font-medium">Table Name</th>
                <th className="p-4 font-medium text-right">Est. Rows</th>
                <th className="p-4 font-medium text-right">Size (MB)</th>
                <th className="p-4 font-medium text-center">LOBs</th>
                <th className="p-4 font-medium w-48">Filter (WHERE)</th>
                <th className="p-4 font-medium w-16"></th>
              </tr>
            </thead>
            <tbody className="divide-y divide-slate-800/50">
              {filteredTables.map((row) => (
                <tr key={row.key} className={`hover:bg-slate-900/40 transition-colors ${row.included ? 'bg-blue-900/5' : ''}`}>
                  <td className="p-4 text-center">
                    <input
                      type="checkbox"
                      checked={row.included}
                      onChange={() => toggleTable(row.key)}
                      className="rounded border-slate-600 bg-slate-800 text-blue-600 focus:ring-blue-600 focus:ring-offset-slate-950"
                    />
                  </td>
                  <td className="p-4 font-mono text-slate-300 text-xs">{row.owner}</td>
                  <td className="p-4 font-medium">
                    {row.tableName}
                    {row.columns.length === 0 && (
                      <span className="ml-2 px-2 py-0.5 bg-amber-500/10 text-amber-300 rounded text-[10px] font-bold tracking-wider">
                        NO COLUMNS
                      </span>
                    )}
                  </td>
                  <td className="p-4 text-right text-slate-400 font-mono text-xs">{(row.estRows ?? 0).toLocaleString()}</td>
                  <td className="p-4 text-right text-slate-400 font-mono text-xs">
                    {((row.estBytes ?? 0) / 1024 / 1024).toFixed(2)}
                  </td>
                  <td className="p-4 text-center">
                    {row.hasLobs && (
                      <span className="px-2 py-0.5 bg-rose-500/10 text-rose-400 rounded text-[10px] font-bold tracking-wider">
                        LOB
                      </span>
                    )}
                  </td>
                  <td className="p-4">
                    <input
                      type="text"
                      placeholder="e.g. YEAR = 2024"
                      disabled={!row.included}
                      value={row.whereClause}
                      onChange={(e) => setWhereClause(row.key, e.target.value)}
                      className="w-full bg-slate-900 border border-slate-700 rounded py-1 px-2 text-xs focus:outline-none focus:border-blue-500 disabled:opacity-50 disabled:cursor-not-allowed"
                    />
                  </td>
                  <td className="p-4 text-center">
                    <button
                      className="text-slate-500 hover:text-rose-400 text-xs"
                      onClick={() => removeTable(row.key)}
                      title="Remove from manifest"
                    >
                      Remove
                    </button>
                  </td>
                </tr>
              ))}

              {filteredTables.length === 0 && (
                <tr>
                  <td colSpan={8} className="p-8 text-center text-slate-500">
                    No tables yet. Use Refresh Dictionary for a full schema scan, or add specific tables above.
                  </td>
                </tr>
              )}
            </tbody>
          </table>
        </div>
        <div className="bg-slate-900 border-t border-slate-800 p-3 flex justify-between items-center text-xs text-slate-400">
          <div>
            Selected: <span className="text-blue-400 font-bold">{includedCount}</span> tables
          </div>
          <div className="flex gap-4">
            <div>
              Total Rows: <span className="text-slate-200">{totalRows.toLocaleString()}</span>
            </div>
            <div>
              Total Size: <span className="text-slate-200">{(totalBytes / 1024 / 1024).toFixed(2)} MB</span>
            </div>
          </div>
        </div>
      </div>
    </div>
  );
}
