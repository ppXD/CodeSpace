/** The QUALITY dial of the launch composer — the six review/verification knobs the three intent presets set as a
 *  group (plan confirm + plan critic + output critic + reviewer kind + revise rounds + decision critic). A preset is
 *  a NAMED combination, not a new wire field: applying one just writes these knobs through the normal config state;
 *  hand-editing any knob afterwards makes the combination read "Custom" (presetOf returns null). Pure — vitest-pinned. */
export interface QualityConfig {
  requirePlanConfirmation: boolean;
  plannerReview: string;
  outputReview: string;
  reviewerAgent: boolean;
  reviseRounds: string;
  decisionReview: string;
}

export interface QualityPreset {
  id: string;
  label: string;
  /** One-sentence "when to pick this" — the pill's tooltip. */
  hint: string;
  config: QualityConfig;
}

export const QUALITY_PRESETS: QualityPreset[] = [
  {
    id: "prototype",
    label: "Prototype",
    hint: "No reviews — fastest and cheapest. You eyeball the result yourself.",
    config: { requirePlanConfirmation: false, plannerReview: "None", outputReview: "None", reviewerAgent: false, reviseRounds: "", decisionReview: "None" },
  },
  {
    id: "delivery",
    label: "Delivery",
    hint: "You stay in the loop: the plan parks for your approval, and a weak result is flagged for you — never silently consumed.",
    config: { requirePlanConfirmation: true, plannerReview: "Gate", outputReview: "Gate", reviewerAgent: false, reviseRounds: "", decisionReview: "None" },
  },
  {
    id: "unattended",
    label: "Unattended",
    hint: "No human in the loop: critiques feed back and the work revises itself, an independent agent re-checks the result, and only a hard block escalates to you.",
    config: { requirePlanConfirmation: false, plannerReview: "Improve", outputReview: "Improve", reviewerAgent: true, reviseRounds: "2", decisionReview: "Gate" },
  },
];

/** The preset a config currently matches, or null when the knobs are a custom mix. */
export function presetOf(cfg: QualityConfig): string | null {
  const match = QUALITY_PRESETS.find(p =>
    p.config.requirePlanConfirmation === cfg.requirePlanConfirmation
    && p.config.plannerReview === cfg.plannerReview
    && p.config.outputReview === cfg.outputReview
    && p.config.reviewerAgent === cfg.reviewerAgent
    && p.config.reviseRounds === cfg.reviseRounds
    && p.config.decisionReview === cfg.decisionReview);
  return match?.id ?? null;
}
