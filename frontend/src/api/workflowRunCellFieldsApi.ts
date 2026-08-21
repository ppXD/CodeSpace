import type { NodeStatus } from "./workflows";
import { ApiError, fetchJson } from "./request";
import type { WorkflowRunCellCoordinate } from "./workflowRunViewMetadataApi";

export const WORKFLOW_RUN_CELL_FIELD_DESCRIPTOR_LIMIT = 50;
export const WORKFLOW_RUN_CELL_FIELD_DESCRIPTOR_CURSOR_MAX = 8192;
export type WorkflowRunCellFieldSection = "Input" | "Output" | "Error";
export type WorkflowRunCellFieldAvailability = "Available" | "NotRecorded" | "CorruptReference" | "NameTooLarge" | "Truncated" | "Unavailable";
export type WorkflowRunCellFieldMaterialization = "Inline" | "Artifact";
export type WorkflowRunCellFieldJsonKind = "Object" | "Array" | "String" | "Number" | "Boolean" | "Null" | "Unknown";
export type WorkflowRunCellFieldProblemCode = "MalformedReference" | "ArtifactMetadataMissing" | "DeclaredSizeMismatch" | "DeclaredContentTypeMismatch" | "StoredContentTypeMismatch";

export interface WorkflowRunCellFieldDescriptor {
  section: WorkflowRunCellFieldSection;
  name: string | null;
  jsonKind: WorkflowRunCellFieldJsonKind;
  materialization: WorkflowRunCellFieldMaterialization;
  availability: WorkflowRunCellFieldAvailability;
  totalBytes: number | null;
  sha256: string | null;
  contentType: string;
  problemCode: WorkflowRunCellFieldProblemCode | null;
}

export interface WorkflowRunCellFieldPage extends WorkflowRunCellCoordinate {
  stateRecordId: string;
  stateRecordSequence: number;
  firstStartedRecordId: string | null;
  firstStartedRecordSequence: number | null;
  status: NodeStatus;
  requestCursor: string | null;
  limit: number;
  fieldsAvailability: WorkflowRunCellFieldAvailability;
  inputsAvailability: WorkflowRunCellFieldAvailability;
  outputsAvailability: WorkflowRunCellFieldAvailability;
  errorAvailability: WorkflowRunCellFieldAvailability;
  fields: WorkflowRunCellFieldDescriptor[];
  nextCursor: string | null;
}

export class InvalidWorkflowRunCellFieldPageError extends Error {
  constructor() {
    super("Invalid Workflow Run cell-field descriptor response.");
    this.name = "InvalidWorkflowRunCellFieldPageError";
  }
}

const zeroGuid = "00000000-0000-0000-0000-000000000000";
const statuses = new Set<NodeStatus>(["Pending", "Running", "Success", "Failure", "Skipped", "Suspended"]);
const sections = new Set<WorkflowRunCellFieldSection>(["Input", "Output", "Error"]);
const availabilities = new Set<WorkflowRunCellFieldAvailability>(["Available", "NotRecorded", "CorruptReference", "NameTooLarge", "Truncated", "Unavailable"]);
const sectionAvailabilities = new Set<WorkflowRunCellFieldAvailability>(["Available", "NotRecorded", "Unavailable"]);
const materials = new Set<WorkflowRunCellFieldMaterialization>(["Inline", "Artifact"]);
const jsonKinds = new Set<WorkflowRunCellFieldJsonKind>(["Object", "Array", "String", "Number", "Boolean", "Null", "Unknown"]);
const problems = new Set<WorkflowRunCellFieldProblemCode>(["MalformedReference", "ArtifactMetadataMissing", "DeclaredSizeMismatch", "DeclaredContentTypeMismatch", "StoredContentTypeMismatch"]);
const pageKeys = ["errorAvailability", "fields", "fieldsAvailability", "firstStartedRecordId", "firstStartedRecordSequence", "inputsAvailability",
  "iterationKey", "limit", "nextCursor", "nodeId", "outputsAvailability", "requestCursor", "requestedRunId", "scope", "sourceRunId",
  "stateRecordId", "stateRecordSequence", "status"].sort().join(",");
