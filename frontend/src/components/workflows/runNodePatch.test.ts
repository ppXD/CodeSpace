import type { Node } from "@xyflow/react";
import { describe, expect, it } from "vitest";

import type { NodeStatus, WorkflowRunNodeSummary } from "@/api/workflows";

import { nodeRunFingerprint, patchNodes } from "./runNodePatch";
import type { WorkflowNodeData } from "./WorkflowNode";

function row(over: Partial<WorkflowRunNodeSummary> = {}): WorkflowRunNodeSummary {
  return { nodeId: "n", iterationKey: "", status: "Success", inputs: null, outputs: null, error: null, startedAt: "t0", completedAt: "t1", ...over } as WorkflowRunNodeSummary;
}

function node(id: string, over: Partial<WorkflowNodeData> = {}, extra: Partial<Node<WorkflowNodeData>> = {}): Node<WorkflowNodeData> {
  const data: WorkflowNodeData = { nodeId: id, typeKey: "agent.run", displayName: "Run agent", iconKey: null, kind: "Regular", category: "Agent", label: null, ...over };
  return { id, type: "wf", position: { x: 0, y: 0 }, data, ...extra };
}

describe("nodeRunFingerprint", () => {
  it("is stable when only static base data differs", () => {
    const a = node("n1", { runStatus: "Running", displayName: "A" });
    const b = node("n1", { runStatus: "Running", displayName: "B (renamed)" });
    expect(nodeRunFingerprint(a)).toBe(nodeRunFingerprint(b)); // base data isn't part of the run overlay
  });

  it("changes on status, rows, rerun eligibility, hidden, and fan-out", () => {
    const base = node("n1", { runStatus: "Running" });
    expect(nodeRunFingerprint(base)).not.toBe(nodeRunFingerprint(node("n1", { runStatus: "Success" })));
    expect(nodeRunFingerprint(base)).not.toBe(nodeRunFingerprint(node("n1", { runStatus: "Running", runRows: [row()] })));
    expect(nodeRunFingerprint(base)).not.toBe(nodeRunFingerprint(node("n1", { runStatus: "Running", rerunnableFromHere: true })));
    expect(nodeRunFingerprint(base)).not.toBe(nodeRunFingerprint(node("n1", { runStatus: "Running" }, { hidden: true })));
    expect(nodeRunFingerprint(base)).not.toBe(nodeRunFingerprint(node("n1", { runStatus: "Running", fanout: [row()] })));
  });
});

describe("patchNodes", () => {
  it("returns next as-is when there is no previous array", () => {
    const next = [node("n1"), node("n2")];
    expect(patchNodes(null, next)).toBe(next);
    expect(patchNodes([], next)).toBe(next);
  });

  it("reuses the previous object for every node whose run overlay is unchanged", () => {
    const prev = [node("n1", { runStatus: "Running" }), node("n2", { runStatus: "Success" })];
    const next = [node("n1", { runStatus: "Running" }), node("n2", { runStatus: "Success" })]; // fresh objects, same overlay
    const patched = patchNodes(prev, next);
    expect(patched[0]).toBe(prev[0]); // same reference → React Flow skips it
    expect(patched[1]).toBe(prev[1]);
  });

  it("replaces only the node whose overlay moved, keeping the rest reference-stable", () => {
    const prev = [node("n1", { runStatus: "Running" }), node("n2", { runStatus: "Running" })];
    const next = [node("n1", { runStatus: "Success" }), node("n2", { runStatus: "Running" })]; // n1 advanced
    const patched = patchNodes(prev, next);
    expect(patched[0]).toBe(next[0]); // changed → fresh object
    expect(patched[0]).not.toBe(prev[0]);
    expect(patched[1]).toBe(prev[1]); // unchanged → previous object
  });

  it("detects a new row on an existing node as a change", () => {
    const prev = [node("n1", { runStatus: "Running", runRows: [row({ status: "Success" as NodeStatus })] })];
    const next = [node("n1", { runStatus: "Running", runRows: [row({ status: "Success" as NodeStatus }), row({ status: "Running" as NodeStatus, completedAt: null })] })];
    const patched = patchNodes(prev, next);
    expect(patched[0]).toBe(next[0]);
  });

  it("follows next's membership when a node is added or removed", () => {
    const prev = [node("n1"), node("n2")];
    const next = [node("n1"), node("n3")]; // n2 gone, n3 new
    const patched = patchNodes(prev, next);
    expect(patched.map((n) => n.id)).toEqual(["n1", "n3"]);
    expect(patched[0]).toBe(prev[0]); // n1 unchanged → reused
    expect(patched[1]).toBe(next[1]); // n3 is new
  });
});
