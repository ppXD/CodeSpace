import { describe, expect, it } from "vitest";
import { composePlanFeedback, planAgentStatus, planDepsLabel, planStateIcon, planStateTone, planStateWord } from "./planChecklist";

// The five backend states (WorkPlanItemStates) — the render vocabulary must cover each deliberately, and an
// UNKNOWN state must degrade neutral (open vocabulary: a new backend state reads as "something new", never red).
describe("planChecklist state vocabulary", () => {
  const rows: Array<[state: string, icon: string, tone: string, word: string, agent: string]> = [
    ["Pending", "square", "idle", "pending", "Queued"],
    ["InProgress", "dot", "run", "running", "Running"],
    ["Completed", "square-check", "ok", "done", "Succeeded"],
    ["Failed", "square-x", "err", "failed", "Failed"],
    ["NeedsReview", "alert", "warn", "needs review", "NeedsReview"],
  ];

  it.each(rows)("%s maps deliberately", (state, icon, tone, word, agent) => {
    expect(planStateIcon(state)).toBe(icon);
    expect(planStateTone(state)).toBe(tone);
    expect(planStateWord(state)).toBe(word);
    expect(planAgentStatus(state)).toBe(agent);
  });

  it("an unknown state degrades neutral, never red", () => {
    expect(planStateIcon("Paused")).toBe("square");
    expect(planStateTone("Paused")).toBe("idle");
    expect(planStateWord("Paused")).toBe("paused");
    expect(planAgentStatus("Paused")).toBe("Queued");
  });
});

describe("planDepsLabel", () => {
  it("renders ordinals as reader copy", () => {
    expect(planDepsLabel([1, 3])).toBe("after #1, #3");
    expect(planDepsLabel([2])).toBe("after #2");
  });

  it("is null for an independent item", () => {
    expect(planDepsLabel([])).toBeNull();
    expect(planDepsLabel(null)).toBeNull();
    expect(planDepsLabel(undefined)).toBeNull();
  });
});

describe("composePlanFeedback", () => {
  it("joins question choices and the free-text note as lines", () => {
    expect(composePlanFeedback([{ question: "Which market?", choice: "US" }], "ship behind a flag")).toBe(
      "Which market? → US\nship behind a flag",
    );
  });

  it("is just the note when no questions were answered", () => {
    expect(composePlanFeedback([], "  split step 2  ")).toBe("split step 2");
  });

  it("is empty when nothing was entered — the caller gates on it", () => {
    expect(composePlanFeedback([], "   ")).toBe("");
  });
});
