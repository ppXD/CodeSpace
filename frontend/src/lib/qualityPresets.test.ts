import { describe, expect, it } from "vitest";

import { presetOf, QUALITY_PRESETS } from "./qualityPresets";

describe("qualityPresets", () => {
  it("every preset matches its own config", () => {
    for (const p of QUALITY_PRESETS) expect(presetOf(p.config)).toBe(p.id);
  });

  it("the composer's default state IS the Prototype preset", () => {
    // The launch composer opens with everything off — that must read as "Prototype" (an active pill), not "Custom".
    expect(presetOf({ requirePlanConfirmation: false, plannerReview: "None", outputReview: "None", reviewerAgent: false, reviseRounds: "", decisionReview: "None" }))
      .toBe("prototype");
  });

  it("hand-editing any knob turns the combination Custom", () => {
    for (const p of QUALITY_PRESETS) {
      expect(presetOf({ ...p.config, plannerReview: p.config.plannerReview === "Gate" ? "Improve" : "Gate" })).toBeNull();
      expect(presetOf({ ...p.config, reviewerAgent: !p.config.reviewerAgent })).toBeNull();
      expect(presetOf({ ...p.config, requirePlanConfirmation: !p.config.requirePlanConfirmation })).toBeNull();
    }
  });

  it("preset ids and pill labels are pinned", () => {
    expect(QUALITY_PRESETS.map(p => p.id)).toEqual(["prototype", "delivery", "unattended"]);
    expect(QUALITY_PRESETS.map(p => p.label)).toEqual(["Prototype", "Delivery", "Unattended"]);
  });

  it("each preset's tier matches the backend QualityTier wire name exactly (PascalCase)", () => {
    // P3.2: sent verbatim as LaunchTaskInput.tier — a typo here would silently misreport the tier to the backend.
    expect(QUALITY_PRESETS.map(p => p.tier)).toEqual(["Prototype", "Delivery", "Unattended"]);
  });

  it("the ladder of rigor is monotone — Delivery gates for a human, Unattended closes the loop itself", () => {
    const delivery = QUALITY_PRESETS[1].config;
    const unattended = QUALITY_PRESETS[2].config;

    expect(delivery.requirePlanConfirmation).toBe(true);
    expect(delivery.plannerReview).toBe("Gate");
    expect(delivery.outputReview).toBe("Gate");

    expect(unattended.plannerReview).toBe("Improve");
    expect(unattended.outputReview).toBe("Improve");
    expect(unattended.reviewerAgent).toBe(true);
    expect(unattended.reviseRounds).toBe("2");
    expect(unattended.decisionReview).toBe("Gate");
  });
});
