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

/**
 * The picker drills into an OutputSchema's shape, but ONLY along paths the engine's VariableResolver can
 * actually resolve — object keys (`result.data.count`) and typed array items under an explicit `[0]` index
 * (`pullRequests[0].url`), NEVER a `[]` projection (which the resolver leaves unresolved → a silent null
 * dead-ref). Bare arrays and opaque objects stay single leaves. Driven purely by the schema shape.
 */
describe("introspectScope — recursive nested & typed-array outputs", () => {
  const richManifest = (typeKey: string, outputSchema: unknown): NodeManifestDto => ({
    typeKey, displayName: typeKey, category: "Test", kind: "Regular",
    description: null, iconKey: null, configSchema: {}, inputSchema: {}, outputSchema,
  });

  // start(trigger) → s(src, the schema under test) → t. Introspect from t so s is upstream.
  const build = (srcSchema: unknown) => {
    const mbt = new Map<string, NodeManifestDto>([
      ["trigger.x", manifest("trigger.x", "Trigger")],
      ["src", richManifest("src", srcSchema)],
      ["target", manifest("target", "Regular")],
    ]);
    const def: WorkflowDefinition = {
      schemaVersion: 1,
      nodes: [
        { id: "start", typeKey: "trigger.x", config: {}, inputs: {} },
        { id: "s", typeKey: "src", config: {}, inputs: {} },
        { id: "t", typeKey: "target", config: {}, inputs: {} },
      ],
      edges: [{ from: "start", to: "s" }, { from: "s", to: "t" }],
    };
    return introspectScope({ definition: def, currentNodeId: "t", manifestByType: mbt });
  };
  const pathsOf = (schema: unknown) => build(schema).map((x) => x.path);
  const under = (all: string[], prefix: string) => all.filter((x) => x.startsWith(prefix));

  it("drills a typed-item array under [0] — whole array + each item field, no projection dead-ref", () => {
    const p = pathsOf({ type: "object", properties: {
      count: { type: "integer" },
      pullRequests: { type: "array", items: { type: "object", properties: { number: { type: "integer" }, url: { type: "string" } } } },
    } });

    expect(p).toContain("nodes.s.outputs.count");
    expect(p).toContain("nodes.s.outputs.pullRequests");             // bind the whole array (e.g. a map's items)
    expect(p).toContain("nodes.s.outputs.pullRequests[0].number");   // resolvable index form
    expect(p).toContain("nodes.s.outputs.pullRequests[0].url");
    expect(p).not.toContain("nodes.s.outputs.pullRequests.number");  // the resolver can't walk an array by key
    expect(p).not.toContain("nodes.s.outputs.pullRequests[].number"); // projection is never resolvable
  });

  it("drills a nested object by key at each level (whole object + fields)", () => {
    const p = pathsOf({ type: "object", properties: {
      result: { type: "object", properties: { data: { type: "object", properties: { count: { type: "integer" } } } } },
    } });

    expect(p).toContain("nodes.s.outputs.result");
    expect(p).toContain("nodes.s.outputs.result.data");
    expect(p).toContain("nodes.s.outputs.result.data.count");
  });

  it("leaves a bare {type:array} as a single leaf (no invented item keys)", () => {
    expect(under(pathsOf({ type: "object", properties: { files: { type: "array" } } }), "nodes.s.outputs.files"))
      .toEqual(["nodes.s.outputs.files"]);
  });

  it("leaves an opaque {type:object} (no properties) as a single leaf", () => {
    expect(under(pathsOf({ type: "object", properties: { json: { type: "object" } } }), "nodes.s.outputs.json"))
      .toEqual(["nodes.s.outputs.json"]);
  });

  it("drills a union-typed array ([\"array\",\"null\"]) with typed items", () => {
    const p = pathsOf({ type: "object", properties: {
      rows: { type: ["array", "null"], items: { type: "object", properties: { x: { type: "string" } } } },
    } });

    expect(p).toContain("nodes.s.outputs.rows");
    expect(p).toContain("nodes.s.outputs.rows[0].x");
  });

  it("bounds recursion depth so a pathological schema can't flood the picker", () => {
    const deep = { type: "object", properties: { l1: { type: "object", properties: { l2: { type: "object", properties: {
      l3: { type: "object", properties: { l4: { type: "object", properties: { l5: { type: "object", properties: {
        l6: { type: "object", properties: { leaf: { type: "string" } } },
      } } } } } } } } } } } };
    const p = pathsOf(deep);

    expect(p).toContain("nodes.s.outputs.l1.l2.l3.l4.l5");   // reached the bound
    expect(p.some((x) => x.includes("l6"))).toBe(false);      // stopped before going deeper
  });

  it("carries the leaf/branch type hint and a readable nested label", () => {
    const all = build({ type: "object", properties: {
      pullRequests: { type: "array", items: { type: "object", properties: { number: { type: "integer" } } } },
    } });

    expect(all.find((x) => x.path === "nodes.s.outputs.pullRequests")?.type).toBe("array");
    const field = all.find((x) => x.path === "nodes.s.outputs.pullRequests[0].number");
    expect(field?.type).toBe("integer");
    expect(field?.label).toBe("src → pullRequests[0].number");
  });

  it("keeps a flat scalar output identical to before (backward compatible)", () => {
    const p = pathsOf({ type: "object", properties: { status: { type: "string" }, ok: { type: "boolean" } } });
    expect(under(p, "nodes.s.outputs.status")).toEqual(["nodes.s.outputs.status"]);
    expect(under(p, "nodes.s.outputs.ok")).toEqual(["nodes.s.outputs.ok"]);
  });

  it("does NOT key-walk a union that is also array-typed — emits the whole value, not dead `.key` paths", () => {
    const p = pathsOf({ type: "object", properties: {
      rows: { type: ["object", "array"], properties: { name: { type: "string" } } },
    } });
    expect(p).toContain("nodes.s.outputs.rows");            // bind the whole value ($ref resolves whichever kind)
    expect(p).not.toContain("nodes.s.outputs.rows.name");   // the resolver can't key-walk an array → never emit
  });

  it("skips non-identifier keys the resolver grammar can't match (hyphen, @, dot)", () => {
    const p = pathsOf({ type: "object", properties: {
      "content-type": { type: "string" },
      "@id": { type: "string" },
      contentType: { type: "string" },
    } });
    expect(p).toContain("nodes.s.outputs.contentType");        // identifier → offered
    expect(p).not.toContain("nodes.s.outputs.content-type");   // hyphen → would insert a literal dead ref
    expect(p.some((x) => x.includes("@id"))).toBe(false);
  });

  it("a root-level typed-item array drills nothing (no malformed leading-[0] path)", () => {
    const p = pathsOf({ type: "array", items: { type: "object", properties: { x: { type: "string" } } } });
    expect(p.some((x) => x.includes("[0]"))).toBe(false);
    expect(p.some((x) => x.startsWith("nodes.s.outputs.["))).toBe(false);
    expect(p).toContain("nodes.s.outputs");   // falls back to the generic "outputs not typed" placeholder
  });

  it("preserves union type hints on the whole-object / whole-array branch suggestions", () => {
    const all = build({ type: "object", properties: {
      result: { type: ["object", "null"], properties: { count: { type: "integer" } } },
      rows: { type: ["array", "null"], items: { type: "object", properties: { x: { type: "string" } } } },
    } });
    expect(all.find((x) => x.path === "nodes.s.outputs.result")?.type).toBe("object|null");
    expect(all.find((x) => x.path === "nodes.s.outputs.rows")?.type).toBe("array|null");
  });
});

