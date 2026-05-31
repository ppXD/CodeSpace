import { describe, expect, it } from "vitest";

import type { NodeDefinition, NodeKind, NodeManifestDto, WorkflowDefinition } from "@/api/workflows";

import { definitionToRfNodes, LOOP_CONTAINER_H, LOOP_CONTAINER_W } from "./definitionToRfNodes";

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

  it("auto-lays out top-level nodes that have no saved position", () => {
    const out = byId(definitionToRfNodes(def([node("a", "http.request"), node("b", "http.request")]), manifests));

    expect(out.a.position).toEqual({ x: 80, y: 80 });
    expect(out.b.position).toEqual({ x: 80, y: 260 });
  });

  it("defaults a positionless body node to a slot inside its container", () => {
    const out = byId(definitionToRfNodes(
      def([node("loop", "flow.loop"), node("ls", "flow.loop_start", { parentId: "loop" })]),
      manifests,
    ));

    expect(out.ls.position).toEqual({ x: 40, y: 60 });
  });
});
