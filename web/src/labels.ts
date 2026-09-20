// Display labels for values that come from (or go to) the API.
//
// IMPORTANT: these map a stored value to the words a user reads. They must never be used in a
// comparison. Status strings drive behaviour - which buttons exist, and the batch progress
// percentage - and slot keys are primary-key data in the database as well as the live-target
// safety gate. Compare raw values; label them only at the point of render.

/** Status values, as stored, mapped to plain words. Covers job, table and batch statuses. */
const STATUS_LABELS: Record<string, string> = {
  Draft: 'Draft',
  Pending: 'Waiting',
  Queued: 'Waiting',
  Creating: 'Preparing',
  Planning: 'Planning',
  Loading: 'Copying',
  Running: 'Running',
  Validating: 'Checking rows',
  Paused: 'Paused',
  Completed: 'Finished',
  CompletedWithErrors: 'Finished with errors',
  Failed: 'Failed',
  Cancelled: 'Cancelled',
  Done: 'Done',
};

/** Falls back to the raw value so an unrecognised status still shows something. */
export function statusLabel(value?: string | null): string {
  if (!value) return '—';
  return STATUS_LABELS[value] ?? value;
}

const STATUS_COLORS: Record<string, string> = {
  Draft: '#94a3b8',
  Pending: '#94a3b8',
  Queued: '#94a3b8',
  Creating: '#a78bfa',
  Planning: '#a78bfa',
  Loading: '#3b82f6',
  Running: '#3b82f6',
  Validating: '#fbbf24',
  Paused: '#fbbf24',
  Completed: '#10b981',
  CompletedWithErrors: '#f59e0b',
  Failed: '#ef4444',
  Cancelled: '#ef4444',
  Done: '#10b981',
};

export function statusColor(value?: string | null): string {
  if (!value) return 'var(--text-secondary)';
  return STATUS_COLORS[value] ?? 'var(--text-secondary)';
}

/**
 * The four database roles a migration can bind. `key` is the stored value and must not change:
 * it is part of the primary key in application_connections, and the server gates the live-target
 * confirmation on the key ending in "live".
 * `kind` matches Connection.kind (0 = Oracle, 1 = PostgreSQL).
 */
export const SLOTS = [
  { key: 'oracle_test', label: 'Source · Test', kind: 0 },
  { key: 'oracle_live', label: 'Source · Live', kind: 0 },
  { key: 'pg_test', label: 'Target · Test', kind: 1 },
  { key: 'pg_live', label: 'Target · Live', kind: 1 },
] as const;

export function slotLabel(key?: string | null): string {
  if (!key) return '—';
  return SLOTS.find((s) => s.key === key)?.label ?? key;
}

/** Job command tokens, so a raw token like `retry_failed` never reaches the screen. */
const COMMAND_LABELS: Record<string, string> = {
  launch: 'start the run',
  pause: 'pause the run',
  resume: 'resume the run',
  cancel: 'cancel the run',
  retry_failed: 'retry the failed tables',
};

export function commandLabel(command: string): string {
  return COMMAND_LABELS[command] ?? command;
}

/** Connection.kind is sent as a number. */
export function databaseTypeLabel(kind: number): string {
  return kind === 0 ? 'Oracle' : 'PostgreSQL';
}
