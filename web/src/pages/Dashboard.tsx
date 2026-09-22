import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { Link } from 'react-router-dom';
import { ArrowRight, RefreshCw, Square, Search } from 'lucide-react';
import {
  countTable,
  fetchConnections,
  fetchDashboardSummary,
  fetchRowCountComparison,
  fetchSavedCountSchemas,
  fetchSchemas,
  fetchTargetSchemas,
  hasRole,
  refreshCountTables,
  type DashboardSummary,
  type PairStatus,
  type RowCountComparison,
  type TableCount,
  type TablePair,
} from '../api';
import { formatAgo, formatBytes, formatCount, formatDuration } from '../format';
import { runKindLabel, statusColor, statusLabel } from '../labels';
import './Dashboard.css';

type Side = 'source' | 'destination';
type Choice = { connectionId: number | null; schema: string };
type SyncState = { running: boolean; done: number; total: number; current: string | null; message: string | null };

const IDLE: SyncState = { running: false, done: 0, total: 0, current: null, message: null };
const CHOICE_KEY = 'o2p_dashboard_compare';

const STATUS: Record<PairStatus, { label: string; tone: string; hint: string }> = {
  match: { label: 'Match', tone: 'good', hint: 'Both tables hold the same number of rows.' },
  missing_rows: { label: 'Missing rows', tone: 'bad', hint: 'The destination has fewer rows than the source.' },
  extra_rows: { label: 'Extra rows', tone: 'warn', hint: 'The destination has more rows than the source.' },
  only_source: { label: 'Only in source', tone: 'muted', hint: 'No destination table with this name.' },
  only_destination: { label: 'Only in destination', tone: 'muted', hint: 'No source table with this name.' },
  not_counted: { label: 'Not counted', tone: 'muted', hint: 'One side has not been counted yet. Use Sync.' },
  error: { label: 'Error', tone: 'bad', hint: 'The last count of one side failed.' },
};

type Filter = 'all' | 'differences' | 'one_side' | 'not_counted';
const FILTERS: { id: Filter; label: string; test: (s: PairStatus) => boolean }[] = [
  { id: 'all', label: 'All', test: () => true },
  { id: 'differences', label: 'Differences', test: (s) => s === 'missing_rows' || s === 'extra_rows' || s === 'error' },
  { id: 'one_side', label: 'Only one side', test: (s) => s === 'only_source' || s === 'only_destination' },
  { id: 'not_counted', label: 'Not counted', test: (s) => s === 'not_counted' },
];

// Same rules as RowCountComparison.Compare on the server, so a count can update its row in place during a sync.
function restatus(pair: TablePair): TablePair {
  const { source, destination } = pair;
  if (!destination) return { ...pair, difference: null, status: 'only_source' };
  if (!source) return { ...pair, difference: null, status: 'only_destination' };
  if (source.error || destination.error) return { ...pair, difference: null, status: 'error' };
  if (source.rows == null || destination.rows == null) return { ...pair, difference: null, status: 'not_counted' };
  const difference = destination.rows - source.rows;
  return { ...pair, difference, status: difference === 0 ? 'match' : difference < 0 ? 'missing_rows' : 'extra_rows' };
}

function loadChoices(): { source: Choice; destination: Choice } {
  const empty = { source: { connectionId: null, schema: '' }, destination: { connectionId: null, schema: '' } };
  try {
    const raw = localStorage.getItem(CHOICE_KEY);
    return raw ? { ...empty, ...JSON.parse(raw) } : empty;
  } catch {
    return empty;
  }
}

