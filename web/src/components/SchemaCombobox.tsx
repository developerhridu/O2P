import { useEffect, useId, useMemo, useRef, useState } from 'react';
import { ChevronDown, RefreshCw } from 'lucide-react';
import { fetchSchemas, fetchTargetSchemas } from '../api';
import './SchemaCombobox.css';

type Props = {
  id?: string;
  value: string;
  onChange: (value: string) => void;
  /** The source (Oracle) database to list schemas from. */
  connectionId: number | '';
  disabled?: boolean;
  /** sm = 36px (toolbar), md = 44px (form dialog). */
  size?: 'sm' | 'md';
  className?: string;
  /**
   * Which side this picker is for. 'oracle' lists the source schemas that own readable tables;
   * 'postgres' lists the schemas of a destination database.
   */
  variant?: 'oracle' | 'postgres';
};

/** One row in the list, whichever side it came from. */
type SchemaOption = { name: string; detail: string; muted?: boolean };

type SchemaData = { options: SchemaOption[]; skipped: number };

async function loadOptions(variant: 'oracle' | 'postgres', connectionId: number): Promise<SchemaData> {
  if (variant === 'postgres') {
    const data = await fetchTargetSchemas(connectionId);
    return {
      options: data.schemas.map((s) => ({
        name: s.name,
        // A run only needs CREATE for tables that do not exist yet, so a read-only schema is
        // still offered - just marked, so the choice is informed.
        detail: s.canCreate ? 'can create tables' : 'read-only',
        muted: !s.canCreate,
      })),
      skipped: 0,
    };
  }
  const data = await fetchSchemas(connectionId);
  return {
    options: data.schemas.map((s) => ({
      name: s.name,
      detail: `${s.tableCount.toLocaleString()} ${s.tableCount === 1 ? 'table' : 'tables'}`,
    })),
    skipped: data.skipped,
  };
}

// A stable empty list, so `schemas` keeps the same identity while nothing is loaded.
const NO_SCHEMAS: SchemaOption[] = [];

type Load =
  | { connId: number; status: 'loading' }
  | { connId: number; status: 'error'; message: string }
  | { connId: number; status: 'ready'; data: SchemaData };

/**
 * A schema picker fed from the real source database.
 *
 * It is an editable combobox on purpose, not a strict select: a saved selection can point at a
 * schema that is not in the list, an account may not be able to read the catalog at all, and a
 * schema Oracle marks as system-maintained is filtered out even though it is legitimately
 * wanted. Typing a name that is not listed is always allowed.
 *
 * The list is fetched the first time the box is opened - not on mount - because merely opening a
 * saved selection should not open an Oracle session, possibly against the wrong database.
 */
