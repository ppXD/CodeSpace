import { fetchJson } from "./request";

/**
 * Unified variable API. Scope (Team / Workflow / Project) and value-type (String / Number /
 * Boolean / Object / Array / Secret) are orthogonal.
 *
 * REST surface (backend):
 *   GET    /api/team-variables                              → list team summaries (no values for Secret)
 *   PUT    /api/team-variables/{name}                       → upsert
 *   DELETE /api/team-variables/{name}                       → soft-delete (idempotent)
 *   GET    /api/workflows/{workflowId}/variables            → list workflow summaries
 *   PUT    /api/workflows/{workflowId}/variables/{name}     → upsert
 *   DELETE /api/workflows/{workflowId}/variables/{name}     → soft-delete
 *   GET    /api/projects/{projectId}/variables              → list project summaries
 *   PUT    /api/projects/{projectId}/variables/{name}       → upsert
 *   DELETE /api/projects/{projectId}/variables/{name}       → soft-delete
 *
 * Tenant boundary: team scope comes from X-Team-Id; workflow + project scope are verified
 * against the current team by the service (cross-team scopeId → 404 by design).
 */

/** Mirrors backend `Messages.Enums.VariableScope` (string enum). */
export type VariableScope = "Team" | "Workflow" | "Project";

/** Mirrors backend `Messages.Enums.VariableValueType` (string enum). */
export type VariableValueType = "String" | "Number" | "Boolean" | "Object" | "Array" | "Secret";

/** Mirrors backend `VariableSummary`. `valuePlain` is null for Secret rows — by design, not by omission. */
export interface VariableSummary {
  id: string;
  scope: VariableScope;
  scopeId: string;
  teamId: string;
  name: string;
  valueType: VariableValueType;
  valuePlain: string | null;
  description: string | null;
  createdDate: string;
  lastModifiedDate: string;
}

export interface SetVariableInput {
  /** JSON-encodable value. Secret type expects a JSON-string; other types accept matching JSON. */
  value: unknown;
  valueType: VariableValueType;
  description?: string | null;
}

export const teamVariablesApi = {
  list: () => fetchJson<VariableSummary[]>("/api/team-variables"),

  set: (name: string, input: SetVariableInput) =>
    fetchJson<void>(`/api/team-variables/${encodeURIComponent(name)}`, {
      method: "PUT",
      // `name` is duplicated in URL + body so the backend's `required Name` deserialization
      // succeeds; the controller then overrides with the URL value via `command with { Name = name }`
      // so the URL stays the authoritative source (Rule 17).
      body: JSON.stringify({
        name,
        valueType: input.valueType,
        value: input.value,
        description: input.description ?? null,
      }),
    }),

  delete: (name: string) =>
    fetchJson<void>(`/api/team-variables/${encodeURIComponent(name)}`, { method: "DELETE" }),
};

export const workflowVariablesApi = {
  list: (workflowId: string) =>
    fetchJson<VariableSummary[]>(`/api/workflows/${workflowId}/variables`),

  set: (workflowId: string, name: string, input: SetVariableInput) =>
    fetchJson<void>(`/api/workflows/${workflowId}/variables/${encodeURIComponent(name)}`, {
      method: "PUT",
      body: JSON.stringify({
        workflowId,
        name,
        valueType: input.valueType,
        value: input.value,
        description: input.description ?? null,
      }),
    }),

  delete: (workflowId: string, name: string) =>
    fetchJson<void>(`/api/workflows/${workflowId}/variables/${encodeURIComponent(name)}`, { method: "DELETE" }),
};

export const projectVariablesApi = {
  list: (projectId: string) =>
    fetchJson<VariableSummary[]>(`/api/projects/${projectId}/variables`),

  set: (projectId: string, name: string, input: SetVariableInput) =>
    fetchJson<void>(`/api/projects/${projectId}/variables/${encodeURIComponent(name)}`, {
      method: "PUT",
      body: JSON.stringify({
        projectId,
        name,
        valueType: input.valueType,
        value: input.value,
        description: input.description ?? null,
      }),
    }),

  delete: (projectId: string, name: string) =>
    fetchJson<void>(`/api/projects/${projectId}/variables/${encodeURIComponent(name)}`, { method: "DELETE" }),
};
