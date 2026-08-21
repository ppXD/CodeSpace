import { ApiError, fetchJson } from "./request";

export const WORKFLOW_RUN_CELL_FIELD_PAGE_BYTES = 64 * 1024;
export const WORKFLOW_RUN_CELL_FIELD_CURSOR_MAX = 16 * 1024;

export type WorkflowRunViewScope = "LineageMerged" | "AttemptOnly";
export type WorkflowRunCellFieldSection = "Input" | "Output" | "Error";
export type WorkflowRunCellFieldRangeSource = "Unavailable" | "Inline" | "Artifact";
export type WorkflowRunCellFieldRangeAvailability =
  | "Available"
  | "NotRecorded"
  | "StaleIdentity"
  | "CorruptReference"
  | "MetadataMissing"
  | "PhysicalObjectMissing"
  | "IntegrityFailure"
  | "BackendUnavailable"
  | "AccessDenied"
  | "InvalidRange";
export type WorkflowRunNodeStatus = "Pending" | "Running" | "Success" | "Failure" | "Skipped" | "Suspended";

/** Exact public coordinate copied from one cell-field descriptor page. Artifact identity is deliberately absent. */
export interface WorkflowRunCellFieldReadIdentity {
  requestedRunId: string;
  scope: WorkflowRunViewScope;
  sourceRunId: string;
  nodeId: string;
  iterationKey: string;
  stateRecordId: string;
  stateRecordSequence: number;
  firstStartedRecordId: string | null;
  firstStartedRecordSequence: number | null;
  section: WorkflowRunCellFieldSection;
  name: string | null;
}

export interface WorkflowRunCellFieldRangePage extends WorkflowRunCellFieldReadIdentity {
  status: WorkflowRunNodeStatus;
  availability: WorkflowRunCellFieldRangeAvailability;
  source: WorkflowRunCellFieldRangeSource;
  requestCursor: string | null;
  limitBytes: number;
  offsetBytes: number;
  returnedBytes: number;
  totalBytes: number | null;
  nextCursor: string | null;
  text: string | null;
  contentType: string | null;
  integrityVerified: boolean;
  completeJsonValue: boolean;
  retryable: boolean;
}

export interface WorkflowRunCellFieldRangeRequest {
  cursor: string | null;
  offsetBytes: number;
}

export class InvalidWorkflowRunCellFieldRangeResponseError extends Error {
  constructor() {
    super("Invalid Workflow Run cell-field range response.");
    this.name = "InvalidWorkflowRunCellFieldRangeResponseError";
  }
}

const scopes = new Set<WorkflowRunViewScope>(["LineageMerged", "AttemptOnly"]);
const sections = new Set<WorkflowRunCellFieldSection>(["Input", "Output", "Error"]);
const statuses = new Set<WorkflowRunNodeStatus>(["Pending", "Running", "Success", "Failure", "Skipped", "Suspended"]);
const availabilities = new Set<WorkflowRunCellFieldRangeAvailability>([
  "Available", "NotRecorded", "StaleIdentity", "CorruptReference", "MetadataMissing", "PhysicalObjectMissing",
  "IntegrityFailure", "BackendUnavailable", "AccessDenied", "InvalidRange",
]);
const sources = new Set<WorkflowRunCellFieldRangeSource>(["Unavailable", "Inline", "Artifact"]);
const responseKeys = [
  "availability", "completeJsonValue", "contentType", "firstStartedRecordId", "firstStartedRecordSequence",
  "integrityVerified", "iterationKey", "limitBytes", "name", "nextCursor", "nodeId", "offsetBytes", "requestCursor",
  "requestedRunId", "retryable", "returnedBytes", "scope", "section", "source", "sourceRunId", "stateRecordId",
  "stateRecordSequence", "status", "text", "totalBytes",
].sort().join(",");
const zeroGuid = "00000000-0000-0000-0000-000000000000";
const strictUtf8 = new TextDecoder("utf-8", { fatal: true });
const utf8 = new TextEncoder();

