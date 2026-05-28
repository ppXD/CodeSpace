import { describe, expect, it } from "vitest";

import { dayDividerLabel, firstUnreadId, isNewDay } from "./messageDividers";

describe("isNewDay", () => {
  // Local-time literals (no trailing Z): the divider keys off the viewer's local calendar day,
  // matching how message timestamps are rendered, so the boundary is timezone-independent here.
  it("is false for two times on the same calendar day", () => {
    expect(isNewDay("2026-05-28T01:00:00", "2026-05-28T23:00:00")).toBe(false);
  });

  it("is true across a day boundary", () => {
    expect(isNewDay("2026-05-27T23:00:00", "2026-05-28T01:00:00")).toBe(true);
  });
});

describe("dayDividerLabel", () => {
  const now = new Date("2026-05-28T12:00:00");

  it("labels the current day Today", () => {
    expect(dayDividerLabel("2026-05-28T08:00:00", now)).toBe("Today");
  });

  it("labels the previous day Yesterday", () => {
    expect(dayDividerLabel("2026-05-27T08:00:00", now)).toBe("Yesterday");
  });

  it("omits the year for an earlier day this year", () => {
    const label = dayDividerLabel("2026-03-05T08:00:00", now);
    expect(label).not.toBe("Today");
    expect(label).not.toBe("Yesterday");
    expect(label).not.toContain("2026");
    expect(label).toContain("5");
  });

  it("qualifies an older day with its year", () => {
    expect(dayDividerLabel("2020-08-02T08:00:00", now)).toContain("2020");
  });
});

describe("firstUnreadId", () => {
  // UUID v7 ids sort lexicographically by time; single letters stand in for that ordering here.
  const messages = [
    { id: "m1", authorUserId: "other" },
    { id: "m2", authorUserId: "me" },
    { id: "m3", authorUserId: "other" },
    { id: "m4", authorUserId: "other" },
  ];

  it("returns null when the caller has never read anything", () => {
    expect(firstUnreadId(messages, null, "me")).toBeNull();
  });

  it("returns null when the cursor is already at the newest message", () => {
    expect(firstUnreadId(messages, "m4", "me")).toBeNull();
  });

  it("returns the first message past the cursor that the caller didn't write", () => {
    // m2 is past m1 but it's the caller's own → skipped; m3 is the first other-authored unread.
    expect(firstUnreadId(messages, "m1", "me")).toBe("m3");
  });

  it("ignores the caller's own messages when finding the boundary", () => {
    const ownTail = [
      { id: "m1", authorUserId: "other" },
      { id: "m2", authorUserId: "me" },
      { id: "m3", authorUserId: "me" },
    ];
    expect(firstUnreadId(ownTail, "m1", "me")).toBeNull();
  });
});
