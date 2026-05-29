import { describe, expect, it } from "vitest";

import { avatarColor } from "./avatarColor";

describe("avatarColor", () => {
  it("is deterministic — the same id always maps to the same swatch", () => {
    expect(avatarColor("user-123")).toEqual(avatarColor("user-123"));
  });

  it("returns a bg/fg pair of hex colours", () => {
    const c = avatarColor("anyone");
    expect(c.bg).toMatch(/^#[0-9a-fA-F]{6}$/);
    expect(c.fg).toMatch(/^#[0-9a-fA-F]{6}$/);
  });

  it("spreads different ids across more than one swatch", () => {
    const ids = Array.from({ length: 12 }, (_, i) => `member-${i}`);
    const distinct = new Set(ids.map(id => avatarColor(id).bg));
    expect(distinct.size).toBeGreaterThan(1);
  });
});
