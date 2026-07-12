import { describe, expect, it } from "vitest";

import type { RoomBlock } from "@/api/sessions";

import { partitionForFailureHoist } from "./room-blocks";

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
