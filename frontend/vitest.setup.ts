/**
 * Global setup for every vitest run. Imports the jest-dom matchers so specs can
 * use {@link expect.toBeInTheDocument} / etc.; clears localStorage between tests
 * so cross-test bleed-through (auth header / team header injection state) can't
 * silently green a regression.
 */
import "@testing-library/jest-dom/vitest";
import { afterEach, beforeEach } from "vitest";
import { cleanup } from "@testing-library/react";

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
