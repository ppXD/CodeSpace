import { render } from "@testing-library/react";
import type { NodeProps } from "@xyflow/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import type { NodeManifestDto, NodeStatus, WorkflowDefinition, WorkflowRunNodeSummary } from "@/api/workflows";

// A module-level render tally keyed by node id — hoisted so the vi.mock factory (which is itself hoisted
// above the imports) can close over the SAME Map the test body reads.
const renderCounts = vi.hoisted(() => new Map<string, number>());

// Replace the real WorkflowNode with a tiny counter component. Every time React Flow renders a node's
// component, we bump that node's tally. The real WorkflowNodeData type export is preserved via importActual
// (it's erased at runtime, but keeping the spread means any real value export still resolves).
vi.mock("./WorkflowNode", async (importActual) => {
  const actual = await importActual<typeof import("./WorkflowNode")>();
  return {
    ...actual,
    WorkflowNode: ({ data }: NodeProps) => {
      const id = (data as { nodeId: string }).nodeId;
      renderCounts.set(id, (renderCounts.get(id) ?? 0) + 1);
      return <div data-testid={id} />;
    },
  };
});

import { RunCanvas } from "./RunCanvas";

// Identity-stable across BOTH renders: RunCanvas resets its patch cache when the definition / manifest
// identity changes, so a real 2s poll (which keeps these two references and only swaps runNodes) MUST reuse
// them here — otherwise every node would be treated as brand-new and re-render.
const manifest: NodeManifestDto = {
  typeKey: "t.a", displayName: "A", category: "AI", kind: "Regular",
  description: null, iconKey: null, configSchema: {}, inputSchema: {}, outputSchema: {},
};
const manifestByType = new Map<string, NodeManifestDto>([["t.a", manifest]]);

const definition: WorkflowDefinition = {
  schemaVersion: 1,
  nodes: [
    { id: "n1", typeKey: "t.a", position: { x: 0, y: 0 }, config: {}, inputs: {} },
    { id: "n2", typeKey: "t.a", position: { x: 240, y: 0 }, config: {}, inputs: {} },
    { id: "n3", typeKey: "t.a", position: { x: 480, y: 0 }, config: {}, inputs: {} },
  ],
  edges: [],
};

/** A fresh runNodes array with fresh row objects (exactly what a real 2s poll produces). */
function rows(statuses: Record<string, NodeStatus>): WorkflowRunNodeSummary[] {
  return Object.entries(statuses).map(([nodeId, status]) => ({
    nodeId, iterationKey: "", status, inputs: {}, outputs: {}, error: null,
    startedAt: "2026-07-13T00:00:00.000Z",
    completedAt: status === "Running" ? null : "2026-07-13T00:00:02.000Z",
  }));
}

beforeEach(() => renderCounts.clear());
afterEach(() => vi.clearAllTimers());

describe("RunCanvas re-render isolation (A3)", () => {
  it("re-renders ONLY the node whose status flipped on a poll, keeping the others reference-stable", () => {
    const { rerender } = render(
      <RunCanvas definition={definition} runStatus="Running" manifestByType={manifestByType} runNodes={rows({ n1: "Running", n2: "Running", n3: "Running" })} />,
    );

    // Baseline: each node has rendered (>=1) after the first paint + fitView measure pass. Record per-node.
    const base1 = renderCounts.get("n1") ?? 0;
    const base2 = renderCounts.get("n2") ?? 0;
    const base3 = renderCounts.get("n3") ?? 0;

    expect(base1).toBeGreaterThanOrEqual(1);
    expect(base2).toBeGreaterThanOrEqual(1);
    expect(base3).toBeGreaterThanOrEqual(1);

    // A real poll: brand-new array + brand-new row objects; ONLY n2 flips Running → Success.
    rerender(
      <RunCanvas definition={definition} runStatus="Running" manifestByType={manifestByType} runNodes={rows({ n1: "Running", n2: "Success", n3: "Running" })} />,
    );

    // The changed node re-rendered (patchNodes handed React Flow a NEW object for n2 → its fingerprint moved).
    expect(renderCounts.get("n2")).toBe(base2 + 1);

    // The unchanged nodes did NOT re-render (patchNodes returned their PREVIOUS object → React Flow's per-node
    // memo skipped them). This is the whole point of the reference-stabilization cache.
    expect(renderCounts.get("n1")).toBe(base1);
    expect(renderCounts.get("n3")).toBe(base3);
  });
});
