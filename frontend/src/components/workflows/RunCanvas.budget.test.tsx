import { render } from "@testing-library/react";
import type { NodeProps } from "@xyflow/react";
import { afterEach, describe, expect, it, vi } from "vitest";

import type { NodeManifestDto, WorkflowDefinition, WorkflowRunNodeSummary } from "@/api/workflows";

// Replace the real WorkflowNode with a tiny probe that faithfully mirrors the one contract under test:
// stamp `data-hot` on the `.wf-rf-node` from `data.hot` (the real WorkflowNode does exactly this — see
// WorkflowNode.test's "threads data.hot" case). This keeps the budget test focused on RunCanvas's hot-set
// computation + threading, not on ReactFlow rendering the real (footer-heavy) card.
vi.mock("./WorkflowNode", async (importActual) => {
  const actual = await importActual<typeof import("./WorkflowNode")>();
  return {
    ...actual,
    WorkflowNode: ({ data }: NodeProps) => {
      const d = data as { nodeId: string; hot?: boolean };
      return <div className="wf-rf-node" data-node={d.nodeId} data-hot={d.hot || undefined} />;
    },
  };
});

import { RunCanvas } from "./RunCanvas";

const manifest: NodeManifestDto = {
  typeKey: "t.a", displayName: "A", category: "AI", kind: "Regular",
  description: null, iconKey: null, configSchema: {}, inputSchema: {}, outputSchema: {},
};
// Identity-stable across rerenders so RunCanvas keeps its patch cache (a real 2s poll only swaps runNodes).
const manifestByType = new Map<string, NodeManifestDto>([["t.a", manifest]]);
const definition: WorkflowDefinition = {
  schemaVersion: 1,
  nodes: [
    { id: "n1", typeKey: "t.a", position: { x: 0, y: 0 }, config: {}, inputs: {} },
    { id: "n2", typeKey: "t.a", position: { x: 240, y: 0 }, config: {}, inputs: {} },
    { id: "n3", typeKey: "t.a", position: { x: 480, y: 0 }, config: {}, inputs: {} },
    { id: "n4", typeKey: "t.a", position: { x: 720, y: 0 }, config: {}, inputs: {} },
  ],
  edges: [],
};

/** Running rows, one per node, each with the given start time — what a live poll produces. */
function running(startedByNode: Record<string, string>): WorkflowRunNodeSummary[] {
  return Object.entries(startedByNode).map(([nodeId, startedAt]) => ({
    nodeId, iterationKey: "", status: "Running", inputs: {}, outputs: {}, error: null,
    startedAt, completedAt: null,
  }));
}

function hotIds(container: HTMLElement): string[] {
  return Array.from(container.querySelectorAll(".wf-rf-node[data-hot]"))
    .map((e) => e.getAttribute("data-node")!)
    .sort();
}

afterEach(() => vi.clearAllTimers());

describe("RunCanvas hot-node budget (C3)", () => {
  it("marks ONLY the 2 most-recently-started running nodes as hot", () => {
    const { container } = render(
      <RunCanvas
        definition={definition}
        runStatus="Running"
        manifestByType={manifestByType}
        runNodes={running({
          n1: "2026-07-13T00:00:01.000Z",
          n2: "2026-07-13T00:00:04.000Z", // newest
          n3: "2026-07-13T00:00:02.000Z",
          n4: "2026-07-13T00:00:03.000Z", // 2nd newest
        })}
      />,
    );

    expect(container.querySelectorAll(".wf-rf-node").length).toBe(4);
    expect(hotIds(container)).toEqual(["n2", "n4"]);

    // The two older running nodes carry NO hot marker — they read as running via the static accent ring only.
    const notHot = Array.from(container.querySelectorAll(".wf-rf-node"))
      .filter((e) => !e.hasAttribute("data-hot"))
      .map((e) => e.getAttribute("data-node")!)
      .sort();
    expect(notHot).toEqual(["n1", "n3"]);
  });

  it("re-renders a node that drops out of the hot set when another starts more recently", () => {
    const { container, rerender } = render(
      <RunCanvas
        definition={definition}
        runStatus="Running"
        manifestByType={manifestByType}
        runNodes={running({
          n1: "2026-07-13T00:00:01.000Z",
          n2: "2026-07-13T00:00:04.000Z",
          n3: "2026-07-13T00:00:02.000Z",
          n4: "2026-07-13T00:00:03.000Z",
        })}
      />,
    );
    expect(hotIds(container)).toEqual(["n2", "n4"]);

    // n1 becomes the newest; n2..n4 keep their SAME start times. n4 must drop from hot to cold even though
    // its own row didn't change — proving `hot` is in the patch fingerprint (else patchNodes would reuse the
    // stale, still-pulsing n4 card).
    rerender(
      <RunCanvas
        definition={definition}
        runStatus="Running"
        manifestByType={manifestByType}
        runNodes={running({
          n1: "2026-07-13T00:00:05.000Z", // now newest
          n2: "2026-07-13T00:00:04.000Z",
          n3: "2026-07-13T00:00:02.000Z",
          n4: "2026-07-13T00:00:03.000Z", // unchanged, but no longer top-2
        })}
      />,
    );

    expect(hotIds(container)).toEqual(["n1", "n2"]);
    const n4 = container.querySelector('.wf-rf-node[data-node="n4"]');
    expect(n4?.hasAttribute("data-hot")).toBe(false);
  });
});
