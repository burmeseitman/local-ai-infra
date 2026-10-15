import { useCallback, useEffect, useRef, useState } from "react";
import { ApiError, isAbortError } from "../api/http";

export interface AsyncResource<T> {
  data: T | null;
  error: ApiError | null;
  /** True only until the first response (success or failure) arrives. */
  initialLoading: boolean;
  refreshing: boolean;
  lastUpdated: number | null;
  reload: (force?: boolean) => Promise<void>;
}

/**
 * Loads a resource on mount and optionally re-polls it. Keeps the last good data
 * while a refresh is in flight or after a failure, so the UI never flashes empty.
 */
export function useAsyncResource<T>(
  load: (force: boolean, signal: AbortSignal) => Promise<T>,
  pollIntervalMs?: number,
): AsyncResource<T> {
  const [data, setData] = useState<T | null>(null);
  const [error, setError] = useState<ApiError | null>(null);
  const [initialLoading, setInitialLoading] = useState(true);
  const [refreshing, setRefreshing] = useState(false);
  const [lastUpdated, setLastUpdated] = useState<number | null>(null);
  const controllerRef = useRef<AbortController | null>(null);
  const loadRef = useRef(load);

  useEffect(() => {
    loadRef.current = load;
  }, [load]);

  // Performs no synchronous state updates, so it is safe to start from an effect.
  const fetchData = useCallback(async (force: boolean) => {
    controllerRef.current?.abort();
    const controller = new AbortController();
    controllerRef.current = controller;
    try {
      const result = await loadRef.current(force, controller.signal);
      setData(result);
      setError(null);
      setLastUpdated(Date.now());
    } catch (caught) {
      if (isAbortError(caught)) {
        return;
      }
      setError(
        caught instanceof ApiError
          ? caught
          : new ApiError("Unexpected error while loading data.", "client_error", null, true),
      );
    } finally {
      if (controllerRef.current === controller) {
        controllerRef.current = null;
        setRefreshing(false);
        setInitialLoading(false);
      }
    }
  }, []);

  /** User-initiated refresh: also drives the `refreshing` indicator. */
  const reload = useCallback(
    async (force = false) => {
      setRefreshing(true);
      await fetchData(force);
    },
    [fetchData],
  );

  useEffect(() => {
    void fetchData(false);
    if (!pollIntervalMs) {
      return () => controllerRef.current?.abort();
    }
    const timer = window.setInterval(() => {
      if (document.visibilityState === "visible") {
        void fetchData(false);
      }
    }, pollIntervalMs);
    return () => {
      window.clearInterval(timer);
      controllerRef.current?.abort();
    };
  }, [fetchData, pollIntervalMs]);

  return { data, error, initialLoading, refreshing, lastUpdated, reload };
}
