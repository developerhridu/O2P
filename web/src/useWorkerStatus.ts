import { useEffect, useState } from 'react';
import { fetchWorkerStatus, type WorkerStatus } from './api';

/**
 * Polls whether the Worker (shown to users as "the copier") is running.
 *
 * Returns null while unknown - before the first answer, or if the API could not be asked. Callers
 * must treat null as "say nothing": claiming the copier is down because the status check itself
 * failed would be a false alarm about the wrong thing.
 */
export function useWorkerStatus(intervalMs = 10000): WorkerStatus | null {
  const [status, setStatus] = useState<WorkerStatus | null>(null);

  useEffect(() => {
    let alive = true;
    const load = async () => {
      try {
        const s = await fetchWorkerStatus();
        if (alive) setStatus(s);
      } catch {
        if (alive) setStatus(null);
      }
    };
    load();
    const timer = setInterval(load, intervalMs);
    return () => {
      alive = false;
      clearInterval(timer);
    };
  }, [intervalMs]);

  return status;
}
