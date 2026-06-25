import { useMemo } from "react";
import { Background, BackgroundVariant, Controls, Panel, ReactFlow, ReactFlowProvider, type Edge, type Node } from "@xyflow/react";
import "@xyflow/react/dist/style.css";

import type { NodeManifestDto, NodeStatus, WorkflowDefinition, WorkflowRunNodeSummary, WorkflowRunStatus } from "@/api/workflows";
import { isRunActive } from "@/hooks/use-workflows";
import { ERROR_HANDLE } from "@/lib/workflowErrorRoute";

import { definitionToRfNodes } from "./definitionToRfNodes";
import { runEdgeFlow } from "./runCanvasFlow";
import { collapsedMapNode, runFanoutCollapse } from "./runFanoutCollapse";
import { RunOpenContext } from "./runOpenContext";
import { summarizeRunProgress } from "./runProgress";
import { CATCH_HANDLE } from "./workflowContainers";
import { WorkflowNode, type WorkflowNodeData } from "./WorkflowNode";

const NODE_TYPES = { wf: WorkflowNode };

interface RunCanvasProps {
  definition: WorkflowDefinition;
  /** The run's per-node rows — a node id may repeat (one row per map branch / loop pass). */
  runNodes: WorkflowRunNodeSummary[];
  /** The run's overall status — once terminal, a node still reading Running/Suspended is stale, so edges stop flowing. */
  runStatus: WorkflowRunStatus;
  manifestByType: Map<string, NodeManifestDto>;
  /** Open another run full-view (a sub-workflow node's child run). Provided to nodes via RunOpenContext. */
  onOpenRun?: (runId: string) => void;
}

/**
 * A read-only canvas that paints a run onto the workflow graph (n8n-style): each node carries its run
 * status as a ring + corner badge, so the whole execution reads at a glance. The graph comes from the
 * definition; status is merged in by node id. Editing is fully disabled — this is an observation surface.
 *
 * The run→node mapping is intentionally a thin, separate layer (aggregateStatus / statusByNodeId) so it
 * can be lifted later for the generic task-run view, where any run resolves to the same node-status shape.
 */
export function RunCanvas({ definition, runNodes, runStatus, manifestByType, onOpenRun }: RunCanvasProps) {
  return (
    <ReactFlowProvider>
      <RunOpenContext.Provider value={onOpenRun ?? null}>
        <RunCanvasInner definition={definition} runNodes={runNodes} runStatus={runStatus} manifestByType={manifestByType} />
      </RunOpenContext.Provider>
    </ReactFlowProvider>
  );
}

function RunCanvasInner({ definition, runNodes, runStatus, manifestByType }: RunCanvasProps) {
  const statuses = useMemo(() => statusByNodeId(runNodes), [runNodes]);
  const rowsByNodeId = useMemo(() => rowsByNode(runNodes), [runNodes]);
  // A fanned-out map collapses to ONE auto-sized fan-out card: its worker body's branch rows ride on the map
  // node, and the marker + worker body nodes (and their intra-edges) are hidden.
  const collapse = useMemo(() => runFanoutCollapse(definition, manifestByType, rowsByNodeId), [definition, manifestByType, rowsByNodeId]);
  // Once the run is terminal (Success/Failure/Cancelled), a node still reading Running/Suspended is stale — the
  // cancel/finish stopped it. The edges + progress chip read this so a cancelled run doesn't keep "flowing".
  const runActive = isRunActive(runStatus);

  const nodes = useMemo<Node<WorkflowNodeData>[]>(
    () => definitionToRfNodes(definition, manifestByType).map((n) => {
      const fanout = collapse.fanoutRowsByMapId.get(n.id);
      const data = { ...(n.data as WorkflowNodeData), runStatus: statuses.get(n.id), runRows: rowsByNodeId.get(n.id), fanout };

      // A collapsed map → an auto-sized fan-out card: keep its structure (incl. parentId for a nested map), drop
      // only the container sizing. See collapsedMapNode.
      if (fanout) return collapsedMapNode(n, data);

      return {
        ...n,
        data,
        draggable: false,
        selectable: false,
        connectable: false,
        ...(collapse.hiddenNodeIds.has(n.id) ? { hidden: true } : {}),
      };
    }),
    [definition, manifestByType, statuses, rowsByNodeId, collapse],
  );

  // Drop the intra-body edges of a collapsed map (both endpoints are now hidden) so no dangling connector remains.
  const edges = useMemo<Edge[]>(
    () => definitionToRunEdges(definition, statuses, runActive).filter((e) => !collapse.hiddenNodeIds.has(e.source) && !collapse.hiddenNodeIds.has(e.target)),
    [definition, statuses, runActive, collapse],
  );

  return (
    <div className="wf-run-canvas">
      <ReactFlow
        nodes={nodes}
        edges={edges}
        nodeTypes={NODE_TYPES}
        fitView
        fitViewOptions={{ padding: 0.22 }}
        minZoom={0.2}
        maxZoom={1.5}
        nodesDraggable={false}
        nodesConnectable={false}
        elementsSelectable={false}
        proOptions={{ hideAttribution: true }}
      >
        <Background variant={BackgroundVariant.Dots} gap={20} size={1} color="#E6E1D5" />
        <Controls showInteractive={false} position="bottom-right" />
        <Panel position="top-left"><RunProgress statuses={statuses} runActive={runActive} /></Panel>
      </ReactFlow>
    </div>
  );
}

