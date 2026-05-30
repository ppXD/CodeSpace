import { describe, expect, it } from "vitest";

import type { NodeManifestDto, WorkflowDefinition } from "@/api/workflows";
import { introspectScope } from "./scope-introspection";

/**
 * The `{{}}` picker must surface a node's universal `error` output — but ONLY where it's genuinely
 * reachable, i.e. for nodes on that node's error branch. A success-path node must not be cluttered
 * with error paths that would always resolve null there.
 */
function manifest(typeKey: string, kind: NodeManifestDto["kind"], outputProps: Record<string, { type?: string }> = {}): NodeManifestDto {
  return {
    typeKey,
    displayName: typeKey,
    category: "Test",
    kind,
    description: null,
    iconKey: null,
    configSchema: {},
    inputSchema: {},
    outputSchema: { type: "object", properties: outputProps },
  };
}

const manifestByType = new Map<string, NodeManifestDto>([
  ["trigger.x", manifest("trigger.x", "Trigger")],
  ["regular.a", manifest("regular.a", "Regular", { value: { type: "string" } })],
  ["regular.h", manifest("regular.h", "Regular")],
  ["builtin.terminal", manifest("builtin.terminal", "Terminal")],
]);

// start → a → normalNext → end ; a =(error)=> handler → end
const definition: WorkflowDefinition = {
  schemaVersion: 1,
  nodes: [
    { id: "start", typeKey: "trigger.x", config: {}, inputs: {} },
    { id: "a", typeKey: "regular.a", config: {}, inputs: {} },
    { id: "normalNext", typeKey: "regular.h", config: {}, inputs: {} },
    { id: "handler", typeKey: "regular.h", config: {}, inputs: {} },
    { id: "end", typeKey: "builtin.terminal", config: {}, inputs: {} },
  ],
  edges: [
    { from: "start", to: "a" },
    { from: "a", to: "normalNext" },
    { from: "a", to: "handler", sourceHandle: "error" },
    { from: "normalNext", to: "end" },
    { from: "handler", to: "end" },
  ],
};

const paths = (nodeId: string) => introspectScope({ definition, currentNodeId: nodeId, manifestByType }).map((s) => s.path);

describe("introspectScope — error outputs", () => {
  it("offers the error output to a node on the error branch", () => {
    const p = paths("handler");
    expect(p).toContain("nodes.a.outputs.error.message");
    expect(p).toContain("nodes.a.outputs.error.node");
  });

  it("does NOT offer the error output to a node on the success branch", () => {
    const p = paths("normalNext");
    expect(p).not.toContain("nodes.a.outputs.error.message");
    expect(p).not.toContain("nodes.a.outputs.error.node");
  });

  it("still offers the node's normal declared outputs on both branches", () => {
    expect(paths("handler")).toContain("nodes.a.outputs.value");
    expect(paths("normalNext")).toContain("nodes.a.outputs.value");
  });

  it("offers the error output downstream of the handler (chain reachability)", () => {
    // `end` is reachable from the handler, which sits on a's error branch — so a's error is in scope.
    expect(paths("end")).toContain("nodes.a.outputs.error.message");
  });

  it("offers no error outputs when there are no error edges", () => {
    const plain: WorkflowDefinition = {
      schemaVersion: 1,
      nodes: definition.nodes,
      edges: [
        { from: "start", to: "a" },
        { from: "a", to: "normalNext" },
        { from: "normalNext", to: "end" },
      ],
    };
    const p = introspectScope({ definition: plain, currentNodeId: "end", manifestByType }).map((s) => s.path);
    expect(p.some((x) => x.includes(".outputs.error"))).toBe(false);
  });
});
