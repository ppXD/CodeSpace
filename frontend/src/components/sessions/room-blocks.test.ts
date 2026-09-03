import { describe, expect, it } from "vitest";

import type { RoomBlock } from "@/api/sessions";

import { finalAnswerHeading, partitionForFailureHoist } from "./room-blocks";

const block = (id: string, type: string): RoomBlock => ({ id, type } as unknown as RoomBlock);

describe("partitionForFailureHoist", () => {
  it("pulls the diagnostic out of a failed turn and preserves the rest in original order", () => {
    const blocks = [block("map", "execution_map"), block("grp", "agent_group"), block("diag", "diagnostic"), block("stat", "stat")];

    const { hoisted, rest } = partitionForFailureHoist(blocks);

    expect(hoisted?.id).toBe("diag");                              // the failure card is lifted out
    expect(rest.map((b) => b.id)).toEqual(["map", "grp", "stat"]); // diagnostic removed, everything else stays in place
  });

  it("leaves a turn with no diagnostic unchanged (success / running happy path)", () => {
    const blocks = [block("map", "execution_map"), block("ans", "final_answer")];

    const { hoisted, rest } = partitionForFailureHoist(blocks);

    expect(hoisted).toBeNull();
    expect(rest.map((b) => b.id)).toEqual(["map", "ans"]);   // byte-identical order — nothing hoisted
  });
});

describe("finalAnswerHeading", () => {
  it("names the backend's own reason on a degraded card instead of the vaguer 'Stopped'", () => {
    // A run whose acceptance check failed stops ORDERLY with a confident closing line — "Stopped" would be wrong
    // twice over (nothing stopped it, and the checks are the story). The backend authors the word; we render it.
    expect(finalAnswerHeading({ degraded: true, degradedReason: "Checks failed" })).toBe("Checks failed");
  });

  it("falls back to 'Stopped' for a degrade whose reason the card text already carries", () => {
    expect(finalAnswerHeading({ degraded: true })).toBe("Stopped");
    expect(finalAnswerHeading({ degraded: true, degradedReason: null })).toBe("Stopped");
    expect(finalAnswerHeading({ degraded: true, degradedReason: "  " })).toBe("Stopped");   // blank is not an account
  });

  it("keeps the green 'Result' for a clean success, reason or not", () => {
    expect(finalAnswerHeading({})).toBe("Result");
    expect(finalAnswerHeading({ degraded: false })).toBe("Result");
    expect(finalAnswerHeading({ degraded: false, degradedReason: "Checks failed" })).toBe("Result");   // degraded is the gate
  });
});
