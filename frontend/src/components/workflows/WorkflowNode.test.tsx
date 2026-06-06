import { render } from "@testing-library/react";
import { ReactFlowProvider, type NodeProps } from "@xyflow/react";
import { describe, expect, it } from "vitest";

import { WorkflowNode, type WorkflowNodeData } from "./WorkflowNode";

/**
 * The canvas card leads with the node's HUMAN name (its editable label, falling back to the node
 * type's display name) and shows the immutable node id as a muted sub-line. The id is the reference
 * key used in {{nodes.<id>.outputs.…}} — visible to copy, but no longer the dominant header — so
 * setting a Label visibly "renames" the node.
 */
function renderNode(overrides: Partial<WorkflowNodeData>) {
  const data: WorkflowNodeData = {
    nodeId: "review_llm",
    typeKey: "llm.complete",
    displayName: "LLM Complete",
    iconKey: "sparkles",
    kind: "Regular",
    category: "AI",
    label: null,
    ...overrides,
  };
  const props = { id: data.nodeId, data, selected: false } as unknown as NodeProps;
  return render(
    <ReactFlowProvider>
      <WorkflowNode {...props} />
    </ReactFlowProvider>,
  );
}

describe("WorkflowNode card", () => {
  it("leads with the human label and shows the immutable id as a muted ref", () => {
    const { container } = renderNode({ nodeId: "review_llm", label: "Code review" });

    expect(container.querySelector(".wf-rf-node-title")?.textContent).toBe("Code review");
    expect(container.querySelector(".wf-rf-node-ref")?.textContent).toBe("review_llm");
  });

  it("falls back to the type's display name when the node has no label", () => {
    const { container } = renderNode({ nodeId: "complete", label: null, displayName: "LLM Complete" });

    expect(container.querySelector(".wf-rf-node-title")?.textContent).toBe("LLM Complete");
    expect(container.querySelector(".wf-rf-node-ref")?.textContent).toBe("complete");
  });
});
