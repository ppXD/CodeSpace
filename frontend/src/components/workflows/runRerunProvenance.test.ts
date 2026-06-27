import { describe, expect, it } from "vitest";

import type { RunAttempt } from "@/api/workflows";

import { nodeReruns, rerunsByNode } from "./runRerunProvenance";

const attempt = (n: number, rerunFromNodeId: string | null): RunAttempt => ({
  runId: `r${n}`, attemptNumber: n, status: "Success", sourceType: n === 1 ? "manual" : "rerun", rerunFromNodeId, createdDate: `2026-06-2${n}T00:00:00Z`, isLatest: false,
});

describe("rerunsByNode", () => {
  it("groups attempts by the node they re-ran, ignoring the original (no rerun target)", () => {
    const ladder = [attempt(1, null), attempt(2, "agent"), attempt(3, "agent"), attempt(4, "fanout")];
    const byNode = rerunsByNode(ladder);

    expect([...byNode.keys()].sort()).toEqual(["agent", "fanout"]);
    expect(byNode.get("agent")!.map((a) => a.attemptNumber)).toEqual([2, 3]);   // agent re-ran twice, oldest first
    expect(byNode.get("fanout")!.map((a) => a.attemptNumber)).toEqual([4]);
  });

  it("nodeReruns returns the node's attempts, or empty for a never-rerun node", () => {
    const byNode = rerunsByNode([attempt(1, null), attempt(2, "agent")]);
    expect(nodeReruns(byNode, "agent").map((a) => a.runId)).toEqual(["r2"]);
    expect(nodeReruns(byNode, "never")).toEqual([]);
  });

  it("a lineage with no reruns yields an empty map", () => {
    expect(rerunsByNode([attempt(1, null)]).size).toBe(0);
  });
});