function invalid(): never {
  throw new InvalidWorkflowRunCellFieldRangeResponseError();
}

function isObject(value: unknown): value is Record<string, unknown> {
  return value !== null && typeof value === "object" && !Array.isArray(value);
}

function isGuid(value: unknown): value is string {
  return typeof value === "string" && value.toLowerCase() !== zeroGuid
    && /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(value);
}

function sameGuid(value: unknown, expected: string): value is string {
  return isGuid(value) && value.toLowerCase() === expected.toLowerCase();
}

function isSafeInteger(value: unknown, positive = false): value is number {
  return Number.isSafeInteger(value) && (positive ? Number(value) > 0 : Number(value) >= 0);
}

function isStrictUtf8(value: string): boolean {
  try {
    return strictUtf8.decode(utf8.encode(value)) === value;
  } catch {
    return false;
  }
}

function validBoundedIdentity(value: string, allowEmpty: boolean): boolean {
  return (allowEmpty || value.length > 0) && value.length <= 512 && [...value].length <= 256 && isStrictUtf8(value);
}

function validFieldName(value: string): boolean {
  return [...value].length <= 256 && utf8.encode(value).byteLength <= 1024 && isStrictUtf8(value);
}

function validateIdentity(identity: WorkflowRunCellFieldReadIdentity): void {
  if (!isGuid(identity.requestedRunId) || !scopes.has(identity.scope) || !isGuid(identity.sourceRunId)
    || !validBoundedIdentity(identity.nodeId, false) || !validBoundedIdentity(identity.iterationKey, true)
    || !isGuid(identity.stateRecordId) || !isSafeInteger(identity.stateRecordSequence, true)
    || (identity.firstStartedRecordId === null) !== (identity.firstStartedRecordSequence === null)
    || (identity.firstStartedRecordId !== null && !isGuid(identity.firstStartedRecordId))
    || (identity.firstStartedRecordSequence !== null && !isSafeInteger(identity.firstStartedRecordSequence, true))
    || !sections.has(identity.section)
    || (identity.section === "Error" ? identity.name !== null : identity.name === null || !validFieldName(identity.name))) invalid();
}

function nullableCursor(value: unknown): value is string | null {
  return value === null || typeof value === "string" && value.trim().length > 0 && value.length <= WORKFLOW_RUN_CELL_FIELD_CURSOR_MAX;
}

function nullableSafeInteger(value: unknown): value is number | null {
  return value === null || isSafeInteger(value);
}

function nullableString(value: unknown): value is string | null {
  return value === null || typeof value === "string";
}

