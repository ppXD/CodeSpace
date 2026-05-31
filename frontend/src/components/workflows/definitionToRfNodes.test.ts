import { describe, expect, it } from "vitest";

import type { NodeDefinition, NodeKind, NodeManifestDto, WorkflowDefinition } from "@/api/workflows";

import { definitionToRfNodes, fitLoopSizes, LOOP_CONTAINER_H, LOOP_CONTAINER_W } from "./definitionToRfNodes";

const manifest = (typeKey: string, kind: NodeKind, extra: Partial<NodeManifestDto> = {}): NodeManifestDto => ({
  typeKey, displayName: typeKey, category: "Logic", kind, description: null, iconKey: null,
  configSchema: {}, inputSchema: {}, outputSchema: {}, ...extra,
});

const manifests = new Map<string, NodeManifestDto>([
  ["trigger.manual", manifest("trigger.manual", "Trigger", { isManual: true, displayName: "Manual start" })],
  ["flow.loop", manifest("flow.loop", "Loop", { displayName: "Loop" })],
  ["flow.loop_start", manifest("flow.loop_start", "Regular", { displayName: "Loop start" })],
  ["http.request", manifest("http.request", "Regular", { displayName: "HTTP request" })],
  ["flow.terminal", manifest("flow.terminal", "Terminal", { displayName: "Done" })],
]);

const node = (id: string, typeKey: string, extra: Partial<NodeDefinition> = {}): NodeDefinition =>
  ({ id, typeKey, config: {}, inputs: {}, ...extra });

const def = (nodes: NodeDefinition[], extra: Partial<WorkflowDefinition> = {}): WorkflowDefinition =>
  ({ schemaVersion: 1, nodes, edges: [], ...extra });

const byId = (out: ReturnType<typeof definitionToRfNodes>) => Object.fromEntries(out.map((n) => [n.id, n]));

describe("definitionToRfNodes", () => {
  it("stacks loop body nodes ABOVE the container (container zIndex 0, children zIndex 1)", () => {
    // The whole point of this contract: the container's solid body must never cover its children,
    // or you can't grab a child's connect handle to wire nodes inside the loop.
    const out = byId(definitionToRfNodes(
      def([
        node("loop", "flow.loop", { position: { x: 100, y: 100 } }),
        node("ls", "flow.loop_start", { parentId: "loop", position: { x: 40, y: 72 } }),
        node("req", "http.request", { parentId: "loop", position: { x: 300, y: 72 } }),
      ]),
      manifests,
    ));

    expect(out.loop.zIndex).toBe(0);
    expect(out.ls.zIndex).toBe(1);
    expect(out.req.zIndex).toBe(1);
  });

  it("leaves top-level non-loop nodes without an explicit zIndex (default stacking)", () => {
    const out = byId(definitionToRfNodes(def([node("m", "trigger.manual"), node("e", "flow.terminal")]), manifests));

    expect(out.m.zIndex).toBeUndefined();
    expect(out.e.zIndex).toBeUndefined();
  });

  it("orders the loop container BEFORE its body nodes (React Flow needs parent-before-child)", () => {
    // Body nodes intentionally listed first in the definition to prove the sort reorders them.
    const ids = definitionToRfNodes(
      def([
        node("ls", "flow.loop_start", { parentId: "loop" }),
        node("loop", "flow.loop"),
        node("req", "http.request", { parentId: "loop" }),
      ]),
      manifests,
    ).map((n) => n.id);

    expect(ids.indexOf("loop")).toBeLessThan(ids.indexOf("ls"));
    expect(ids.indexOf("loop")).toBeLessThan(ids.indexOf("req"));
  });

  it("sizes the loop container and carries parentId on body nodes", () => {
    const out = byId(definitionToRfNodes(
      def([node("loop", "flow.loop"), node("ls", "flow.loop_start", { parentId: "loop" })]),
      manifests,
    ));

    expect(out.loop.style).toMatchObject({ width: LOOP_CONTAINER_W, height: LOOP_CONTAINER_H });
    expect(out.ls.parentId).toBe("loop");
  });

  it("surfaces workflow input fields on the manual-start node card", () => {
    const out = byId(definitionToRfNodes(
      def([node("m", "trigger.manual")], { inputs: [{ name: "repo", schema: {} }] }),
      manifests,
    ));

    expect(out.m.data.inputFields).toEqual([{ name: "repo", schema: {} }]);
  });

  it("auto-lays out top-level nodes left→right when they have no saved position", () => {
    const out = byId(definitionToRfNodes(def([node("a", "http.request"), node("b", "http.request")]), manifests));

    expect(out.a.position).toEqual({ x: 80, y: 80 });
    expect(out.b.position).toEqual({ x: 400, y: 80 }, "second node sits to the RIGHT (80 + 320)");
  });

  it("defaults a positionless body node to a slot inside its container", () => {
    const out = byId(definitionToRfNodes(
      def([node("loop", "flow.loop"), node("ls", "flow.loop_start", { parentId: "loop" })]),
      manifests,
    ));

    expect(out.ls.position).toEqual({ x: 40, y: 90 });
  });

  it("renders a NESTED loop as a sized container, stacked by depth", () => {
    const out = byId(definitionToRfNodes(
      def([
        node("outer", "flow.loop", { position: { x: 0, y: 0 } }),
        node("inner", "flow.loop", { parentId: "outer", position: { x: 40, y: 40 } }),
        node("gc", "http.request", { parentId: "inner", position: { x: 60, y: 60 } }),
      ]),
      manifests,
    ));

    // zIndex == nesting depth, so a child always paints above its container at every level.
    expect(out.outer.zIndex).toBe(0);
    expect(out.inner.zIndex).toBe(1);
    expect(out.gc.zIndex).toBe(2);
    expect(out.gc.style).toBeUndefined();

    // Auto-fit: the outer container grew to WRAP the inner loop, so it's strictly bigger → no overlap.
    expect((out.inner.style!.width as number)).toBeGreaterThanOrEqual(LOOP_CONTAINER_W);
    expect((out.outer.style!.width as number)).toBeGreaterThan(out.inner.style!.width as number);
    expect((out.outer.style!.height as number)).toBeGreaterThan(out.inner.style!.height as number);
  });
});

