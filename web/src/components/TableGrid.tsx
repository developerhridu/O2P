import { useEffect, useRef, type ReactNode } from 'react';
import { useVirtualizer } from '@tanstack/react-virtual';
import { ArrowDown, ArrowUp, ArrowUpDown, X } from 'lucide-react';
import { formatBytes, formatCount, formatRows, formatSize, UNKNOWN_LABEL } from '../format';

export type GridRow = {
  key: string;
  owner: string;
  tableName: string;
  included: boolean;
  whereClause: string;
  estRows: number | null;
  estBytes: number | null;
  sizeIsEstimate?: boolean;
  hasLobs: boolean;
  columns: unknown[];
};

export type SortKey = 'owner' | 'tableName' | 'estRows' | 'estBytes';
export type SortState = { key: SortKey; dir: 'asc' | 'desc' } | null;

const ROW_HEIGHT = 44;
const HEADER_HEIGHT = 40;

type Props = {
  /** Already filtered and sorted by the caller. */
  rows: GridRow[];
  sort: SortState;
  onSort: (key: SortKey) => void;
  /** Changes whenever the visible set is re-derived (search, status filter, sort) so the grid scrolls back to the top. */
  resetKey: string;
  onToggle: (key: string) => void;
  /** Applies to the rows currently shown, not to every row. */
  onToggleAll: (next: boolean) => void;
  onWhere: (key: string, value: string) => void;
  onRemove: (key: string) => void;
  empty: ReactNode;
  stats: {
    selected: number;
    total: number;
    rows: number;
    rowsUnknown: number;
    bytes: number;
    bytesUnknown: number;
    withFilter: number;
  };
};

