import { describe, expect, it } from "vitest";

import type { NodeKind } from "@/api/workflows";

import { resolveFooterKind, type NodeFooterKind } from "./index";

/**
 * Pins the footer-kind resolution for every builtin node type + the plugin-fallback ladder
 * (typeKey → category → kind → "receipt"). This is the deterministic seam Phase B plugs bespoke
 * footers into: a drift in these maps must fail here, loudly, before it silently repaints the canvas.
 */

/** One builtin node's resolution inputs + the footer kind it must produce. */
interface Case {
  typeKey: string;
  category: string;
  kind: NodeKind;
  expected: NodeFooterKind;
}

// All 42 builtin typeKeys. typeKeys IN the type-map resolve regardless of category/kind (the type wins);
// the ones NOT in it (triggers, flow.loop, flow.*_start) fall through to category/kind → "receipt".
const BUILTINS: Case[] = [
  // Triggers → receipt via category "Triggers" / kind "Trigger"
  { typeKey: "trigger.manual", category: "Triggers", kind: "Trigger", expected: "receipt" },
  { typeKey: "trigger.schedule", category: "Triggers", kind: "Trigger", expected: "receipt" },
  { typeKey: "trigger.push", category: "Triggers", kind: "Trigger", expected: "receipt" },
  { typeKey: "trigger.pr.opened", category: "Triggers", kind: "Trigger", expected: "receipt" },
  { typeKey: "trigger.pr.updated", category: "Triggers", kind: "Trigger", expected: "receipt" },
  { typeKey: "trigger.pr.merged", category: "Triggers", kind: "Trigger", expected: "receipt" },

  // Agent
  { typeKey: "agent.run", category: "Agent", kind: "Regular", expected: "agentFeed" },
  { typeKey: "agent.run_command", category: "Agent", kind: "Regular", expected: "pipeline" },
  { typeKey: "agent.supervisor", category: "Agent", kind: "Regular", expected: "agentFeed" },

  // Planning / AI → tokenStream
  { typeKey: "plan.author", category: "Planning", kind: "Regular", expected: "tokenStream" },
  { typeKey: "llm.complete", category: "AI", kind: "Regular", expected: "tokenStream" },

  // Chat → wait
  { typeKey: "chat.post_message", category: "Chat", kind: "Regular", expected: "wait" },

  // Tools
  { typeKey: "http.request", category: "Tools", kind: "Regular", expected: "externalCall" },

  // Git → externalCall (except open_change_set → branchDots, integrate → pipeline)
  { typeKey: "git.fetch_pr_checks", category: "Git", kind: "Regular", expected: "externalCall" },
  { typeKey: "git.fetch_pr_diff", category: "Git", kind: "Regular", expected: "externalCall" },
  { typeKey: "git.list_prs", category: "Git", kind: "Regular", expected: "externalCall" },
  { typeKey: "git.pr_review", category: "Git", kind: "Regular", expected: "externalCall" },
  { typeKey: "git.open_change_set", category: "Git", kind: "Regular", expected: "branchDots" },
  { typeKey: "git.create_issue", category: "Git", kind: "Regular", expected: "externalCall" },
  { typeKey: "git.comment_issue", category: "Git", kind: "Regular", expected: "externalCall" },
  { typeKey: "git.close_issue", category: "Git", kind: "Regular", expected: "externalCall" },
  { typeKey: "git.open_pr", category: "Git", kind: "Regular", expected: "externalCall" },
  { typeKey: "git.merge_pr", category: "Git", kind: "Regular", expected: "externalCall" },
  { typeKey: "git.post_pr_comment", category: "Git", kind: "Regular", expected: "externalCall" },
  { typeKey: "git.integrate", category: "Git", kind: "Regular", expected: "pipeline" },

  // Flow containers / markers
  { typeKey: "flow.loop", category: "Logic", kind: "Loop", expected: "receipt" },       // not in type-map → kind Loop → receipt
  { typeKey: "flow.map", category: "Logic", kind: "Map", expected: "branchDots" },
  { typeKey: "flow.try", category: "Logic", kind: "Try", expected: "verdict" },
  { typeKey: "flow.iterate", category: "Logic", kind: "Regular", expected: "verdict" },
  { typeKey: "flow.loop_start", category: "Logic", kind: "Regular", expected: "receipt" },   // Regular/Logic → receipt
  { typeKey: "flow.map_start", category: "Logic", kind: "Regular", expected: "receipt" },
  { typeKey: "flow.try_start", category: "Logic", kind: "Regular", expected: "receipt" },
  { typeKey: "flow.decision", category: "Logic", kind: "Regular", expected: "wait" },
  { typeKey: "flow.sleep", category: "Logic", kind: "Regular", expected: "wait" },
  { typeKey: "flow.subworkflow", category: "Logic", kind: "Regular", expected: "wait" },
  { typeKey: "flow.wait_action", category: "Logic", kind: "Regular", expected: "wait" },
  { typeKey: "flow.wait_approval", category: "Logic", kind: "Regular", expected: "wait" },
  { typeKey: "flow.wait_callback", category: "Logic", kind: "Regular", expected: "wait" },
  { typeKey: "plan.confirm", category: "Planning", kind: "Regular", expected: "wait" },

  // Logic / terminal → verdict
  { typeKey: "logic.if", category: "Logic", kind: "Regular", expected: "verdict" },
  { typeKey: "logic.merge", category: "Logic", kind: "Regular", expected: "verdict" },
  { typeKey: "builtin.terminal", category: "Logic", kind: "Terminal", expected: "verdict" },
];

describe("resolveFooterKind — builtin node types", () => {
  it("resolves all 42 builtin typeKeys to the expected footer kind", () => {
    expect(BUILTINS).toHaveLength(42);
  });

  it.each(BUILTINS)("$typeKey → $expected", ({ typeKey, category, kind, expected }) => {
    expect(resolveFooterKind(typeKey, category, kind)).toBe(expected);
  });
});

describe("resolveFooterKind — plugin fallback ladder", () => {
  it("falls back to the category when the typeKey is unknown", () => {
    expect(resolveFooterKind("custom.thing", "Git", "Regular")).toBe("externalCall");
  });

  it("falls back to the NodeKind when both typeKey and category are unknown", () => {
    expect(resolveFooterKind("custom.map", "Weird", "Map")).toBe("branchDots");
  });

  it("floors at 'receipt' when typeKey, category, and kind all miss a mapping", () => {
    expect(resolveFooterKind("custom.x", "Weird", "Regular")).toBe("receipt");
  });
});
