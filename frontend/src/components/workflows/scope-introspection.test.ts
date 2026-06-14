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

/**
 * A flow.wait_action's `action` output is the value a downstream node branches on — but it's an opaque
 * string unless the picker reveals which keys are possible. Following the wait's token back to the
 * chat.post_message that minted it surfaces the configured button keys, so the next node can wire against
 * a real value (the interaction-difficulty the design caused: you set buttons here, use the value there).
 */
describe("introspectScope — wait_action surfaces the post_message action keys", () => {
  const mbt = new Map<string, NodeManifestDto>([
    ["chat.post_message", manifest("chat.post_message", "Regular", { messageId: { type: "string" }, token: { type: "string" } })],
    ["flow.wait_action", manifest("flow.wait_action", "Regular", { action: { type: "string" }, by: { type: "string" }, comment: { type: "string" }, values: { type: "object" } })],
    ["git.pr_review", manifest("git.pr_review", "Regular", { reviewId: { type: "string" } })],
  ]);

  // post → wait(token wired to post) → review. `review` is downstream of the wait, so it sees the wait's outputs.
  function build(postInputs: unknown, waitToken: unknown): WorkflowDefinition {
    return {
      schemaVersion: 1,
      nodes: [
        { id: "post", typeKey: "chat.post_message", config: {}, inputs: postInputs },
        { id: "wait", typeKey: "flow.wait_action", config: {}, inputs: { token: waitToken } },
        { id: "review", typeKey: "git.pr_review", config: {}, inputs: {} },
      ],
      edges: [{ from: "post", to: "wait" }, { from: "wait", to: "review" }],
    };
  }

  const actionSuggestion = (def: WorkflowDefinition) =>
    introspectScope({ definition: def, currentNodeId: "review", manifestByType: mbt }).find((s) => s.path === "nodes.wait.outputs.action");

  it("lists the post's action keys on the wait's `action` output", () => {
    const def = build({ actions: [{ key: "approve", label: "Approve" }, { key: "reject", label: "Reject" }] }, "{{nodes.post.outputs.token}}");
    const s = actionSuggestion(def);
    expect(s).toBeDefined();
    expect(s!.description).toContain("approve");
    expect(s!.description).toContain("reject");
  });

  it("resolves the token from the { $ref } object form too", () => {
    const def = build({ actions: [{ key: "approve", label: "A" }] }, { $ref: "nodes.post.outputs.token" });
    expect(actionSuggestion(def)!.description).toContain("approve");
  });

  it("falls back to a plain string when the token is a literal (no upstream post link)", () => {
    const def = build({ actions: [{ key: "approve", label: "A" }] }, "a-literal-token");
    expect(actionSuggestion(def)!.description).not.toContain("approve");
  });

  it("falls back when the post's actions is a dynamic expression, not a literal list", () => {
    const def = build({ actions: "{{wf.actions}}" }, "{{nodes.post.outputs.token}}");
    expect(actionSuggestion(def)!.description).not.toContain("approve");
  });

  it("still surfaces the wait's other outputs (by/comment/values) unchanged", () => {
    const def = build({ actions: [{ key: "approve", label: "A" }] }, "{{nodes.post.outputs.token}}");
    const paths = introspectScope({ definition: def, currentNodeId: "review", manifestByType: mbt }).map((s) => s.path);
    expect(paths).toContain("nodes.wait.outputs.by");
    expect(paths).toContain("nodes.wait.outputs.comment");
    expect(paths).toContain("nodes.wait.outputs.values");
  });
});

/**
 * {{item}} / {{index}} discoverability. The engine seeds NodeRunScope.Iteration in two cases the picker
 * must surface: (a) DOWNSTREAM of a flow.iterate; (b) inside a flow.map BODY (BuildMapBranchScope seeds a
 * fresh {item,index} per branch). A loop body nested INSIDE a map inherits the map's Iteration through
 * BuildLoopScope, so it's covered too — but a TOP-LEVEL loop body (which reads {{loop.*}}, not bare
 * item/index) and an unrelated top-level node must NOT get them.
 */
