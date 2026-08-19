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
 * Which layer supplies one DEPENDS ON THE NODE VERSION, and that is the whole
 * problem. happy-dom's Window carries a `localStorage`, and on node 22 — the
 * version .github/workflows/frontend.yml pins — it reaches `globalThis` and every
 * spec runs. On node 26, `globalThis` already has node's own experimental
 * `localStorage`: a configurable getter yielding `undefined` unless node was
 * started with `--localstorage-file`, which also prints an ExperimentalWarning
 * merely for being read. happy-dom's value never lands, and since request.ts /
 * client.ts read `localStorage` directly and the `beforeEach` below clears it,
 * all 1728 specs died before any body ran.
 *
 * So this is not a suite that was broken for everyone — it is a suite that CI
 * cannot verify is runnable on the node its developers actually have. Installing
 * storage here makes it runnable on both, and independent of the next change to
 * either layer.
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
