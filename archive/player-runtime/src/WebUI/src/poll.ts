import { useEffect, useState } from 'react';
import type { Snapshot } from './types';

// One request at a time; only complete server snapshots become visible.
export function useSnapshot<T extends Snapshot>(read: (signal: AbortSignal) => Promise<T>) {
  const [view, setView] = useState<T | null>(null);
  const [connectionError, setConnectionError] = useState(false);
  useEffect(() => {
    const controller = new AbortController();
    let timer: ReturnType<typeof setTimeout>;
    async function poll() {
      try {
        const next = await read(controller.signal);
        if (controller.signal.aborted) return;
        setView(previous => previous?.runId === next.runId && previous.viewRevision > next.viewRevision ? previous : next);
        setConnectionError(false);
      } catch {
        if (!controller.signal.aborted) setConnectionError(true);
      } finally {
        if (!controller.signal.aborted) timer = setTimeout(poll, 350);
      }
    }
    void poll();
    return () => { controller.abort(); clearTimeout(timer); };
  }, [read]);
  return { view, connectionError };
}

export async function getJson<T>(url: string, signal: AbortSignal): Promise<T> {
  const response = await fetch(url, { signal, cache: 'no-store' });
  if (!response.ok) throw new Error(`HTTP ${response.status}`);
  return response.json() as Promise<T>;
}