describe("introspectScope — {{item}} / {{index}} in a map body", () => {
  const mbt = new Map<string, NodeManifestDto>([
    ["trigger.x", manifest("trigger.x", "Trigger")],
    ["flow.map", manifest("flow.map", "Map")],
    ["flow.map_start", manifest("flow.map_start", "Regular")],
    ["flow.loop", manifest("flow.loop", "Loop")],
    ["flow.loop_start", manifest("flow.loop_start", "Regular")],
    ["flow.iterate", manifest("flow.iterate", "Regular", { results: { type: "array" } })],
    ["regular.a", manifest("regular.a", "Regular", { value: { type: "string" } })],
  ]);

  // top trigger → mapNode (container) ; map body: mapStart → bodyNode (parentId mapNode)
  // separately: a top-level loop (loopNode) with loopBody (parentId loopNode), unrelated to the map.
  const def: WorkflowDefinition = {
    schemaVersion: 1,
    nodes: [
      { id: "trigger", typeKey: "trigger.x", config: {}, inputs: {} },
      { id: "mapNode", typeKey: "flow.map", config: {}, inputs: {} },
      { id: "mapStart", typeKey: "flow.map_start", parentId: "mapNode", config: {}, inputs: {} },
      { id: "bodyNode", typeKey: "regular.a", parentId: "mapNode", config: {}, inputs: {} },
      { id: "loopNode", typeKey: "flow.loop", config: {}, inputs: {} },
      { id: "loopBody", typeKey: "regular.a", parentId: "loopNode", config: {}, inputs: {} },
      { id: "topPlain", typeKey: "regular.a", config: {}, inputs: {} },
    ],
    edges: [
      { from: "trigger", to: "mapNode" },
      { from: "mapStart", to: "bodyNode" },
      { from: "mapNode", to: "topPlain" },
    ],
  };

  const itemPaths = (nodeId: string) =>
    introspectScope({ definition: def, currentNodeId: nodeId, manifestByType: mbt }).filter((s) => s.category === "iteration").map((s) => s.path);

  it("offers {{item}} / {{index}} to a node inside a flow.map body", () => {
    expect(itemPaths("bodyNode")).toEqual(["item", "index"]);
  });

  it("offers {{item}} / {{index}} to a node inside a loop body nested under a map (inherits the map's iteration)", () => {
    const nested: WorkflowDefinition = {
      ...def,
      nodes: [
        ...def.nodes,
        // a loop whose body sits INSIDE the map body (loop parented to the map; its body parented to the loop)
        { id: "innerLoop", typeKey: "flow.loop", parentId: "mapNode", config: {}, inputs: {} },
        { id: "innerLoopBody", typeKey: "regular.a", parentId: "innerLoop", config: {}, inputs: {} },
      ],
    };
    const paths = introspectScope({ definition: nested, currentNodeId: "innerLoopBody", manifestByType: mbt }).filter((s) => s.category === "iteration").map((s) => s.path);
    expect(paths).toEqual(["item", "index"]);
  });

  it("does NOT offer {{item}} / {{index}} to a TOP-LEVEL loop body (it reads {{loop.*}}, not bare item/index)", () => {
    expect(itemPaths("loopBody")).toEqual([]);
  });

  it("does NOT offer {{item}} / {{index}} to an unrelated top-level node", () => {
    expect(itemPaths("topPlain")).toEqual([]);
  });

  it("still offers {{item}} / {{index}} downstream of a flow.iterate (existing behaviour unchanged)", () => {
    const iterateDef: WorkflowDefinition = {
      schemaVersion: 1,
      nodes: [
        { id: "trigger", typeKey: "trigger.x", config: {}, inputs: {} },
        { id: "iter", typeKey: "flow.iterate", config: {}, inputs: {} },
        { id: "after", typeKey: "regular.a", config: {}, inputs: {} },
      ],
      edges: [{ from: "trigger", to: "iter" }, { from: "iter", to: "after" }],
    };
    const paths = introspectScope({ definition: iterateDef, currentNodeId: "after", manifestByType: mbt }).filter((s) => s.category === "iteration").map((s) => s.path);
    expect(paths).toEqual(["item", "index"]);
  });
});
