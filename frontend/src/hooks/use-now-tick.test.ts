import { renderHook } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { __activeTimerCountForTest, formatCountdown, formatElapsed, useNowTick } from "./use-now-tick";

/**
 * The whole point of the shared clock is ONE interval regardless of how many components read the
 * time — so the load-bearing assertions are (a) two subscribers = one live timer, and (b) the last
 * unsubscribe clears it. The count comes from the singleton's own bookkeeping, so it's exact under
 * fake timers.
 */
describe("useNowTick shared clock", () => {
  beforeEach(() => vi.useFakeTimers());
  afterEach(() => vi.useRealTimers());

  it("two subscribers share exactly one active interval", () => {
    const a = renderHook(() => useNowTick(1000));
    const b = renderHook(() => useNowTick(1000));

    expect(__activeTimerCountForTest()).toBe(1);

    a.unmount();
    expect(__activeTimerCountForTest()).toBe(1); // b still subscribed — timer stays

    b.unmount();
    expect(__activeTimerCountForTest()).toBe(0); // last unsubscribe clears it
  });
});

describe("formatElapsed", () => {
  it.each([
    [0, "0:00"],
    [42_000, "0:42"],
    [754_000, "12:34"],
    [3_840_000, "1h 04m"],
    [-5, "0:00"],
    [NaN, "0:00"],
  ])("formats %d ms as %s", (ms, expected) => {
    expect(formatElapsed(ms)).toBe(expected);
  });
});

describe("formatCountdown", () => {
  it("floors a past deadline to 0:00", () => {
    expect(formatCountdown(1_000, 5_000)).toBe("0:00");
  });

  it("counts down a deadline 90s ahead as 1:30", () => {
    const now = 10_000;
    expect(formatCountdown(now + 90_000, now)).toBe("1:30");
  });
});
