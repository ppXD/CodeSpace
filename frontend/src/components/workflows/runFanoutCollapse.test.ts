import type { Node } from "@xyflow/react";
import { describe, expect, it } from "vitest";

import type { NodeDefinition, NodeManifestDto, WorkflowDefinition, WorkflowRunNodeSummary } from "@/api/workflows";

import { collapsedMapNode, runFanoutCollapse } from "./runFanoutCollapse";
import type { WorkflowNodeData } from "./WorkflowNode";

function node(id: string, typeKey: string, parentId: string | null = null): NodeDefinition {
  return { id, typeKey, parentId, config: {}, inputs: {} };
}

function manifest(kind: string): NodeManifestDto {
  return { kind } as NodeManifestDto;
}

const manifests = new Map<string, NodeManifestDto>([
  ["flow.map", manifest("Map")],
  ["flow.map_start", manifest("Regular")],
  ["agent.code", manifest("Regular")],
  ["llm.complete", manifest("Regular")],
]);

/** plan → map{ ms(marker) → agent(worker) } → synth — the canonical single-worker fan-out. */
function planMapDef(): WorkflowDefinition {
  return {
    schemaVersion: 1,
    nodes: [node("planner", "llm.complete"), node("map", "flow.map"), node("ms", "flow.map_start", "map"), node("agent", "agent.code", "map"), node("synth", "llm.complete")],
    edges: [],
  };
}

function branch(index: number, status: WorkflowRunNodeSummary["status"] = "Failure"): WorkflowRunNodeSummary {
  return { nodeId: "agent", iterationKey: `map#${index}`, containerKind: "flow.map", status, inputs: null, outputs: null, error: null, startedAt: null, completedAt: null };
}

describe("runFanoutCollapse", () => {
  it("collapses a single-worker map that fanned out (>=2 branches): worker rows ride the map, both body nodes hide", () => {
    const rows = new Map([["agent", [branch(0), branch(1), branch(2)]]]);

    const c = runFanoutCollapse(planMapDef(), manifests, rows);

    expect(c.fanoutRowsByMapId.get("map")).toHaveLength(3);
    expect([...c.hiddenNodeIds].sort()).toEqual(["agent", "ms"]);   // the marker AND the worker are now the card
  });

  it("does NOT collapse a single-branch map — it keeps the container (richer single-agent embed)", () => {
    const c = runFanoutCollapse(planMapDef(), manifests, new Map([["agent", [branch(0)]]]));

    expect(c.fanoutRowsByMapId.size).toBe(0);
    expect(c.hiddenNodeIds.size).toBe(0);
  });

  it("does NOT collapse a map whose body is a multi-step subgraph (two workers)", () => {
    const def = planMapDef();
    def.nodes.push(node("review", "agent.code", "map"));   // a second worker inside the map

    const c = runFanoutCollapse(def, manifests, new Map([["agent", [branch(0), branch(1)]]]));

    expect(c.fanoutRowsByMapId.size).toBe(0);   // a single card can't represent a 2-step body
  });

  it("ignores non-map containers (a loop body is never collapsed here)", () => {
    const def: WorkflowDefinition = {
      schemaVersion: 1,
      nodes: [node("loop", "flow.loop"), node("ls", "flow.loop_start", "loop"), node("body", "agent.code", "loop")],
      edges: [],
    };
    const loopManifests = new Map(manifests).set("flow.loop", manifest("Loop")).set("flow.loop_start", manifest("Regular"));

    const c = runFanoutCollapse(def, loopManifests, new Map([["body", [branch(0), branch(1)]]]));

    expect(c.fanoutRowsByMapId.size).toBe(0);   // kind !== "Map"
  });
});

const fanoutData = { nodeId: "map", typeKey: "flow.map", displayName: "Fan out", iconKey: null, kind: "Map", category: "", label: null, fanout: [branch(0), branch(1)] } as WorkflowNodeData;

function baseMapNode(parentId?: string): Node<WorkflowNodeData> {
  return { id: "map", type: "wf", position: { x: 100, y: 50 }, data: {} as WorkflowNodeData, style: { width: 400, height: 300 }, zIndex: parentId ? 1 : 0, ...(parentId ? { parentId } : {}) };
}

describe("collapsedMapNode", () => {
  it("drops the container style + the top-level z-0 floor, keeps id/type/position, locks interaction", () => {
    const out = collapsedMapNode(baseMapNode(), fanoutData);

    expect(out.style).toBeUndefined();             // fixed container size dropped → the card auto-sizes
    expect(out.zIndex).toBeUndefined();            // top-level → the z-0 floor (was below its body) dropped
    expect(out.position).toEqual({ x: 100, y: 50 });
    expect(out.data.fanout).toHaveLength(2);
    expect(out.draggable).toBe(false);
    expect(out.selectable).toBe(false);
  });

  it("PRESERVES parentId + the depth zIndex for a map nested inside a loop/try (stays in its container)", () => {
    const out = collapsedMapNode(baseMapNode("outerLoop"), fanoutData);

    expect(out.parentId).toBe("outerLoop");        // NOT flung to the canvas origin
    expect(out.zIndex).toBe(1);                    // depth z kept so it still paints above its container
    expect(out.style).toBeUndefined();             // sizing still dropped
  });
});