export default function SchemaCombobox({ id, value, onChange, connectionId, disabled, size = 'sm', className, variant = 'oracle' }: Props) {
  const listId = useId();
  const wrapRef = useRef<HTMLDivElement>(null);
  const inputRef = useRef<HTMLInputElement>(null);

  const [open, setOpen] = useState(false);
  // False until the user types, so opening a box that already holds "HR" shows every schema
  // instead of a list filtered down to the one they already picked.
  const [typed, setTyped] = useState(false);
  const [active, setActive] = useState(-1);
  const [load, setLoad] = useState<Load | null>(null);

  // Successful lists, per database, so going back to a previous one is instant.
  const cache = useRef(new Map<number, SchemaData>());
  // Guards against a slow response for a database the user has since switched away from.
  const requestSeq = useRef(0);

  const current: Load | null = useMemo(() => {
    if (connectionId === '') return null;
    if (load && load.connId === connectionId) return load;
    const cached = cache.current.get(connectionId);
    return cached ? { connId: connectionId, status: 'ready', data: cached } : null;
  }, [load, connectionId]);

  const runLoad = (connId: number) => {
    const seq = ++requestSeq.current;
    setLoad({ connId, status: 'loading' });
    loadOptions(variant, connId)
      .then((data) => {
        cache.current.set(connId, data);
        if (seq === requestSeq.current) setLoad({ connId, status: 'ready', data });
      })
      .catch((err: any) => {
        if (seq === requestSeq.current) {
          setLoad({ connId, status: 'error', message: err?.message || 'Could not load the schemas.' });
        }
      });
  };

  // Load when the box is opened and nothing is loaded for this database yet.
  useEffect(() => {
    if (open && connectionId !== '' && current === null) runLoad(connectionId);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [open, connectionId, current]);

  const reload = () => {
    if (connectionId === '') return;
    cache.current.delete(connectionId);
    runLoad(connectionId);
  };

  const schemas = current?.status === 'ready' ? current.data.options : NO_SCHEMAS;
  const skipped = current?.status === 'ready' ? current.data.skipped : 0;

  const matchesValue = (name: string) =>
    variant === 'oracle' ? name.toUpperCase() === value.trim().toUpperCase() : name === value.trim();

  const filter = typed ? value.trim().toUpperCase() : '';
  const options = useMemo(
    () => (filter ? schemas.filter((s) => s.name.toUpperCase().includes(filter)) : schemas),
    [schemas, filter]
  );

  // Once a list has loaded, a value that is not in it gets a quiet hint. It never blocks or clears.
  const notInList =
    current?.status === 'ready' && value.trim() !== '' && !schemas.some((s) => matchesValue(s.name));

  // Keep the highlighted row in step with the visible options.
  useEffect(() => {
    if (!open) return;
    if (typed) setActive(options.length > 0 ? 0 : -1);
    else setActive(Math.max(0, options.findIndex((o) => matchesValue(o.name))));
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [open, typed, options.length]);

  useEffect(() => {
    if (!open || active < 0) return;
    document.getElementById(`${listId}-opt-${active}`)?.scrollIntoView({ block: 'nearest' });
  }, [open, active, listId]);

  // Close on an outside click.
  useEffect(() => {
    if (!open) return;
    const onMouseDown = (e: MouseEvent) => {
      if (!wrapRef.current?.contains(e.target as Node)) {
        setOpen(false);
        setTyped(false);
      }
    };
    document.addEventListener('mousedown', onMouseDown);
    return () => document.removeEventListener('mousedown', onMouseDown);
  }, [open]);

  const close = () => {
    setOpen(false);
    setTyped(false);
  };

  const pick = (name: string) => {
    onChange(name);
    close();
    inputRef.current?.focus();
  };

  const onKeyDown = (e: React.KeyboardEvent<HTMLInputElement>) => {
    switch (e.key) {
      case 'ArrowDown':
        e.preventDefault();
        if (!open) setOpen(true);
        else setActive((a) => Math.min(a + 1, options.length - 1));
        break;
      case 'ArrowUp':
        e.preventDefault();
        if (!open) setOpen(true);
        else setActive((a) => (a <= 0 ? options.length - 1 : a - 1));
        break;
      case 'Enter':
        if (open) {
          // With a row highlighted, take it. Otherwise this is the type-anyway path: keep the typed
          // text and close. Either way, swallow the Enter so a surrounding form does not submit
          // half-way through choosing; the next Enter, with the list closed, submits as normal.
          e.preventDefault();
          if (active >= 0 && options[active]) pick(options[active].name);
          else close();
        }
        break;
      case 'Escape':
        if (open) {
          e.preventDefault();
          close();
        }
        break;
      case 'Tab':
        close();
        break;
    }
  };

  const statusRow = (() => {
    if (current === null && connectionId !== '') return <div className="sc-row sc-muted">Loading schemas…</div>;
    if (current?.status === 'loading') {
      return (
        <div className="sc-row sc-muted">
          <RefreshCw size={14} className="spin" /> Loading schemas…
        </div>
      );
    }
    if (current?.status === 'error') {
      return (
        <div className="sc-row sc-error" role="alert">
          <span>{current.message}</span>
          <button type="button" className="sc-link" onClick={reload}>
            Retry
          </button>
          <span className="sc-muted">You can still type a schema name.</span>
        </div>
      );
    }
    if (options.length === 0) {
      return (
        <div className="sc-row sc-muted">
          {filter
            ? `No listed schema matches "${filter}". Press Enter to use it anyway.`
            : 'No schemas with readable tables. Type a name to use it anyway.'}
        </div>
      );
    }
    return null;
  })();

  return (
    <div className={`sc-wrap ${className ?? ''}`} ref={wrapRef}>
      <div className="sc-field">
        <input
          ref={inputRef}
          id={id}
          className={`sc-input sc-${size}`}
          type="text"
          role="combobox"
          aria-expanded={open}
          aria-controls={listId}
          aria-autocomplete="list"
          aria-activedescendant={open && active >= 0 ? `${listId}-opt-${active}` : undefined}
          autoComplete="off"
          spellCheck={false}
          disabled={disabled || connectionId === ''}
          placeholder={connectionId === '' ? 'Choose a source database first' : 'Choose or type a schema'}
          value={value}
          onChange={(e) => {
            onChange(variant === 'oracle' ? e.target.value.toUpperCase() : e.target.value);
            setTyped(true);
            setOpen(true);
          }}
          onFocus={() => setOpen(true)}
          onClick={() => setOpen(true)}
          onKeyDown={onKeyDown}
        />
        <button
          type="button"
          className="sc-toggle"
          tabIndex={-1}
          aria-label={open ? 'Hide schemas' : 'Show schemas'}
          disabled={disabled || connectionId === ''}
          // mousedown, not click: keeps focus in the input instead of blurring it first.
          onMouseDown={(e) => {
            e.preventDefault();
            if (open) close();
            else {
              setOpen(true);
              inputRef.current?.focus();
            }
          }}
        >
          <ChevronDown size={16} />
        </button>
      </div>

      {notInList && !open && <div className="sc-hint">Not found in this database</div>}

      {open && connectionId !== '' && (
        <div className="sc-popup">
          {statusRow}
          {options.length > 0 && (
            <ul id={listId} role="listbox" className="sc-list" aria-label="Source schemas">
              {options.map((s, i) => (
                <li
                  key={s.name}
                  id={`${listId}-opt-${i}`}
                  role="option"
                  aria-selected={i === active}
                  className={`sc-option ${i === active ? 'is-active' : ''} ${matchesValue(s.name) ? 'is-current' : ''}`}
                  // mousedown, not click, so the input keeps focus and the popup does not close first.
                  onMouseDown={(e) => {
                    e.preventDefault();
                    pick(s.name);
                  }}
                  onMouseEnter={() => setActive(i)}
                >
                  <span className="sc-name">{s.name}</span>
                  <span className={`sc-count ${s.muted ? 'sc-count-muted' : ''}`}>{s.detail}</span>
                </li>
              ))}
            </ul>
          )}
          {current?.status === 'ready' && (
            <div className="sc-foot">
              <span className="sc-muted">
                {skipped > 0
                  ? `${skipped} ${skipped === 1 ? 'schema' : 'schemas'} hidden — their names can't be scanned yet. `
                  : ''}
                Not listed? Type it and press Enter.
              </span>
              <button type="button" className="sc-link" onMouseDown={(e) => e.preventDefault()} onClick={reload}>
                Reload
              </button>
            </div>
          )}
        </div>
      )}
    </div>
  );
}
