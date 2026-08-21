import type { NodeStatus, WorkflowDefinition, WorkflowRunNodeSummary, WorkflowRunStatus } from "./workflows";
import { ApiError, fetchJson } from "./request";

export type WorkflowRunViewScope = "LineageMerged" | "AttemptOnly";
export type WorkflowRunViewAvailability = "Available" | "Unavailable" | "Truncated" | "TooLarge" | "Corrupt";

export interface WorkflowRunCellCoordinate {
  requestedRunId: string;
  scope: WorkflowRunViewScope;
  sourceRunId: string;
  nodeId: string;
  iterationKey: string;
}

export interface WorkflowRunLazyFieldRead extends WorkflowRunCellCoordinate {
  observationKey: string;
}

export interface WorkflowRunCellMetadata {
  sourceRunId: string;
  nodeId: string;
  iterationKey: string;
  containerKind: string | null;
  status: NodeStatus;
  startedAt: string | null;
  completedAt: string | null;
  childRunId: string | null;
  agentRunId: string | null;
  rerunnableFromHere: boolean;
}

export interface WorkflowRunCanvasNode {
  id: string;
  typeKey: string;
  label: string | null;
  parentId: string | null;
  position: { x: number; y: number } | null;
  width: number | null;
  height: number | null;
}

export interface WorkflowRunCanvasEdge {
  from: string;
  to: string;
  sourceHandle: string | null;
  targetHandle: string | null;
  condition: string | null;
}

export interface WorkflowRunViewMetadata {
  runId: string;
  runNumber: number;
  workflowId: string | null;
  workflowVersion: number | null;
  sourceType: string;
  parentRunId: string | null;
  status: WorkflowRunStatus;
  hasError: boolean;
  startedAt: string | null;
  completedAt: string | null;
  createdDate: string;
  scope: WorkflowRunViewScope;
  cellsAvailability: WorkflowRunViewAvailability;
  linksAvailability: WorkflowRunViewAvailability;
  cells: WorkflowRunCellMetadata[];
  topologyAvailability: WorkflowRunViewAvailability;
  topology: { nodes: WorkflowRunCanvasNode[]; edges: WorkflowRunCanvasEdge[] } | null;
}

export interface WorkflowRunRoomCanvasData {
  definition: WorkflowDefinition;
  rows: WorkflowRunNodeSummary[];
}

export class InvalidWorkflowRunViewMetadataError extends Error {
  constructor() {
    super("Invalid Workflow Run view metadata response.");
    this.name = "InvalidWorkflowRunViewMetadataError";
  }
}

const scopes = new Set<WorkflowRunViewScope>(["LineageMerged", "AttemptOnly"]);
const availabilities = new Set<WorkflowRunViewAvailability>(["Available", "Unavailable", "Truncated", "TooLarge", "Corrupt"]);
const runStatuses = new Set<WorkflowRunStatus>(["Pending", "Enqueued", "Running", "Success", "Failure", "Cancelled", "Suspended"]);
const nodeStatuses = new Set<NodeStatus>(["Pending", "Running", "Success", "Failure", "Skipped", "Suspended"]);
const zeroGuid = "00000000-0000-0000-0000-000000000000";
const metadataKeys = ["cells", "cellsAvailability", "completedAt", "createdDate", "hasError", "linksAvailability", "parentRunId",
  "runId", "runNumber", "scope", "sourceType", "startedAt", "status", "topology", "topologyAvailability", "workflowId", "workflowVersion"].sort().join(",");
const cellKeys = ["agentRunId", "childRunId", "completedAt", "containerKind", "iterationKey", "nodeId", "rerunnableFromHere",
  "sourceRunId", "startedAt", "status"].sort().join(",");
const topologyKeys = ["edges", "nodes"].sort().join(",");
const nodeKeys = ["height", "id", "label", "parentId", "position", "typeKey", "width"].sort().join(",");
const positionKeys = ["x", "y"].sort().join(",");
const edgeKeys = ["condition", "from", "sourceHandle", "targetHandle", "to"].sort().join(",");

