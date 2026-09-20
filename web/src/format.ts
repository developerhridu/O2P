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
