import { describe, expect, it } from "vitest";

import { contradictionChip } from "./agent-contradiction";

describe("contradictionChip", () => {
  it("names an over-claim as a refuted success", () => {
    const chip = contradictionChip("over_claim");

    expect(chip?.tone).toBe("warn");
    expect(chip?.text).toContain("claimed done");
    expect(chip?.title).toMatch(/reported success/i);
  });

  it("names an under-claim as vindicated work, not a failure", () => {
    // D4b: the agent said it couldn't finish, the objective check passed, and the run was kept. The chip has to read
    // as GOOD news — a warn tone here would tell the operator to go look at work that is objectively fine.
    const chip = contradictionChip("under_claim");

    expect(chip?.tone).toBe("ok");
    expect(chip?.text).toContain("check passed");
    expect(chip?.title).toMatch(/objectively fine/i);
  });

  it("renders nothing for an absent or unknown kind rather than leaking a wire token", () => {
    expect(contradictionChip(null)).toBeNull();
    expect(contradictionChip(undefined)).toBeNull();
    expect(contradictionChip("")).toBeNull();
    expect(contradictionChip("sideways_claim")).toBeNull();   // a kind a future backend adds must not render raw
  });
});