export default function TableGrid({
  rows,
  sort,
  onSort,
  resetKey,
  onToggle,
  onToggleAll,
  onWhere,
  onRemove,
  empty,
  stats,
}: Props) {
  const scrollRef = useRef<HTMLDivElement>(null);
  const allRef = useRef<HTMLInputElement>(null);

  const virtualizer = useVirtualizer({
    count: rows.length,
    getScrollElement: () => scrollRef.current,
    estimateSize: () => ROW_HEIGHT,
    overscan: 12,
    // The sticky header sits above the first row inside the same scroll element, so the list
    // starts HEADER_HEIGHT px into the scroll content. Without this the visible range is
    // computed one row off.
    scrollMargin: HEADER_HEIGHT,
  });

  useEffect(() => {
    virtualizer.scrollToOffset(0);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [resetKey]);

  const selectedShown = rows.reduce((n, r) => n + (r.included ? 1 : 0), 0);
  const allShown = rows.length > 0 && selectedShown === rows.length;
  const someShown = selectedShown > 0 && !allShown;

  // `indeterminate` has no HTML attribute; it can only be set on the DOM node.
  useEffect(() => {
    if (allRef.current) allRef.current.indeterminate = someShown;
  }, [someShown]);

  const ariaSort = (key: SortKey): 'ascending' | 'descending' | 'none' =>
    sort?.key === key ? (sort.dir === 'asc' ? 'ascending' : 'descending') : 'none';

  // A function returning JSX, not a component: a component declared inside render is a new type on
  // every render, so React would remount the button each time and drop keyboard focus.
  const sortHeader = (k: SortKey, label: string, className?: string) => {
    const active = sort?.key === k;
    const Icon = !active ? ArrowUpDown : sort?.dir === 'asc' ? ArrowUp : ArrowDown;
    return (
      <div key={k} className={`tb-cell ${className ?? ''}`} role="columnheader" aria-sort={ariaSort(k)}>
        <button type="button" className="tb-sort" data-active={active} onClick={() => onSort(k)}>
          {label}
          <Icon size={13} className="tb-sort-icon" aria-hidden="true" />
        </button>
      </div>
    );
  };

  const items = virtualizer.getVirtualItems();

  return (
    <div className="tb-grid-card">
      <div className="tb-scroll" ref={scrollRef}>
        <div className="tb-grid" role="table" aria-label="Tables in this selection" aria-rowcount={rows.length + 1} aria-colcount={8}>
          <div className="tb-row tb-head" role="row" aria-rowindex={1}>
            <div className="tb-cell tb-cell-check" role="columnheader">
              <input
                ref={allRef}
                type="checkbox"
                className="tb-checkbox"
                checked={allShown}
                disabled={rows.length === 0}
                onChange={() => onToggleAll(!allShown)}
                aria-label={
                  allShown
                    ? `Deselect all ${rows.length} shown tables`
                    : `Select all ${rows.length} shown tables`
                }
              />
            </div>
            {sortHeader('owner', 'Schema')}
            {sortHeader('tableName', 'Table')}
            {sortHeader('estRows', 'Rows (approx.)', 'tb-num')}
            {sortHeader('estBytes', 'Size', 'tb-num')}
            <div className="tb-cell tb-cell-center" role="columnheader">Large objects</div>
            <div className="tb-cell" role="columnheader">Row filter</div>
            <div className="tb-cell" role="columnheader" aria-label="Actions" />
          </div>

          {rows.length === 0 ? (
            <div role="row">{empty}</div>
          ) : (
            <div className="tb-body" style={{ height: virtualizer.getTotalSize() }}>
              {items.map((item) => {
                const row = rows[item.index];
                return (
                  <div
                    key={row.key}
                    role="row"
                    aria-rowindex={item.index + 2}
                    className={`tb-row tb-vrow ${row.included ? 'is-selected' : 'is-off'}`}
                    style={{
                      height: item.size,
                      transform: `translateY(${item.start - HEADER_HEIGHT}px)`,
                    }}
                  >
                    <div className="tb-cell tb-cell-check" role="cell">
                      <input
                        type="checkbox"
                        className="tb-checkbox"
                        checked={row.included}
                        onChange={() => onToggle(row.key)}
                        aria-label={`Include ${row.tableName}`}
                      />
                    </div>
                    <div className="tb-cell tb-cell-mono" role="cell" title={row.owner}>{row.owner}</div>
                    <div className="tb-cell tb-cell-name" role="cell" title={row.tableName}>
                      {row.tableName}
                      {row.columns.length === 0 && <span className="tb-badge tb-badge-warn">No column info</span>}
                    </div>
                    <div
                      className={`tb-cell tb-num ${row.estRows == null ? 'tb-unknown' : ''}`}
                      role="cell"
                      title={row.estRows == null ? 'Oracle has no statistics for this table. Use "Get exact counts" to count it.' : undefined}
                    >
                      {formatRows(row.estRows)}
                    </div>
                    <div
                      className={`tb-cell tb-num ${row.estBytes == null ? 'tb-unknown' : ''}`}
                      role="cell"
                      title={
                        row.estBytes == null
                          ? 'No size could be read for this table.'
                          : row.sizeIsEstimate
                            ? 'Estimated from Oracle statistics, not read from a segments view.'
                            : undefined
                      }
                    >
                      {formatSize(row.estBytes, row.sizeIsEstimate)}
                    </div>
                    <div className="tb-cell tb-cell-center" role="cell">
                      {row.hasLobs && <span className="tb-badge tb-badge-lob">LOB</span>}
                    </div>
                    <div className="tb-cell" role="cell">
                      <input
                        type="text"
                        className="tb-cell-input"
                        placeholder="e.g. YEAR = 2024"
                        disabled={!row.included}
                        value={row.whereClause}
                        onChange={(e) => onWhere(row.key, e.target.value)}
                        aria-label={`Row filter for ${row.tableName}`}
                      />
                    </div>
                    <div className="tb-cell tb-cell-center" role="cell">
                      <button
                        type="button"
                        className="tb-icon-btn"
                        onClick={() => onRemove(row.key)}
                        aria-label={`Remove ${row.tableName} from selection`}
                        title="Remove from selection"
                      >
                        <X size={16} />
                      </button>
                    </div>
                  </div>
                );
              })}
            </div>
          )}
        </div>
      </div>

      <div className="tb-footer">
        <span>
          <strong className="tb-accent">{formatCount(stats.selected)}</strong> selected of {formatCount(stats.total)}
        </span>
        <span>
          Rows <strong>{formatCount(stats.rows)}</strong>
          {stats.rowsUnknown > 0 && <span className="tb-unknown"> · {formatCount(stats.rowsUnknown)} {UNKNOWN_LABEL.toLowerCase()}</span>}
        </span>
        <span>
          Size <strong>{formatBytes(stats.bytes)}</strong>
          {stats.bytesUnknown > 0 && <span className="tb-unknown"> · {formatCount(stats.bytesUnknown)} {UNKNOWN_LABEL.toLowerCase()}</span>}
        </span>
        {stats.withFilter > 0 && (
          <span>
            <strong>{formatCount(stats.withFilter)}</strong> with a row filter
          </span>
        )}
      </div>
    </div>
  );
}
