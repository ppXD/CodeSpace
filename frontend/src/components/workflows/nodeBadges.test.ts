import { describe, expect, it } from "vitest";

import { nodeBadges } from "./nodeIcon";

describe("nodeBadges", () => {
  it("returns no badges for a plain node (nothing set)", () => {
    expect(nodeBadges({})).toEqual([]);
  });

  it("maps each manifest flag to its badge", () => {
    expect(nodeBadges({ isSideEffecting: true }).map((b) => [b.kind, b.label])).toEqual([["write", "Writes"]]);
    expect(nodeBadges({ canSuspend: true }).map((b) => [b.kind, b.label])).toEqual([["wait", "Waits"]]);
    expect(nodeBadges({ alwaysRequiresApproval: true }).map((b) => [b.kind, b.label])).toEqual([["approval", "Approval"]]);
  });

  it("orders the badges most-consequential first: Approval → Writes → Waits", () => {
    const kinds = nodeBadges({ isSideEffecting: true, canSuspend: true, alwaysRequiresApproval: true }).map((b) => b.kind);
    expect(kinds).toEqual(["approval", "write", "wait"]);
  });
});