const descriptorKeys = ["availability", "contentType", "jsonKind", "materialization", "name", "problemCode", "section", "sha256", "totalBytes"].sort().join(",");
const utf8 = new TextEncoder();

function invalid(): never { throw new InvalidWorkflowRunCellFieldPageError(); }
function object(value: unknown): value is Record<string, unknown> { return value !== null && typeof value === "object" && !Array.isArray(value); }
function exact(value: Record<string, unknown>, keys: string): boolean { return Object.keys(value).sort().join(",") === keys; }
function guid(value: unknown): value is string {
  return typeof value === "string" && value.toLowerCase() !== zeroGuid
    && /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(value);
}
function sameGuid(value: unknown, expected: string): value is string { return guid(value) && value.toLowerCase() === expected.toLowerCase(); }
function safe(value: unknown, positive = false): value is number { return Number.isSafeInteger(value) && (positive ? Number(value) > 0 : Number(value) >= 0); }
function cursor(value: unknown): value is string | null {
  return value === null || typeof value === "string" && value.trim().length > 0 && value.length <= WORKFLOW_RUN_CELL_FIELD_DESCRIPTOR_CURSOR_MAX;
}
function boundedIdentity(value: unknown, empty = false): value is string {
  return typeof value === "string" && (empty || value.length > 0) && value.length <= 512 && [...value].length <= 256;
}
function fieldName(value: string): boolean { return [...value].length <= 256 && utf8.encode(value).byteLength <= 1024; }
function compareUtf8(left: string, right: string): number {
  const a = utf8.encode(left);
  const b = utf8.encode(right);
  for (let index = 0; index < Math.min(a.length, b.length); index += 1) if (a[index] !== b[index]) return a[index] - b[index];
  return a.length - b.length;
}

function validateCoordinate(value: WorkflowRunCellCoordinate): void {
  if (!guid(value.requestedRunId) || (value.scope !== "LineageMerged" && value.scope !== "AttemptOnly") || !guid(value.sourceRunId)
    || !boundedIdentity(value.nodeId) || !boundedIdentity(value.iterationKey, true)) invalid();
}

function decodeDescriptor(value: unknown): WorkflowRunCellFieldDescriptor {
  if (!object(value) || !exact(value, descriptorKeys) || !sections.has(value.section as WorkflowRunCellFieldSection)
    || !jsonKinds.has(value.jsonKind as WorkflowRunCellFieldJsonKind) || !materials.has(value.materialization as WorkflowRunCellFieldMaterialization)
    || !availabilities.has(value.availability as WorkflowRunCellFieldAvailability)
    || !(value.totalBytes === null || safe(value.totalBytes)) || !(value.sha256 === null || typeof value.sha256 === "string" && /^[0-9a-f]{64}$/i.test(value.sha256))
    || value.contentType !== "application/json" || !(value.problemCode === null || problems.has(value.problemCode as WorkflowRunCellFieldProblemCode))) invalid();
  const section = value.section as WorkflowRunCellFieldSection;
  const materialization = value.materialization as WorkflowRunCellFieldMaterialization;
  const availability = value.availability as WorkflowRunCellFieldAvailability;
  if (section === "Error" ? value.name !== null : typeof value.name !== "string" || !fieldName(value.name)) invalid();
  if (materialization === "Inline") {
    if (availability !== "Available" || value.totalBytes !== null || value.sha256 !== null || value.problemCode !== null) invalid();
  } else {
    if (section !== "Output" || value.jsonKind !== "Object"
      || !(["Available", "CorruptReference", "Unavailable"] as WorkflowRunCellFieldAvailability[]).includes(availability)) invalid();
    if (availability === "Available"
      ? value.totalBytes === null || value.sha256 === null || value.problemCode !== null
      : value.totalBytes !== null || value.sha256 !== null || value.problemCode === null) invalid();
  }
  if (availability === "Unavailable" && value.problemCode !== "ArtifactMetadataMissing"
    || availability === "CorruptReference" && value.problemCode === "ArtifactMetadataMissing") invalid();
  return value as unknown as WorkflowRunCellFieldDescriptor;
}