/**
 * Slice 2 — inside a flow.map body, {{item}} is one element of the array bound to the map's `items` input.
 * When that source array declares a typed item shape, the picker drills `item.<field>` ("Current item →
 * instruction") on top of bare {{item}} / {{index}}. The engine walks into the iteration element, so every
 * emitted path resolves. Untyped/literal sources fall back to bare item/index only.
 */
describe("introspectScope — typed {{item.*}} inside a map body", () => {
  const richManifest = (typeKey: string, kind: NodeManifestDto["kind"], outputSchema: unknown): NodeManifestDto => ({
    typeKey, displayName: typeKey, category: "Test", kind,
    description: null, iconKey: null, configSchema: {}, inputSchema: {}, outputSchema,
  });

  const planItems = { type: "object", properties: { items: { type: "array", items: { type: "object", properties: {
    instruction: { type: "string" },
    acceptance: { type: "object", properties: { command: { type: "array", items: { type: "string" } } } },
  } } } } };

  const baseManifests = (planSchema: unknown) => new Map<string, NodeManifestDto>([
    ["trigger.x", manifest("trigger.x", "Trigger")],
    ["plan", richManifest("plan", "Regular", planSchema)],
    ["flow.map", manifest("flow.map", "Map")],
    ["flow.map_start", manifest("flow.map_start", "Regular")],
    ["regular.a", manifest("regular.a", "Regular", { value: { type: "string" } })],
  ]);

  // trigger → plan(typed items[]) → map(items ← plan) ; map body: mapStart → body(parentId map)
  const build = (mapItemsBinding: unknown): WorkflowDefinition => ({
    schemaVersion: 1,
    nodes: [
      { id: "trigger", typeKey: "trigger.x", config: {}, inputs: {} },
      { id: "plan", typeKey: "plan", config: {}, inputs: {} },
      { id: "map", typeKey: "flow.map", config: {}, inputs: { items: mapItemsBinding } },
      { id: "mapStart", typeKey: "flow.map_start", parentId: "map", config: {}, inputs: {} },
      { id: "body", typeKey: "regular.a", parentId: "map", config: {}, inputs: {} },
    ],
    edges: [{ from: "trigger", to: "plan" }, { from: "plan", to: "map" }, { from: "mapStart", to: "body" }],
  });

  const iterPaths = (def: WorkflowDefinition, mbt = baseManifests(planItems)) =>
    introspectScope({ definition: def, currentNodeId: "body", manifestByType: mbt }).filter((s) => s.category === "iteration").map((s) => s.path);

  it("drills the bound array's item shape into item.<field> (nested too), keeping bare item/index", () => {
    const p = iterPaths(build("{{nodes.plan.outputs.items}}"));
    expect(p).toContain("item");
    expect(p).toContain("index");
    expect(p).toContain("item.instruction");
    expect(p).toContain("item.acceptance");
    expect(p).toContain("item.acceptance.command");
    expect(p).not.toContain("item.item");   // the whole-item branch is dropped (bare {{item}} covers it)
  });

  it("labels the typed rows 'Current item → <field>' and keeps the ref path in the description", () => {
    const s = introspectScope({ definition: build("{{nodes.plan.outputs.items}}"), currentNodeId: "body", manifestByType: baseManifests(planItems) })
      .find((x) => x.path === "item.instruction");
    expect(s?.label).toBe("Current item → instruction");
    expect(s?.description).toContain("item.instruction");
  });

  it("resolves the { $ref } binding form too", () => {
    expect(iterPaths(build({ $ref: "nodes.plan.outputs.items" }))).toContain("item.instruction");
  });

  it("falls back to bare item/index when the source array has no typed item shape", () => {
    const untyped = baseManifests({ type: "object", properties: { items: { type: "array" } } });
    expect(iterPaths(build("{{nodes.plan.outputs.items}}"), untyped)).toEqual(["item", "index"]);
  });

  it("falls back to bare item/index when items is a literal list, not a ref", () => {
    expect(iterPaths(build([{ instruction: "x" }]))).toEqual(["item", "index"]);
  });
});

