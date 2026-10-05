/**
 * What a figure reads as when we genuinely do not know it. Never a dash and never 0: Oracle only
 * knows a table's row count once DBMS_STATS has gathered statistics, and showing 0 for that made a
 * never-analysed table look empty.
 */
export const UNKNOWN_LABEL = 'Unknown';

/** Number with thousands separators, in the viewer's locale. */
export function formatCount(n: number): string {
  return n.toLocaleString();
}

/** Row count, or "Unknown" when Oracle has no statistic for the table. */
export function formatRows(n: number | null | undefined): string {
  return n == null ? UNKNOWN_LABEL : n.toLocaleString();
}

/** Size, or "Unknown" when no size could be read. `~` marks an estimate. */
export function formatSize(bytes: number | null | undefined, isEstimate = false): string {
  if (bytes == null) return UNKNOWN_LABEL;
  return (isEstimate ? '~' : '') + formatBytes(bytes);
}

/** Bytes as a human-readable size, picking the unit that keeps the number small. */
export function formatBytes(bytes: number): string {
  const mb = bytes / 1024 / 1024;
  if (mb >= 1024) return `${(mb / 1024).toFixed(2)} GB`;
  if (mb >= 1) return `${mb.toFixed(2)} MB`;
  if (bytes >= 1024) return `${(bytes / 1024).toFixed(1)} KB`;
  return `${bytes} B`;
}

/** "just now", "5 min ago", "3 h ago", "2 days ago" - for when a figure was taken. */
export function formatAgo(iso: string | null | undefined, now = Date.now()): string {
  if (!iso) return '';
  const seconds = Math.max(0, (now - new Date(iso).getTime()) / 1000);
  if (seconds < 60) return 'just now';
  if (seconds < 3600) return `${Math.floor(seconds / 60)} min ago`;
  if (seconds < 86400) return `${Math.floor(seconds / 3600)} h ago`;
  const days = Math.floor(seconds / 86400);
  return days === 1 ? '1 day ago' : `${days} days ago`;
}

/** A duration in ms as "0.4 s", "12 s", "3 min 5 s". */
export function formatDuration(ms: number | null | undefined): string {
  if (ms == null) return '';
  if (ms < 10_000) return `${(ms / 1000).toFixed(1)} s`;
  const s = Math.round(ms / 1000);
  if (s < 60) return `${s} s`;
  return `${Math.floor(s / 60)} min ${s % 60} s`;
}