function decode(value: unknown, coordinate: WorkflowRunCellCoordinate, requestCursor: string | null): WorkflowRunCellFieldPage {
  if (!object(value) || !exact(value, pageKeys) || !sameGuid(value.requestedRunId, coordinate.requestedRunId) || value.scope !== coordinate.scope
    || !sameGuid(value.sourceRunId, coordinate.sourceRunId) || value.nodeId !== coordinate.nodeId || value.iterationKey !== coordinate.iterationKey
    || !guid(value.stateRecordId) || !safe(value.stateRecordSequence, true)
    || (value.firstStartedRecordId === null) !== (value.firstStartedRecordSequence === null)
    || !(value.firstStartedRecordId === null || guid(value.firstStartedRecordId))
    || !(value.firstStartedRecordSequence === null || safe(value.firstStartedRecordSequence, true))
    || !statuses.has(value.status as NodeStatus) || value.requestCursor !== requestCursor
    || value.limit !== WORKFLOW_RUN_CELL_FIELD_DESCRIPTOR_LIMIT
    || !availabilities.has(value.fieldsAvailability as WorkflowRunCellFieldAvailability)
    || !sectionAvailabilities.has(value.inputsAvailability as WorkflowRunCellFieldAvailability)
    || !sectionAvailabilities.has(value.outputsAvailability as WorkflowRunCellFieldAvailability)
    || !sectionAvailabilities.has(value.errorAvailability as WorkflowRunCellFieldAvailability)
    || !Array.isArray(value.fields) || value.fields.length > WORKFLOW_RUN_CELL_FIELD_DESCRIPTOR_LIMIT || !cursor(value.nextCursor)) invalid();
  const fieldsAvailability = value.fieldsAvailability as WorkflowRunCellFieldAvailability;
  const fields = value.fields.map(decodeDescriptor);
  if (fieldsAvailability === "Truncated" ? fields.length === 0 || value.nextCursor === null
    : value.nextCursor !== null || (fieldsAvailability !== "Available" && fields.length !== 0)) invalid();
  const sectionRank = (section: WorkflowRunCellFieldSection) => section === "Input" ? 0 : section === "Output" ? 1 : 2;
  for (let index = 0; index < fields.length; index += 1) {
    const field = fields[index];
    const sectionAvailability = field.section === "Input" ? value.inputsAvailability : field.section === "Output" ? value.outputsAvailability : value.errorAvailability;
    if (sectionAvailability !== "Available") invalid();
    if (index === 0) continue;
    const previous = fields[index - 1];
    const rank = sectionRank(field.section) - sectionRank(previous.section);
    if (rank < 0 || rank === 0 && compareUtf8(previous.name ?? "", field.name ?? "") >= 0) invalid();
  }
  return { ...value, fields } as unknown as WorkflowRunCellFieldPage;
}

function path(coordinate: WorkflowRunCellCoordinate, requestCursor: string | null): string {
  const query = new URLSearchParams({ scope: coordinate.scope, sourceRunId: coordinate.sourceRunId, nodeId: coordinate.nodeId,
    iterationKey: coordinate.iterationKey, limit: String(WORKFLOW_RUN_CELL_FIELD_DESCRIPTOR_LIMIT) });
  if (requestCursor !== null) query.set("cursor", requestCursor);
  return `/api/workflows/runs/${encodeURIComponent(coordinate.requestedRunId)}/cells/fields?${query}`;
}

export const workflowRunCellFieldsApi = {
  async read(coordinate: WorkflowRunCellCoordinate, requestCursor: string | null, signal?: AbortSignal): Promise<WorkflowRunCellFieldPage | null> {
    validateCoordinate(coordinate);
    if (!cursor(requestCursor)) invalid();
    try {
      return decode(await fetchJson<unknown>(path(coordinate, requestCursor), { signal }), coordinate, requestCursor);
    } catch (error) {
      if (error instanceof ApiError && error.status === 404) return null;
      throw error;
    }
  },
};