export default function Dashboard() {
  const canSync = hasRole('Admin') || hasRole('Operator');

  const [summary, setSummary] = useState<DashboardSummary | null>(null);
  const [connections, setConnections] = useState<any[]>([]);
  const [choices, setChoices] = useState(loadChoices);
  const [schemaOptions, setSchemaOptions] = useState<Record<Side, string[]>>({ source: [], destination: [] });
  const [comparison, setComparison] = useState<RowCountComparison | null>(null);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [sync, setSync] = useState<Record<Side, SyncState>>({ source: IDLE, destination: IDLE });
  const [busyRow, setBusyRow] = useState<string | null>(null);
  const [filter, setFilter] = useState<Filter>('all');
  const [search, setSearch] = useState('');
  const aborters = useRef<Record<Side, AbortController | null>>({ source: null, destination: null });

  const oracle = useMemo(() => connections.filter((c) => c.kind === 0), [connections]);
  const postgres = useMemo(() => connections.filter((c) => c.kind === 1), [connections]);
  const ready = !!(choices.source.connectionId && choices.source.schema && choices.destination.connectionId && choices.destination.schema);

  // ---- top cards --------------------------------------------------------------------------------
  useEffect(() => {
    let alive = true;
    const load = () => fetchDashboardSummary().then((s) => alive && setSummary(s)).catch(() => {});
    load();
    const timer = setInterval(load, 5000);
    return () => { alive = false; clearInterval(timer); };
  }, []);

  useEffect(() => {
    fetchConnections().then(setConnections).catch(() => setConnections([]));
  }, []);

  useEffect(() => {
    try { localStorage.setItem(CHOICE_KEY, JSON.stringify(choices)); } catch { /* per-browser convenience only */ }
  }, [choices]);

  // Stop any sync still running when the page is left.
  useEffect(() => () => {
    aborters.current.source?.abort();
    aborters.current.destination?.abort();
  }, []);

  // ---- schema pickers ---------------------------------------------------------------------------
  const loadSchemas = useCallback(async (side: Side, connectionId: number | null) => {
    if (!connectionId) {
      setSchemaOptions((o) => ({ ...o, [side]: [] }));
      return;
    }
    // Schemas that already have counts are always offered - that is all a Viewer can see.
    const saved = await fetchSavedCountSchemas(connectionId);
    let live: string[] = [];
    if (canSync) {
      try {
        live = side === 'source'
          ? (await fetchSchemas(connectionId)).schemas.map((s) => s.name)
          : (await fetchTargetSchemas(connectionId)).schemas.map((s) => s.name);
      } catch {
        live = [];
      }
    }
    const all = Array.from(new Set([...live, ...saved])).sort((a, b) => a.localeCompare(b));
    setSchemaOptions((o) => ({ ...o, [side]: all }));
  }, [canSync]);

  useEffect(() => { loadSchemas('source', choices.source.connectionId); }, [choices.source.connectionId, loadSchemas]);
  useEffect(() => { loadSchemas('destination', choices.destination.connectionId); }, [choices.destination.connectionId, loadSchemas]);

  const choose = (side: Side, patch: Partial<Choice>) => {
    aborters.current[side]?.abort();
    setChoices((c) => ({ ...c, [side]: { ...c[side], ...patch } }));
  };

  // ---- comparison -------------------------------------------------------------------------------
  const loadComparison = useCallback(async () => {
    if (!ready) {
      setComparison(null);
      return;
    }
    try {
      const data = await fetchRowCountComparison(
        choices.source.connectionId!, choices.source.schema, choices.destination.connectionId!, choices.destination.schema);
      setComparison(data);
      setLoadError(null);
    } catch (err: any) {
      setLoadError(err.message);
    }
  }, [ready, choices]);

  useEffect(() => { loadComparison(); }, [loadComparison]);

  /** Puts one fresh count into its row, without reloading the whole comparison. */
  const applyCount = (side: Side, count: TableCount) => {
    setComparison((c) => {
      if (!c) return c;
      const pairs = c.pairs.map((p) => (p[side]?.table === count.table ? restatus({ ...p, [side]: count }) : p));
      return { ...c, pairs };
    });
  };

  const runSync = async (side: Side) => {
    const choice = choices[side];
    if (!choice.connectionId || !choice.schema) return;

    const aborter = new AbortController();
    aborters.current[side] = aborter;
    const set = (s: Partial<SyncState>) => setSync((all) => ({ ...all, [side]: { ...all[side], ...s } }));
    set({ ...IDLE, running: true, current: 'Reading the table list…' });

    let done = 0;
    let failed = 0;
    let total = 0;
    try {
      const tables = await refreshCountTables(choice.connectionId, choice.schema, aborter.signal);
      total = tables.length;
      await loadComparison(); // new tables appear as "Not counted", dropped ones disappear
      set({ total, current: null });

      for (const table of tables) {
        if (aborter.signal.aborted) break;
        set({ current: table, done });
        const count = await countTable(choice.connectionId, choice.schema, table, aborter.signal);
        if (count.error) failed++;
        applyCount(side, count);
        done++;
      }
      set({
        running: false, done, current: null,
        message: total === 0
          ? `No tables in ${choice.schema}.`
          : `Counted ${formatCount(done)} table${done === 1 ? '' : 's'}${failed ? `, ${failed} failed` : ''}.`,
      });
    } catch (err: any) {
      const stopped = aborter.signal.aborted;
      set({
        running: false, current: null,
        message: stopped ? `Stopped after ${formatCount(done)} of ${formatCount(total)} tables.` : err.message,
      });
    } finally {
      if (aborters.current[side] === aborter) aborters.current[side] = null;
      await loadComparison();
    }
  };

  const stopSync = (side: Side) => aborters.current[side]?.abort();

  const recountOne = async (side: Side, table: string) => {
    const choice = choices[side];
    if (!choice.connectionId) return;
    setBusyRow(`${side}:${table}`);
    try {
      applyCount(side, await countTable(choice.connectionId, choice.schema, table));
    } catch (err: any) {
      alert(err.message);
    } finally {
      setBusyRow(null);
    }
  };

  // ---- derived ----------------------------------------------------------------------------------
  const pairs = comparison?.pairs ?? [];
  const totals = useMemo(() => {
    const count = (test: (s: PairStatus) => boolean) => pairs.filter((p) => test(p.status)).length;
    return {
      tables: pairs.length,
      matching: count((s) => s === 'match'),
      comparable: count((s) => s === 'match' || s === 'missing_rows' || s === 'extra_rows'),
      sourceRows: pairs.reduce((n, p) => n + (p.source?.rows ?? 0), 0),
      destinationRows: pairs.reduce((n, p) => n + (p.destination?.rows ?? 0), 0),
      // Only tables present and counted on both sides: a table that exists on one side only would
      // otherwise swamp the figure with rows that were never meant to be compared.
      pairedDifference: pairs.reduce((n, p) => n + (p.difference ?? 0), 0),
      byFilter: Object.fromEntries(FILTERS.map((f) => [f.id, count(f.test)])) as Record<Filter, number>,
    };
  }, [pairs]);

  const visible = useMemo(() => {
    const test = FILTERS.find((f) => f.id === filter)!.test;
    const q = search.trim().toLowerCase();
    return pairs.filter((p) => test(p.status) && (!q || p.key.toLowerCase().includes(q)
      || p.destination?.table.toLowerCase().includes(q)));
  }, [pairs, filter, search]);

  const difference = totals.pairedDifference;

  return (
    <div className="dash">
      <div>
        <h1 className="text-gradient" style={{ margin: 0 }}>Dashboard</h1>
        <p className="dash-muted" style={{ margin: '4px 0 0 0' }}>What is copying now, and whether source and destination agree.</p>
      </div>

      <Summary summary={summary} />

      <section className="card dash-compare" aria-labelledby="compare-title">
        <div className="dash-compare-head">
          <h3 id="compare-title" style={{ margin: 0 }}>Row count comparison</h3>
          <p className="dash-muted" style={{ margin: 0 }}>
            Exact row counts of every table in two schemas, paired by table name (ORDERS matches orders).
          </p>
        </div>

        <div className="dash-pickers">
          <SidePicker
            label="Source" side="source" choice={choices.source} databases={oracle}
            schemas={schemaOptions.source} disabled={sync.source.running} onChange={(p) => choose('source', p)}
          />
          <ArrowRight className="dash-pickers-arrow" size={18} aria-hidden />
          <SidePicker
            label="Destination" side="destination" choice={choices.destination} databases={postgres}
            schemas={schemaOptions.destination} disabled={sync.destination.running} onChange={(p) => choose('destination', p)}
          />
        </div>

        {canSync && (
          <div className="dash-syncs">
            {(['source', 'destination'] as Side[]).map((side) => {
              const s = sync[side];
              const choice = choices[side];
              const name = side === 'source' ? 'Source' : 'Destination';
              return (
                <div key={side} className="dash-sync">
                  {s.running ? (
                    <button className="btn btn-secondary dash-btn" onClick={() => stopSync(side)}>
                      <Square size={14} /> Stop
                    </button>
                  ) : (
                    <button
                      className="btn dash-btn"
                      onClick={() => runSync(side)}
                      disabled={!choice.connectionId || !choice.schema}
                      title={`Read ${name.toLowerCase()} table list and count every table exactly`}
                    >
                      <RefreshCw size={14} /> Sync {name}
                    </button>
                  )}
                  <span className="dash-sync-status" aria-live="polite">
                    {s.running
                      ? s.total > 0
                        ? `Counting ${formatCount(s.done + 1)} of ${formatCount(s.total)}: ${s.current}`
                        : s.current
                      : s.message}
                  </span>
                  {s.running && s.total > 0 && (
                    <progress className="dash-progress" max={s.total} value={s.done} aria-label={`${name} sync progress`} />
                  )}
                </div>
              );
            })}
          </div>
        )}

        {!ready ? (
          <div className="dash-empty">Choose a source and a destination schema to compare.</div>
        ) : loadError ? (
          <div className="dash-empty dash-bad">{loadError}</div>
        ) : pairs.length === 0 ? (
          <div className="dash-empty">
            {canSync
              ? 'No counts yet. Press Sync Source and Sync Destination to read both schemas.'
              : 'No counts yet. Ask an operator to sync these schemas.'}
          </div>
        ) : (
          <>
            <div className="dash-totals">
              <span><strong>{formatCount(totals.matching)}</strong> of {formatCount(totals.comparable)} counted pairs match</span>
              <span>Source <strong>{formatCount(totals.sourceRows)}</strong> rows</span>
              <span>Destination <strong>{formatCount(totals.destinationRows)}</strong> rows</span>
              <span className={difference === 0 ? 'dash-good' : 'dash-bad'}>
                Difference in paired tables <strong>{difference > 0 ? '+' : difference < 0 ? '−' : ''}{formatCount(Math.abs(difference))}</strong>
              </span>
            </div>

            <div className="dash-tools">
              <div className="dash-filters" role="group" aria-label="Show">
                {FILTERS.map((f) => (
                  <button
                    key={f.id}
                    className={`dash-chip${filter === f.id ? ' is-on' : ''}`}
                    aria-pressed={filter === f.id}
                    onClick={() => setFilter(f.id)}
                  >
                    {f.label} <span className="dash-chip-n">{formatCount(totals.byFilter[f.id])}</span>
                  </button>
                ))}
              </div>
              <label className="dash-search">
                <Search size={14} aria-hidden />
                <input
                  type="search" value={search} onChange={(e) => setSearch(e.target.value)}
                  placeholder="Find a table" aria-label="Find a table"
                />
              </label>
            </div>

            <div className="dash-table-wrap">
              <table className="dash-table">
                <thead>
                  <tr>
                    <th>Source table</th>
                    <th className="num">Rows</th>
                    <th>Destination table</th>
                    <th className="num">Rows</th>
                    <th className="num">Difference</th>
                    <th>Status</th>
                  </tr>
                </thead>
                <tbody>
                  {visible.map((p) => (
                    <tr key={`${p.source?.table ?? ''}|${p.destination?.table ?? ''}`}>
                      <td className="dash-name">{p.source?.table ?? <span className="dash-muted">—</span>}</td>
                      <CountCell
                        count={p.source} canSync={canSync && !sync.source.running}
                        busy={busyRow === `source:${p.source?.table}`}
                        onRecount={() => p.source && recountOne('source', p.source.table)}
                      />
                      <td className="dash-name">{p.destination?.table ?? <span className="dash-muted">—</span>}</td>
                      <CountCell
                        count={p.destination} canSync={canSync && !sync.destination.running}
                        busy={busyRow === `destination:${p.destination?.table}`}
                        onRecount={() => p.destination && recountOne('destination', p.destination.table)}
                      />
                      <td className={`num ${p.difference ? 'dash-bad' : ''}`}>
                        {p.difference == null ? '' : `${p.difference > 0 ? '+' : p.difference < 0 ? '−' : ''}${formatCount(Math.abs(p.difference))}`}
                      </td>
                      <td>
                        <span className={`dash-status tone-${STATUS[p.status].tone}`} title={STATUS[p.status].hint}>
                          {STATUS[p.status].label}
                        </span>
                      </td>
                    </tr>
                  ))}
                  {visible.length === 0 && (
                    <tr><td colSpan={6} className="dash-empty">No tables match.</td></tr>
                  )}
                </tbody>
              </table>
            </div>
          </>
        )}
      </section>
    </div>
  );
}

