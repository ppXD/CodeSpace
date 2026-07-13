import { useCallback, useSyncExternalStore } from "react";

/**
 * A single shared 1-second clock. Every time display in the app (run canvas elapsed, Session Room
 * relative ages, countdowns) subscribes to ONE module-level `setInterval` per interval length —
 * instead of each component spinning its own — so they all tick in lockstep and the page never runs
 * N drifting timers. The interval starts on the first subscriber and is cleared on the last
 * unsubscribe, keyed by `intervalMs` so distinct cadences (a 1s tick, a 250ms tick) don't collide.
 */
type Clock = {
  /** Subscribers React registered via `useSyncExternalStore`; notified on each tick. */
  subscribers: Set<() => void>;
  /** The live `setInterval` handle, or null when no one is subscribed (so no timer runs). */
  timer: ReturnType<typeof setInterval> | null;
  /** The latest `Date.now()`, captured ONLY on tick so `getSnapshot` is stable between ticks. */
  now: number;
};

const clocks = new Map<number, Clock>();

/** The clock for a given interval, created on first use. */
function getClock(intervalMs: number): Clock {
  let clock = clocks.get(intervalMs);

  if (!clock) {
    clock = { subscribers: new Set(), timer: null, now: Date.now() };
    clocks.set(intervalMs, clock);
  }

  return clock;
}

/** Subscribe to the shared clock — starts the interval on the first subscriber, clears it on the last unsubscribe. */
function subscribe(intervalMs: number, onChange: () => void): () => void {
  const clock = getClock(intervalMs);
  clock.subscribers.add(onChange);

  if (clock.timer === null) {
    clock.timer = setInterval(() => {
      clock.now = Date.now();
      for (const cb of clock.subscribers) cb();
    }, intervalMs);
  }

  return () => {
    clock.subscribers.delete(onChange);

    if (clock.subscribers.size === 0 && clock.timer !== null) {
      clearInterval(clock.timer);
      clock.timer = null;
    }
  };
}

/**
 * Subscribe to the shared 1-second clock and re-render on each tick. Returns the latest `Date.now()`
 * captured on tick — stable between ticks, so a component reading it re-renders once per second, not
 * every render. All callers with the same `intervalMs` share ONE interval.
 */
export function useNowTick(intervalMs = 1000): number {
  const subscribeToClock = useCallback((onChange: () => void) => subscribe(intervalMs, onChange), [intervalMs]);
  const getSnapshot = useCallback(() => getClock(intervalMs).now, [intervalMs]);

  return useSyncExternalStore(subscribeToClock, getSnapshot, getSnapshot);
}

/** Format an elapsed duration: `0:42`, `12:34`, and `1h 04m` past an hour. Negative / NaN → `0:00`. */
export function formatElapsed(ms: number): string {
  if (!Number.isFinite(ms) || ms < 0) return "0:00";

  const totalSeconds = Math.floor(ms / 1000);
  const hours = Math.floor(totalSeconds / 3600);
  const minutes = Math.floor((totalSeconds % 3600) / 60);
  const seconds = totalSeconds % 60;

  if (hours > 0) return `${hours}h ${String(minutes).padStart(2, "0")}m`;

  return `${minutes}:${String(seconds).padStart(2, "0")}`;
}

/** Format the time remaining until a deadline — `max(0, deadline - now)`, then the same shape as elapsed. */
export function formatCountdown(deadlineMs: number, nowMs: number): string {
  return formatElapsed(Math.max(0, deadlineMs - nowMs));
}

/** TEST ONLY — the number of shared clocks with a live `setInterval`, so tests can assert dedup. */
export function __activeTimerCountForTest(): number {
  let count = 0;

  for (const clock of clocks.values()) if (clock.timer !== null) count += 1;

  return count;
}
