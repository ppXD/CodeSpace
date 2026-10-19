import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { formatRelative } from "./formatRelative";

describe("formatRelative", () => {
  const now = new Date("2026-10-19T12:00:00Z");
  const before = (seconds: number) => new Date(now.getTime() - seconds * 1000).toISOString();

  beforeEach(() => { vi.useFakeTimers(); vi.setSystemTime(now); });
  afterEach(() => { vi.useRealTimers(); });

  it.each([
    [0, "just now"],
    [59, "just now"],
    [60, "1 min ago"],
    [3599, "59 min ago"],
    [3600, "1 hr ago"],
    [86399, "23 hr ago"],
    [86400, "1 d ago"],
    [21 * 86400, "21 d ago"],
  ])("reads %i seconds ago as '%s'", (seconds, expected) => {
    expect(formatRelative(before(seconds))).toBe(expected);
  });

  it("reads a time slightly in the future (clock skew with the server) as just now, never as negative", () => {
    expect(formatRelative(new Date(now.getTime() + 5000).toISOString())).toBe("just now");
  });
});