describe("loop_start lock", () => {
  it("locks the loop's entry marker inside its container (extent:parent), but not other body nodes", () => {
    const out = byId(definitionToRfNodes(
      def([
        node("loop", "flow.loop"),
        node("ls", "flow.loop_start", { parentId: "loop" }),
        node("req", "http.request", { parentId: "loop" }),
      ]),
      manifests,
    ));

    expect(out.ls.extent).toBe("parent");   // entry marker can't be dragged out
    expect(out.req.extent).toBeUndefined(); // a regular body node can still be dragged out
  });
});

describe("explicit (user-resized) loop size", () => {
  it("uses the persisted width/height instead of auto-fit, and marks it on data.size", () => {
    const out = byId(definitionToRfNodes(
      def([node("loop", "flow.loop", { position: { x: 0, y: 0 }, width: 500, height: 300 })]),
      manifests,
    ));

    expect(out.loop.style).toEqual({ width: 500, height: 300 });
    expect((out.loop.data as { size?: unknown }).size).toEqual({ width: 500, height: 300 });
  });
});

describe("fitLoopSizes", () => {
  it("keeps a leaf loop at the default size when its body fits", () => {
    const sizes = fitLoopSizes([
      { id: "loop", parentId: null, x: 0, y: 0, isLoop: true },
      { id: "ls", parentId: "loop", x: 40, y: 90, isLoop: false },
    ]);
    expect(sizes.get("loop")).toEqual({ width: LOOP_CONTAINER_W, height: LOOP_CONTAINER_H });
  });

  it("grows an outer loop to wrap a nested loop (no overlap)", () => {
    const sizes = fitLoopSizes([
      { id: "outer", parentId: null, x: 0, y: 0, isLoop: true },
      { id: "inner", parentId: "outer", x: 40, y: 40, isLoop: true },
      { id: "ls_i", parentId: "inner", x: 40, y: 90, isLoop: false },
    ]);

    expect(sizes.get("inner")).toEqual({ width: LOOP_CONTAINER_W, height: LOOP_CONTAINER_H });
    expect(sizes.get("outer")).toEqual({ width: 40 + LOOP_CONTAINER_W + 40, height: 40 + LOOP_CONTAINER_H + 40 });
  });

  it("grows a loop to fit a body node placed past the default edge", () => {
    const sizes = fitLoopSizes([
      { id: "loop", parentId: null, x: 0, y: 0, isLoop: true },
      { id: "far", parentId: "loop", x: 900, y: 0, isLoop: false },
    ]);
    expect(sizes.get("loop")!.width).toBe(900 + 280 + 40); // far.x + node estimate + padding
  });
});
