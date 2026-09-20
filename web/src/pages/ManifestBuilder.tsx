import { useEffect, useMemo, useRef, useState } from 'react';
import { useParams, useNavigate, Link } from 'react-router-dom';
import { ArrowLeft, ChevronDown, Plus, RefreshCw, Save, Search, SearchX, Table2 } from 'lucide-react';
import {
  createManifest,
  fetchConnections,
  fetchManifest,
  refreshDiscovery,
  updateManifestTables,
} from '../api';
import SchemaCombobox from '../components/SchemaCombobox';
import TableGrid, { type SortKey, type SortState } from '../components/TableGrid';
import '../builder.css';

type StatusFilter = 'all' | 'selected' | 'unselected';

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

  // View-only state: none of this is saved or sent to the API.
  const [statusFilter, setStatusFilter] = useState<StatusFilter>('all');
  const [sort, setSort] = useState<SortState>(null);
  const [addOpen, setAddOpen] = useState(false);
  const addWrapRef = useRef<HTMLDivElement>(null);
  const addBtnRef = useRef<HTMLButtonElement>(null);
  const addTextRef = useRef<HTMLTextAreaElement>(null);

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
          setManifestName(`Table selection ${new Date().toISOString().slice(0, 10)}`);
        }
      } catch (err: any) {
        setError(err.message || 'Could not load this table selection.');
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

  // Search first, then the All / Selected / Unselected split. The split's counts are taken over the
  // searched set so that they always add up to what "All" shows.
  const searchedTables = useMemo(() => {
    const q = search.trim().toLowerCase();
    if (!q) return tables;
    return tables.filter((t) => t.tableName.toLowerCase().includes(q) || t.owner.toLowerCase().includes(q));
  }, [tables, search]);

  const searchedSelected = useMemo(() => searchedTables.filter((t) => t.included).length, [searchedTables]);

  const visibleTables = useMemo(() => {
    const byStatus =
      statusFilter === 'all'
        ? searchedTables
        : searchedTables.filter((t) => (statusFilter === 'selected' ? t.included : !t.included));
    if (!sort) return byStatus;

    const dir = sort.dir === 'asc' ? 1 : -1;
    const compare = (a: BuilderTable, b: BuilderTable) => {
      switch (sort.key) {
        case 'estRows':
          return ((a.estRows ?? 0) - (b.estRows ?? 0)) * dir;
        case 'estBytes':
          return ((a.estBytes ?? 0) - (b.estBytes ?? 0)) * dir;
        default:
          // numeric: true so BL_2 sorts before BL_10.
          return a[sort.key].localeCompare(b[sort.key], undefined, { numeric: true, sensitivity: 'base' }) * dir;
      }
    };
    return [...byStatus].sort(compare);
  }, [searchedTables, statusFilter, sort]);

  // none -> ascending -> descending -> none
  const handleSort = (key: SortKey) => {
    setSort((prev) => {
      if (!prev || prev.key !== key) return { key, dir: 'asc' };
      return prev.dir === 'asc' ? { key, dir: 'desc' } : null;
    });
  };

  // Acts on the rows currently shown, so search + select-all selects only the matches.
  const handleToggleAll = (next: boolean) => {
    const shown = new Set(visibleTables.map((t) => t.key));
    setTables((ts) => ts.map((t) => (shown.has(t.key) ? { ...t, included: next } : t)));
  };

  const clearFilters = () => {
    setSearch('');
    setStatusFilter('all');
  };

  // Add-tables popover: Escape or an outside click closes it, and it takes focus while open.
  useEffect(() => {
    if (!addOpen) return;
    addTextRef.current?.focus();

    const onMouseDown = (e: MouseEvent) => {
      if (!addWrapRef.current?.contains(e.target as Node)) setAddOpen(false);
    };
    const onKeyDown = (e: KeyboardEvent) => {
      if (e.key === 'Escape') {
        setAddOpen(false);
        addBtnRef.current?.focus();
      }
    };
    document.addEventListener('mousedown', onMouseDown);
    document.addEventListener('keydown', onKeyDown);
    return () => {
      document.removeEventListener('mousedown', onMouseDown);
      document.removeEventListener('keydown', onKeyDown);
    };
  }, [addOpen]);

  const handleRefreshDictionary = async () => {
    if (!connectionId || !owner.trim()) {
      alert('Choose a source database and a schema first.');
      return;
    }
    setRefreshing(true);
    setError(null);
    try {
      const result = await refreshDiscovery(Number(connectionId), owner.trim().toUpperCase());
      const discovered = (result.tables || []).map(toBuilderTable);
      setTables((prev) => mergeTables(prev, discovered));
    } catch (err: any) {
      setError(err.message || 'Could not scan the source database.');
    } finally {
      setRefreshing(false);
    }
  };

  const handleAddCustomTables = async () => {
    if (!connectionId || !owner.trim()) {
      alert('Choose a source database and a schema first.');
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
        setAddOpen(false);
      }
    } catch (err: any) {
      setError(err.message || 'Could not add those tables.');
    } finally {
      setAddingCustom(false);
    }
  };

  const handleSave = async () => {
    if (tables.length === 0) {
      alert('Select at least one table before saving.');
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
          name: manifestName.trim() || 'Table selection',
          version: 1,
        });
        await updateManifestTables(created.id, payload);
        navigate(`/applications/${appIdNum}/manifests/${created.id}/builder`, { replace: true });
      } else {
        await updateManifestTables(Number(manifestId), payload);
      }
    } catch (err: any) {
      setError(err.message || 'Could not save the table selection.');
    } finally {
      setSaving(false);
    }
  };

  if (loading) {
    return (
      <div className="card flex items-center justify-center gap-3 py-12 text-slate-300">
        <RefreshCw size={20} className="spin" />
        Loading table selection...
      </div>
    );
  }

  const includedTables = tables.filter((t) => t.included);
  const stats = {
    selected: includedTables.length,
    total: tables.length,
    rows: includedTables.reduce((acc, t) => acc + (t.estRows || 0), 0),
    bytes: includedTables.reduce((acc, t) => acc + (t.estBytes || 0), 0),
    withFilter: includedTables.filter((t) => t.whereClause.trim() !== '').length,
  };

  const canScan = !!connectionId && !!owner.trim();
  const resetKey = `${search}|${statusFilter}|${sort ? `${sort.key}:${sort.dir}` : 'none'}`;

  const emptyState =
    tables.length === 0 ? (
      <div className="tb-empty">
        <Table2 size={40} />
        <div className="tb-empty-title">No tables in this selection yet</div>
        <p>Scan the source schema to list everything in it, or add specific tables by name.</p>
        <div className="tb-empty-actions">
          <button type="button" className="tb-btn tb-btn-primary" onClick={handleRefreshDictionary} disabled={refreshing || !canScan}>
            <RefreshCw size={16} className={refreshing ? 'animate-spin' : ''} />
            Scan source database
          </button>
          <button type="button" className="tb-btn" onClick={() => setAddOpen(true)}>
            <Plus size={16} />
            Add tables
          </button>
        </div>
        {!canScan && <p className="tb-muted">Choose a source database and a schema first.</p>}
      </div>
    ) : (
      <div className="tb-empty">
        <SearchX size={40} />
        <div className="tb-empty-title">No tables match</div>
        <p>Nothing matches the current search and filter.</p>
        <div className="tb-empty-actions">
          <button type="button" className="tb-btn" onClick={clearFilters}>
            Clear search and filter
          </button>
        </div>
      </div>
    );

  return (
    <div className="page-fill">
      <div className="tb-header">
        <div className="tb-title-block">
          <Link to={`/applications/${appId}`} className="tb-back" aria-label="Back to migration" title="Back to migration">
            <ArrowLeft size={20} />
          </Link>
          <div>
            {isNew ? (
              <input
                value={manifestName}
                onChange={(e) => setManifestName(e.target.value)}
                className="tb-title-input"
                aria-label="Table selection name"
              />
            ) : (
              <h1 className="tb-title">{manifestName || 'Select tables'}</h1>
            )}
            <p className="tb-subtitle">Pick the tables to copy, and filter their rows if you need to.</p>
          </div>
        </div>
        <div className="tb-actions">
          <button type="button" className="tb-btn" onClick={handleRefreshDictionary} disabled={refreshing || !canScan}>
            <RefreshCw size={16} className={refreshing ? 'animate-spin' : ''} />
            Scan source database
          </button>
          <button type="button" className="tb-btn tb-btn-primary" onClick={handleSave} disabled={saving || tables.length === 0}>
            <Save size={16} className={saving ? 'animate-spin' : ''} />
            Save selection
          </button>
        </div>
      </div>

      {error && (
        <div className="tb-error" role="alert">
          {error}
        </div>
      )}

      <div className="tb-card">
        <div className="tb-toolbar">
          <div className="tb-field">
            <label className="tb-field-label" htmlFor="tb-source-db">Source database</label>
            <select
              id="tb-source-db"
              className="tb-select"
              value={connectionId}
              onChange={(e) => setConnectionId(Number(e.target.value))}
            >
              <option value="">Select a source database</option>
              {oracleConnections.map((c) => (
                <option key={c.id} value={c.id}>
                  {c.name}
                </option>
              ))}
            </select>
          </div>

          <div className="tb-field">
            <label className="tb-field-label" htmlFor="tb-source-schema">Source schema</label>
            <SchemaCombobox
              id="tb-source-schema"
              className="tb-schema-box"
              connectionId={connectionId}
              value={owner}
              onChange={setOwner}
            />
          </div>

          <div className="tb-popover-wrap" ref={addWrapRef}>
            <button
              ref={addBtnRef}
              type="button"
              className="tb-btn"
              aria-haspopup="dialog"
              aria-expanded={addOpen}
              onClick={() => setAddOpen((o) => !o)}
            >
              <Plus size={16} />
              Add tables
              <ChevronDown size={14} />
            </button>

            {addOpen && (
              <div className="tb-popover" role="dialog" aria-label="Add specific tables">
                <h2 className="tb-popover-title">Add specific tables</h2>
                <p className="tb-popover-hint">
                  Not limited to a full schema scan. One table name per line, or comma-separated.
                </p>
                <textarea
                  ref={addTextRef}
                  className="tb-textarea"
                  placeholder={'EMPLOYEES, DEPARTMENTS\nINVOICES'}
                  value={customTablesText}
                  onChange={(e) => setCustomTablesText(e.target.value)}
                  onKeyDown={(e) => {
                    if ((e.ctrlKey || e.metaKey) && e.key === 'Enter') {
                      e.preventDefault();
                      handleAddCustomTables();
                    }
                  }}
                />
                <div className="tb-popover-actions">
                  <span className="tb-muted">Ctrl + Enter to add</span>
                  <button
                    type="button"
                    className="tb-btn tb-btn-primary"
                    onClick={handleAddCustomTables}
                    disabled={addingCustom || !canScan || !customTablesText.trim()}
                  >
                    <Plus size={16} className={addingCustom ? 'animate-spin' : ''} />
                    Add tables
                  </button>
                </div>
                {!canScan && <p className="tb-popover-hint" style={{ marginTop: 8 }}>Choose a source database and a schema first.</p>}
              </div>
            )}
          </div>
        </div>

        <div className="tb-toolbar">
          <div className="tb-search">
            <Search size={16} />
            <input
              type="text"
              className="tb-input"
              placeholder="Search tables"
              aria-label="Search tables"
              value={search}
              onChange={(e) => setSearch(e.target.value)}
            />
          </div>

          <div className="tb-seg" role="group" aria-label="Show tables">
            <button type="button" aria-pressed={statusFilter === 'all'} onClick={() => setStatusFilter('all')}>
              All <span className="tb-seg-count">{searchedTables.length}</span>
            </button>
            <button type="button" aria-pressed={statusFilter === 'selected'} onClick={() => setStatusFilter('selected')}>
              Selected <span className="tb-seg-count">{searchedSelected}</span>
            </button>
            <button type="button" aria-pressed={statusFilter === 'unselected'} onClick={() => setStatusFilter('unselected')}>
              Unselected <span className="tb-seg-count">{searchedTables.length - searchedSelected}</span>
            </button>
          </div>

          <span className="tb-muted tb-spacer">
            Showing {visibleTables.length.toLocaleString()} of {tables.length.toLocaleString()}
          </span>
        </div>
      </div>

      <TableGrid
        rows={visibleTables}
        sort={sort}
        onSort={handleSort}
        resetKey={resetKey}
        onToggle={toggleTable}
        onToggleAll={handleToggleAll}
        onWhere={setWhereClause}
        onRemove={removeTable}
        empty={emptyState}
        stats={stats}
      />
    </div>
  );
}