function Summary({ summary }: { summary: DashboardSummary | null }) {
  const last = summary?.lastRun;
  return (
    <div className="dash-cards">
      <Link to="/jobs" className="card dash-card">
        <span className="dash-card-label">Runs in progress</span>
        <span className="dash-card-value">{summary ? formatCount(summary.runsInProgress) : '…'}</span>
        <span className="dash-card-sub">waiting, running or paused</span>
      </Link>
      <div className="card dash-card">
        <span className="dash-card-label">Copied in the last 24 h</span>
        <span className="dash-card-value">{summary ? formatBytes(summary.copiedBytes24h) : '…'}</span>
        <span className="dash-card-sub">{summary ? `${formatCount(summary.copiedRows24h)} rows` : ''}</span>
      </div>
      <div className="card dash-card">
        <span className="dash-card-label">Speed now</span>
        <span className="dash-card-value dash-accent">{summary ? `${summary.mbPerSecond.toFixed(1)} MB/s` : '…'}</span>
        <span className="dash-card-sub">{summary ? `${formatCount(Math.round(summary.rowsPerSecond))} rows/s` : ''}</span>
      </div>
      {last ? (
        <Link to={`/jobs/${last.id}`} className="card dash-card">
          <span className="dash-card-label">Last run</span>
          <span className="dash-card-value dash-card-run">
            #{last.id} <span style={{ color: statusColor(last.status) }}>{statusLabel(last.status)}</span>
          </span>
          <span className="dash-card-sub">
            {runKindLabel(last.kind)} · {last.application} · {formatAgo(last.completedAt ?? last.startedAt ?? last.createdAt)}
          </span>
        </Link>
      ) : (
        <div className="card dash-card">
          <span className="dash-card-label">Last run</span>
          <span className="dash-card-value">{summary ? 'None yet' : '…'}</span>
        </div>
      )}
    </div>
  );
}