function invalid(): never { throw new InvalidWorkflowRunViewMetadataError(); }
function object(value: unknown): value is Record<string, unknown> { return value !== null && typeof value === "object" && !Array.isArray(value); }
function exact(value: Record<string, unknown>, keys: string): boolean { return Object.keys(value).sort().join(",") === keys; }
function guid(value: unknown): value is string {
  return typeof value === "string" && value.toLowerCase() !== zeroGuid
    && /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(value);
}
function nullableGuid(value: unknown): value is string | null { return value === null || guid(value); }
function safe(value: unknown, positive = false): value is number { return Number.isSafeInteger(value) && (positive ? Number(value) > 0 : Number(value) >= 0); }
function finite(value: unknown): value is number { return typeof value === "number" && Number.isFinite(value) && Math.abs(value) <= 10_000_000; }
function nullableFinite(value: unknown): value is number | null { return value === null || finite(value); }
function iso(value: unknown): value is string { return typeof value === "string" && /^\d{4}-\d{2}-\d{2}T/.test(value) && Number.isFinite(Date.parse(value)); }
function nullableIso(value: unknown): value is string | null { return value === null || iso(value); }
function bounded(value: unknown, maximum: number, empty = false): value is string {
  return typeof value === "string" && (empty || value.length > 0) && value.length <= maximum;
}
function nullableBounded(value: unknown, maximum: number): value is string | null { return value === null || bounded(value, maximum, true); }

function decodeCell(value: unknown, links: WorkflowRunViewAvailability): WorkflowRunCellMetadata {
  if (!object(value) || !exact(value, cellKeys) || !guid(value.sourceRunId) || !bounded(value.nodeId, 256)
    || !bounded(value.iterationKey, 256, true) || !nullableBounded(value.containerKind, 256)
    || !nodeStatuses.has(value.status as NodeStatus) || !nullableIso(value.startedAt) || !nullableIso(value.completedAt)
    || !nullableGuid(value.childRunId) || !nullableGuid(value.agentRunId) || typeof value.rerunnableFromHere !== "boolean"
    || links !== "Available" && (value.childRunId !== null || value.agentRunId !== null)) invalid();
  return value as unknown as WorkflowRunCellMetadata;
}

function decodeTopology(value: unknown): WorkflowRunViewMetadata["topology"] {
  if (!object(value) || !exact(value, topologyKeys) || !Array.isArray(value.nodes) || value.nodes.length > 1000
    || !Array.isArray(value.edges) || value.edges.length > 5000) invalid();
  const ids = new Set<string>();
  const nodes = value.nodes.map((raw) => {
    if (!object(raw) || !exact(raw, nodeKeys) || !bounded(raw.id, 256) || ids.has(raw.id)
      || !bounded(raw.typeKey, 256) || !nullableBounded(raw.label, 512) || !nullableBounded(raw.parentId, 256)
      || !nullableFinite(raw.width) || !nullableFinite(raw.height)) invalid();
    let position: WorkflowRunCanvasNode["position"] = null;
    if (raw.position !== null) {
      if (!object(raw.position) || !exact(raw.position, positionKeys) || !finite(raw.position.x) || !finite(raw.position.y)) invalid();
      position = { x: raw.position.x, y: raw.position.y };
    }
    ids.add(raw.id);
    return { id: raw.id, typeKey: raw.typeKey, label: raw.label, parentId: raw.parentId, position, width: raw.width, height: raw.height } as WorkflowRunCanvasNode;
  });
  if (nodes.some((node) => node.parentId !== null && !ids.has(node.parentId))) invalid();
  const parentById = new Map(nodes.map((node) => [node.id, node.parentId]));
  for (const node of nodes) {
    const ancestors = new Set<string>([node.id]);
    let parent = node.parentId;
    while (parent !== null) {
      if (ancestors.has(parent)) invalid();
      ancestors.add(parent);
      parent = parentById.get(parent) ?? null;
    }
  }
  const edges = value.edges.map((raw) => {
    if (!object(raw) || !exact(raw, edgeKeys) || !bounded(raw.from, 256) || !bounded(raw.to, 256)
      || !ids.has(raw.from) || !ids.has(raw.to) || !nullableBounded(raw.sourceHandle, 256)
      || !nullableBounded(raw.targetHandle, 256) || !nullableBounded(raw.condition, 1024)) invalid();
    return raw as unknown as WorkflowRunCanvasEdge;
  });
  return { nodes, edges };
}

