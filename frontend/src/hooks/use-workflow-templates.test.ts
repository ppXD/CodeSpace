import { describe, expect, it } from "vitest";

import { WORKFLOW_TEMPLATES } from "./use-workflow-templates";
import type { NodeDefinition } from "@/api/workflows";

// The templates ship a whole pre-wired workflow. These pin the wiring that makes the AI PR Review
// gate a real closed loop — because a typo in a {{ref}} string fails silently at run time, not at
// compile time.

const gate = WORKFLOW_TEMPLATES.find((t) => t.id === "ai-pr-review")!;
const node = (typeKey: string) => gate.definition.nodes.find((n: NodeDefinition) => n.typeKey === typeKey)!;

describe("WORKFLOW_TEMPLATES — AI PR Review", () => {
  it("is the single template and ships disabled (a channel must be picked first)", () => {
    expect(WORKFLOW_TEMPLATES.map((t) => t.id)).toEqual(["ai-pr-review"]);
    expect(gate.enabled).toBe(false);
  });

  it("posts a bounded quorum approval card (2 approvals, any block, 24h auto-resolve)", () => {
    const config = node("chat.post_message").config as { waitForResponse: boolean; resolve: Record<string, unknown> };
    expect(config.waitForResponse).toBe(true);
    expect(config.resolve).toMatchObject({ mode: "quorum", count: 2, deadlineSeconds: 86400, onTimeout: "request_changes" });

    const actions = (node("chat.post_message").inputs as { actions: Array<{ key: string; vetoes?: boolean }> }).actions;
    expect(actions.map((a) => a.key)).toEqual(["approve", "request_changes"]);
    expect(actions.find((a) => a.key === "request_changes")!.vetoes).toBe(true);
  });

  it("submits the card's decision back to the PR as the person who clicked", () => {
    const inputs = node("git.pr_review").inputs as Record<string, string>;
    expect(inputs.verdict).toBe("{{nodes.review_gate.outputs.action}}");
    expect(inputs.actAsUserId).toBe("{{nodes.review_gate.outputs.by}}");
    expect(inputs.body).toBe("{{nodes.review_gate.outputs.comment}}");
  });

  it("is a connected pipeline trigger → … → review → submit → end", () => {
    const froms = gate.definition.edges.map((e) => `${e.from}->${e.to}`);
    expect(froms).toEqual([
      "trigger->fetch_diff",
      "fetch_diff->review_llm",
      "review_llm->review_gate",
      "review_gate->submit_review",
      "submit_review->end",
    ]);
  });
});