function SidePicker(props: {
  label: string;
  side: Side;
  choice: Choice;
  databases: any[];
  schemas: string[];
  disabled: boolean;
  onChange: (patch: Partial<Choice>) => void;
}) {
  const { label, side, choice, databases, schemas, disabled, onChange } = props;
  // Keep a remembered schema selectable even before the live list has loaded.
  const options = choice.schema && !schemas.includes(choice.schema) ? [choice.schema, ...schemas] : schemas;
  return (
    <fieldset className="dash-side" disabled={disabled}>
      <legend>{label}</legend>
      <select
        aria-label={`${label} database`}
        value={choice.connectionId ?? ''}
        onChange={(e) => onChange({ connectionId: e.target.value ? Number(e.target.value) : null, schema: '' })}
      >
        <option value="">{side === 'source' ? 'Oracle database…' : 'PostgreSQL database…'}</option>
        {databases.map((d) => <option key={d.id} value={d.id}>{d.name}</option>)}
      </select>
      <select
        aria-label={`${label} schema`}
        value={choice.schema}
        disabled={!choice.connectionId}
        onChange={(e) => onChange({ schema: e.target.value })}
      >
        <option value="">Schema…</option>
        {options.map((s) => <option key={s} value={s}>{s}</option>)}
      </select>
    </fieldset>
  );
}

function CountCell({ count, canSync, busy, onRecount }: {
  count: TableCount | null;
  canSync: boolean;
  busy: boolean;
  onRecount: () => void;
}) {
  if (!count) return <td className="num" />;
  const when = count.countedAt ? new Date(count.countedAt).toLocaleString() : '';
  return (
    <td className="num">
      <div className="dash-count">
        <span className="dash-count-n">
          {count.rows == null ? <span className="dash-muted">Not counted</span> : formatCount(count.rows)}
        </span>
        {canSync && (
          <button
            className="dash-icon-btn"
            onClick={onRecount}
            disabled={busy}
            title={`Count ${count.table} again`}
            aria-label={`Count ${count.table} again`}
          >
            <RefreshCw size={13} className={busy ? 'spin' : undefined} />
          </button>
        )}
      </div>
      {count.error ? (
        <div className="dash-count-sub dash-bad" title={count.error}>{count.error}</div>
      ) : count.countedAt ? (
        <div className="dash-count-sub" title={`Counted ${when}${count.durationMs != null ? `, took ${formatDuration(count.durationMs)}` : ''}`}>
          {formatAgo(count.countedAt)}
        </div>
      ) : null}
    </td>
  );
}