function decode(value: unknown, runId: string, scope: WorkflowRunViewScope): WorkflowRunViewMetadata {
  if (!object(value) || !exact(value, metadataKeys) || !guid(value.runId) || value.runId.toLowerCase() !== runId.toLowerCase()
    || !safe(value.runNumber, true) || !nullableGuid(value.workflowId)
    || !(value.workflowVersion === null || safe(value.workflowVersion, true)) || !bounded(value.sourceType, 256)
    || !nullableGuid(value.parentRunId) || !runStatuses.has(value.status as WorkflowRunStatus) || typeof value.hasError !== "boolean"
    || !nullableIso(value.startedAt) || !nullableIso(value.completedAt) || !iso(value.createdDate) || value.scope !== scope
    || !availabilities.has(value.cellsAvailability as WorkflowRunViewAvailability)
    || !availabilities.has(value.linksAvailability as WorkflowRunViewAvailability)
    || !availabilities.has(value.topologyAvailability as WorkflowRunViewAvailability) || !Array.isArray(value.cells)
    || value.cells.length > 21_000) invalid();
  const cellsAvailability = value.cellsAvailability as WorkflowRunViewAvailability;
  const linksAvailability = value.linksAvailability as WorkflowRunViewAvailability;
  if (!(["Available", "Truncated"] as WorkflowRunViewAvailability[]).includes(cellsAvailability) && value.cells.length !== 0) invalid();
  const cells = value.cells.map((cell) => decodeCell(cell, linksAvailability));
  const identities = new Set(cells.map((cell) => `${cell.sourceRunId.toLowerCase()}\n${cell.nodeId}\n${cell.iterationKey}`));
  if (identities.size !== cells.length) invalid();
  const topologyAvailability = value.topologyAvailability as WorkflowRunViewAvailability;
  const topology = value.topology === null ? null : decodeTopology(value.topology);
  if ((topologyAvailability === "Available") !== (topology !== null)) invalid();
  if (topology !== null) {
    const topologyIds = new Set(topology.nodes.map((node) => node.id));
    if (cells.some((cell) => !topologyIds.has(cell.nodeId))) invalid();
  }
  if (cells.some((cell) => cell.rerunnableFromHere && cell.iterationKey !== "")
    || cellsAvailability === "Truncated" && cells.some((cell) => cell.rerunnableFromHere)) invalid();
  return { ...value, cells, topology } as unknown as WorkflowRunViewMetadata;
}

function observationKey(cell: WorkflowRunCellMetadata): string {
  return JSON.stringify([cell.sourceRunId, cell.nodeId, cell.iterationKey, cell.status, cell.startedAt, cell.completedAt,
    cell.childRunId, cell.agentRunId, cell.rerunnableFromHere]);
}

export function workflowRunLazyFieldRead(row: WorkflowRunNodeSummary): WorkflowRunLazyFieldRead | null {
  const candidate = (row as WorkflowRunNodeSummary & { lazyFieldRead?: WorkflowRunLazyFieldRead }).lazyFieldRead;
  return candidate ?? null;
}

export function adaptWorkflowRunViewToCanvas(view: WorkflowRunViewMetadata): WorkflowRunRoomCanvasData | null {
  if (view.topologyAvailability !== "Available" || view.topology === null) return null;
  return {
    definition: {
      schemaVersion: 1,
      nodes: view.topology.nodes.map((node) => ({ ...node, config: {}, inputs: {} })),
      edges: view.topology.edges,
    },
    rows: view.cells.map((cell) => ({
      nodeId: cell.nodeId,
      iterationKey: cell.iterationKey,
      containerKind: cell.containerKind,
      status: cell.status,
      inputs: null,
      outputs: null,
      error: null,
      startedAt: cell.startedAt,
      completedAt: cell.completedAt,
      childRunId: cell.childRunId,
      agentRunId: cell.agentRunId,
      rerunnableFromHere: cell.rerunnableFromHere,
      lazyFieldRead: {
        requestedRunId: view.runId,
        scope: view.scope,
        sourceRunId: cell.sourceRunId,
        nodeId: cell.nodeId,
        iterationKey: cell.iterationKey,
        observationKey: observationKey(cell),
      },
    })),
  };
}

export const workflowRunViewMetadataApi = {
  async read(runId: string, scope: WorkflowRunViewScope, signal?: AbortSignal): Promise<WorkflowRunViewMetadata | null> {
    if (!guid(runId) || !scopes.has(scope)) invalid();
    try {
      const value = await fetchJson<unknown>(`/api/workflows/runs/${encodeURIComponent(runId)}/view-metadata?scope=${scope}`, { signal });
      return decode(value, runId, scope);
    } catch (error) {
      if (error instanceof ApiError && error.status === 404) return null;
      throw error;
    }
  },
};
