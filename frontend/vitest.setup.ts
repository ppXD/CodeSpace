/**
 * Global setup for every vitest run. Imports the jest-dom matchers so specs can
 * use {@link expect.toBeInTheDocument} / etc.; clears localStorage between tests
 * so cross-test bleed-through (auth header / team header injection state) can't
 * silently green a regression.
 */
import "@testing-library/jest-dom/vitest";
import { afterEach, beforeEach } from "vitest";
import { cleanup } from "@testing-library/react";

/**
 * Web Storage, in memory — installed unconditionally, for the whole suite.
 *
 * Nothing in the stack reliably supplies one. happy-dom's own Window carries a
 * `localStorage`, but under vitest 4 that value does not reach `globalThis` or
 * `window` (`window` and `document` do). What IS on `globalThis` is node's own
 * experimental `localStorage`: a configurable GETTER that yields `undefined`
 * unless node was started with `--localstorage-file`, and that prints an
 * ExperimentalWarning merely for being read. request.ts / client.ts touch
 * `localStorage` directly, so with none present every spec died in the
 * `beforeEach` below before its own body ran — 1728 tests reporting red for one
 * missing global, which reads as a broken suite rather than a missing shim.
 *
 * Defined rather than detected on purpose: probing first would mean reading
 * node's getter (the warning, once per worker), and a test suite wants storage
 * that is deterministic and empty per worker regardless of which layer below it
 * happens to provide one this month.
 */
class MemoryStorage implements Storage {
  private entries = new Map<string, string>();

  get length(): number {
    return this.entries.size;
  }

  key(index: number): string | null {
    return [...this.entries.keys()][index] ?? null;
  }

  getItem(key: string): string | null {
    return this.entries.get(key) ?? null;
  }

  setItem(key: string, value: string): void {
    this.entries.set(key, String(value));
  }

  removeItem(key: string): void {
    this.entries.delete(key);
  }

  clear(): void {
    this.entries.clear();
  }
}

for (const name of ["localStorage", "sessionStorage"] as const) {
  Object.defineProperty(globalThis, name, { value: new MemoryStorage(), configurable: true, writable: true });
}

beforeEach(() => {
  // request.ts / client.ts read JWT + activeTeamId from localStorage on every
  // call. Tests that mutate either must start from a known-empty state.
  localStorage.clear();
});

afterEach(() => {
  // React's auto-unmount avoids "found multiple matching elements" failures when
  // a later test renders the same component.
  cleanup();
});