/**
 * A live, run-level progress chip overlaid on the canvas (the run's overall status badge sits in the
 * header; this adds the per-node counts the header can't show). Derived from the same aggregated
 * node-status map that paints the cards, so it flips on every 2s poll alongside the badges. A pulsing dot
 * signals the run is still moving. Hidden until the run has touched a node.
 */
function RunProgress({ statuses, runActive }: { statuses: Map<string, NodeStatus>; runActive: boolean }) {
  const c = summarizeRunProgress(statuses);
  if (!c) return null;                                        // nothing executed yet

  // "running" is only meaningful while the run is live; on a terminal run a node still reading Running is stale.
  const live = runActive && c.running > 0;
  const tone = c.failure > 0 ? "failure" : live ? "running" : "success";

  return (
    <div className="wf-run-progress" data-tone={tone}>
      <span className="wf-run-progress-dot" data-live={live || undefined} />
      <span className="wf-run-progress-stat">{c.success} done</span>
      {live && <span className="wf-run-progress-stat wf-run-progress-running">{c.running} running</span>}
      {c.failure > 0 && <span className="wf-run-progress-stat wf-run-progress-failed">{c.failure} failed</span>}
    </div>
  );
}

/** The editor's edge mapping + an n8n-style run overlay: the path the run actually took lights up. */
function definitionToRunEdges(def: WorkflowDefinition, statuses: Map<string, NodeStatus>, runActive: boolean): Edge[] {
  return def.edges.map((e, idx) => {
    const isError = e.sourceHandle === ERROR_HANDLE;
    const isCatch = e.sourceHandle === CATCH_HANDLE;
    const flow = runEdgeFlow(statuses.get(e.from), statuses.get(e.to), runActive);
    return {
      id: `e${idx}-${e.from}-${e.to}`,
      source: e.from,
      target: e.to,
      sourceHandle: e.sourceHandle ?? undefined,
      type: "default",
      animated: flow.animated || isError || isCatch || undefined,
      label: e.condition ?? undefined,
      style: flow.stroke ? { stroke: flow.stroke, strokeWidth: 2 } : undefined,
      ...(isError ? { className: "wf-rf-edge-error" } : isCatch ? { className: "wf-rf-edge-catch" } : {}),
    };
  });
}

/** Group a run's rows by node id — one entry for a plain node, N for a map/loop fan-out. */
function rowsByNode(runNodes: WorkflowRunNodeSummary[]): Map<string, WorkflowRunNodeSummary[]> {
  const out = new Map<string, WorkflowRunNodeSummary[]>();
  for (const n of runNodes) {
    const arr = out.get(n.nodeId);
    if (arr) arr.push(n); else out.set(n.nodeId, [n]);
  }
  return out;
}

/** Collapse a node id's run rows to one status (a map/loop node fans out to many). */
function statusByNodeId(runNodes: WorkflowRunNodeSummary[]): Map<string, NodeStatus> {
  const grouped = new Map<string, NodeStatus[]>();
  for (const n of runNodes) {
    const arr = grouped.get(n.nodeId);
    if (arr) arr.push(n.status); else grouped.set(n.nodeId, [n.status]);
  }
  const out = new Map<string, NodeStatus>();
  for (const [id, statuses] of grouped) out.set(id, aggregateStatus(statuses));
  return out;
}

/** In-progress beats failed beats pending beats skipped; all-success is success. */
function aggregateStatus(statuses: NodeStatus[]): NodeStatus {
  if (statuses.includes("Running")) return "Running";
  if (statuses.includes("Suspended")) return "Suspended";
  if (statuses.includes("Failure")) return "Failure";
  if (statuses.includes("Pending")) return "Pending";
  if (statuses.every((s) => s === "Skipped")) return "Skipped";
  return "Success";
}
