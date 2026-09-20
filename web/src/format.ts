/** Number with thousands separators, in the viewer's locale. */
export function formatCount(n: number): string {
  return n.toLocaleString();
}

/** Bytes as a human-readable size, picking the unit that keeps the number small. */
export function formatBytes(bytes: number): string {
  const mb = bytes / 1024 / 1024;
  if (mb >= 1024) return `${(mb / 1024).toFixed(2)} GB`;
  if (mb >= 1) return `${mb.toFixed(2)} MB`;
  if (bytes >= 1024) return `${(bytes / 1024).toFixed(1)} KB`;
  return `${bytes} B`;
}