function decodePage(value: unknown, identity: WorkflowRunCellFieldReadIdentity,
  request: WorkflowRunCellFieldRangeRequest): WorkflowRunCellFieldRangePage {
  if (!isObject(value) || Object.keys(value).sort().join(",") !== responseKeys
    || !sameGuid(value.requestedRunId, identity.requestedRunId) || value.scope !== identity.scope
    || !sameGuid(value.sourceRunId, identity.sourceRunId) || value.nodeId !== identity.nodeId
    || value.iterationKey !== identity.iterationKey || !sameGuid(value.stateRecordId, identity.stateRecordId)
    || value.stateRecordSequence !== identity.stateRecordSequence
    || value.firstStartedRecordId !== identity.firstStartedRecordId
    || value.firstStartedRecordSequence !== identity.firstStartedRecordSequence
    || value.section !== identity.section || value.name !== identity.name
    || !statuses.has(value.status as WorkflowRunNodeStatus)
    || !availabilities.has(value.availability as WorkflowRunCellFieldRangeAvailability)
    || !sources.has(value.source as WorkflowRunCellFieldRangeSource)
    || value.requestCursor !== request.cursor || value.limitBytes !== WORKFLOW_RUN_CELL_FIELD_PAGE_BYTES
    || value.offsetBytes !== request.offsetBytes || !isSafeInteger(value.offsetBytes)
    || !isSafeInteger(value.returnedBytes) || value.returnedBytes > WORKFLOW_RUN_CELL_FIELD_PAGE_BYTES
    || !nullableSafeInteger(value.totalBytes) || !nullableCursor(value.nextCursor) || !nullableString(value.text)
    || !nullableString(value.contentType) || typeof value.integrityVerified !== "boolean"
    || typeof value.completeJsonValue !== "boolean" || typeof value.retryable !== "boolean") return invalid();

  const availability = value.availability as WorkflowRunCellFieldRangeAvailability;
  const source = value.source as WorkflowRunCellFieldRangeSource;
  if (availability === "Available") {
    if (source === "Unavailable" || typeof value.text !== "string" || value.contentType !== "application/json"
      || value.totalBytes === null || value.retryable || utf8.encode(value.text).byteLength !== value.returnedBytes
      || !isStrictUtf8(value.text) || value.offsetBytes > value.totalBytes - value.returnedBytes) return invalid();
    const end = value.offsetBytes + value.returnedBytes;
    if (value.nextCursor === null ? end !== value.totalBytes : value.returnedBytes === 0 || end >= value.totalBytes) return invalid();
    const complete = value.offsetBytes === 0 && value.nextCursor === null;
    if (value.completeJsonValue !== complete) return invalid();
    if (complete) {
      try { JSON.parse(value.text); } catch { return invalid(); }
    }
  } else {
    if (value.returnedBytes !== 0 || value.nextCursor !== null || value.text !== null || value.integrityVerified
      || value.completeJsonValue || value.retryable !== (availability === "BackendUnavailable")) return invalid();
    const unavailableSource = availability === "NotRecorded" || availability === "StaleIdentity" || availability === "IntegrityFailure";
    const inlineSource = availability === "IntegrityFailure" || availability === "InvalidRange";
    const artifactSource = availability !== "NotRecorded" && availability !== "StaleIdentity";
    if (source === "Unavailable" ? !unavailableSource || value.totalBytes !== null || value.contentType !== null
      : source === "Inline" ? !inlineSource || value.contentType !== "application/json"
        : !artifactSource || value.contentType !== "application/json") return invalid();
  }

  return value as unknown as WorkflowRunCellFieldRangePage;
}

function buildPath(identity: WorkflowRunCellFieldReadIdentity, cursor: string | null): string {
  const query = new URLSearchParams({
    scope: identity.scope,
    sourceRunId: identity.sourceRunId,
    nodeId: identity.nodeId,
    iterationKey: identity.iterationKey,
    stateRecordId: identity.stateRecordId,
    stateRecordSequence: String(identity.stateRecordSequence),
    section: identity.section,
    limitBytes: String(WORKFLOW_RUN_CELL_FIELD_PAGE_BYTES),
  });
  if (identity.firstStartedRecordId !== null) {
    query.set("firstStartedRecordId", identity.firstStartedRecordId);
    query.set("firstStartedRecordSequence", String(identity.firstStartedRecordSequence));
  }
  if (identity.name !== null) query.set("name", identity.name);
  if (cursor !== null) query.set("cursor", cursor);
  return `/api/workflows/runs/${encodeURIComponent(identity.requestedRunId)}/cells/fields/range?${query}`;
}

export const workflowRunCellFieldRangeApi = {
  async read(identity: WorkflowRunCellFieldReadIdentity, request: WorkflowRunCellFieldRangeRequest,
    signal?: AbortSignal): Promise<WorkflowRunCellFieldRangePage | null> {
    validateIdentity(identity);
    if (!isSafeInteger(request.offsetBytes)
      || (request.cursor === null ? request.offsetBytes !== 0 : !nullableCursor(request.cursor) || request.offsetBytes === 0)) invalid();
    try {
      const value = await fetchJson<unknown>(buildPath(identity, request.cursor), { signal });
      return decodePage(value, identity, request);
    } catch (error) {
      if (error instanceof ApiError && error.status === 404) return null;
      throw error;
    }
  },
};