describe("introspectScope — human labels (display-only, ref preserved)", () => {
  const find = (nodeId: string, path: string) =>
    introspectScope({ definition, currentNodeId: nodeId, manifestByType }).find((s) => s.path === path);

  it("labels a node output 'Node → key' but keeps the raw ref path for insertion", () => {
    const s = find("normalNext", "nodes.a.outputs.value");
    expect(s?.label).toBe("regular.a → value");            // human headline
    expect(s?.path).toBe("nodes.a.outputs.value");          // inserted {{ref}} unchanged — non-breaking
    expect(s?.description).toBe("nodes.a.outputs.value");    // raw path demoted to the sub-line, still discoverable
  });

  it("labels the error output 'Node → error message' with the ref preserved", () => {
    const s = find("handler", "nodes.a.outputs.error.message");
    expect(s?.label).toBe("regular.a → error message");
    expect(s?.path).toBe("nodes.a.outputs.error.message");
  });

  it("prefers the node's user label over the type name in the headline (ref stays id-based)", () => {
    const withLabel: WorkflowDefinition = {
      ...definition,
      nodes: definition.nodes.map((n) => (n.id === "a" ? { ...n, label: "Planner" } : n)),
    };
    const s = introspectScope({ definition: withLabel, currentNodeId: "normalNext", manifestByType })
      .find((x) => x.path === "nodes.a.outputs.value");
    expect(s?.label).toBe("Planner → value");               // "Planner → value", not the type key
    expect(s?.path).toBe("nodes.a.outputs.value");           // ref still the stable id path
  });
});
