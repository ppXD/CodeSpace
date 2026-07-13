import type { ComponentType } from "react";

import type { NodeKind, NodeStatus, WorkflowRunNodeSummary } from "@/api/workflows";

import type { WorkflowNodeData } from "../WorkflowNode";
import { BranchDotsFooter } from "./BranchDotsFooter";
import { ReceiptFooter } from "./ReceiptFooter";

/**
 * The kinds of run-result footer a node can hang under it in a run view. Each is a self-contained
 * "how this node's work reads" language — a settle receipt, a streamed token feed, a fan-out dot strip,
 * a wait card, etc. A node resolves to exactly one kind via {@link resolveFooterKind}; the {@link FOOTERS}
 * registry maps that kind to the component. Later PRs swap individual kinds to their bespoke component;
 * for now every kind but `branchDots` renders the coze-style {@link ReceiptFooter}.
 */
export type NodeFooterKind = "receipt" | "externalCall" | "tokenStream" | "agentFeed" | "branchDots" | "wait" | "pipeline" | "verdict";

/** What every footer component receives — the node's data + its run status + row(s), plus an optional hover title. */
export interface NodeFooterProps {
  data: WorkflowNodeData;
  status: NodeStatus;
  rows: WorkflowRunNodeSummary[];
  title?: string;
}

/** Per-typeKey footer, the most specific route — a node's own type wins over its category/kind. */
const FOOTER_BY_TYPE: Record<string, NodeFooterKind> = {
  "agent.run": "agentFeed", "agent.supervisor": "agentFeed", "agent.run_command": "pipeline",
  "llm.complete": "tokenStream", "plan.author": "tokenStream",
  "flow.map": "branchDots", "git.open_change_set": "branchDots",
  "git.integrate": "pipeline",
  "chat.post_message": "wait", "flow.sleep": "wait", "flow.decision": "wait",
  "flow.wait_action": "wait", "flow.wait_approval": "wait", "flow.wait_callback": "wait",
  "plan.confirm": "wait", "flow.subworkflow": "wait",
  "logic.if": "verdict", "logic.merge": "verdict", "flow.iterate": "verdict",
  "flow.try": "verdict", "builtin.terminal": "verdict",
  "http.request": "externalCall",
  "git.fetch_pr_checks": "externalCall", "git.fetch_pr_diff": "externalCall",
  "git.list_prs": "externalCall", "git.pr_review": "externalCall",
  "git.create_issue": "externalCall", "git.comment_issue": "externalCall",
  "git.close_issue": "externalCall", "git.open_pr": "externalCall",
  "git.merge_pr": "externalCall", "git.post_pr_comment": "externalCall",
};

/** Per-category fallback for a typeKey the registry doesn't name — a whole manifest category shares a footer language. */
const FOOTER_BY_CATEGORY: Record<string, NodeFooterKind> = {
  Agent: "agentFeed", AI: "tokenStream", Planning: "tokenStream",
  Git: "externalCall", Tools: "externalCall", Chat: "wait", Logic: "receipt", Triggers: "receipt",
};

/** Last-resort fallback by NodeKind — covers a plugin node with an unknown type AND category. */
const FOOTER_BY_KIND: Record<NodeKind, NodeFooterKind> = {
  Trigger: "receipt", Terminal: "verdict", Map: "branchDots", Loop: "receipt", Try: "verdict", Regular: "receipt",
};

/**
 * The footer kind for a node: its typeKey wins, then its manifest category, then its NodeKind, then a
 * `receipt` floor. Deterministic + total — a brand-new plugin type with an unheard-of category still
 * resolves (via kind → receipt), so a footer always renders.
 */
export function resolveFooterKind(typeKey: string, category: string, kind: NodeKind): NodeFooterKind {
  return FOOTER_BY_TYPE[typeKey] ?? FOOTER_BY_CATEGORY[category] ?? FOOTER_BY_KIND[kind] ?? "receipt";
}

/**
 * The kind → component registry Phase B plugs into: each footer kind swaps to its bespoke component here,
 * one PR at a time, with the rest of the pipeline untouched. THIS PR routes every kind to {@link ReceiptFooter}
 * except `branchDots` → {@link BranchDotsFooter}, so the rendered output is 1:1 with the pre-registry code.
 */
export const FOOTERS: Record<NodeFooterKind, ComponentType<NodeFooterProps>> = {
  receipt: ReceiptFooter,
  externalCall: ReceiptFooter,
  tokenStream: ReceiptFooter,
  agentFeed: ReceiptFooter,
  branchDots: BranchDotsFooter,
  wait: ReceiptFooter,
  pipeline: ReceiptFooter,
  verdict: ReceiptFooter,
};
