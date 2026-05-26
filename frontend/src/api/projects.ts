import { fetchJson } from "./request";
import type { SetVariableInput, VariableSummary } from "./variables";

/**
 * Projects API. A Project is a Variable namespace — workflows reference its variables via
 * the dotted path `project.{slug}.{var_name}`. Project has no FK to workflow / repository /
 * workflow_run; it exists purely to group variables under a named scope.
 *
 * REST surface (backend):
 *   GET    /api/projects                              → list summaries
 *   GET    /api/projects/{projectId}                  → single project
 *   POST   /api/projects                              → create
 *   PUT    /api/projects/{projectId}                  → update name + description (slug is immutable)
 *   DELETE /api/projects/{projectId}                  → soft-delete + cascade-soft-delete variables
 *   GET    /api/projects/{projectId}/variables        → list variables
 *   PUT    /api/projects/{projectId}/variables/{name} → upsert variable
 *   DELETE /api/projects/{projectId}/variables/{name} → soft-delete variable
 *
 * Tenant boundary: team comes from X-Team-Id header. Cross-team project access returns 404.
 */
export interface ProjectSummary {
  id: string;
  teamId: string;
  slug: string;
  name: string;
  description: string | null;
  createdDate: string;
  lastModifiedDate: string;
}

export interface CreateProjectInput {
  slug: string;
  name: string;
  description?: string | null;
}

export interface UpdateProjectInput {
  name: string;
  description?: string | null;
}

export const projectsApi = {
  list: () => fetchJson<ProjectSummary[]>("/api/projects"),

  get: (projectId: string) => fetchJson<ProjectSummary>(`/api/projects/${projectId}`),

  create: (input: CreateProjectInput) =>
    fetchJson<{ projectId: string }>("/api/projects", {
      method: "POST",
      body: JSON.stringify(input),
    }),

  update: (projectId: string, input: UpdateProjectInput) =>
    fetchJson<void>(`/api/projects/${projectId}`, {
      method: "PUT",
      // ProjectId is duplicated in URL + body so the backend's `required ProjectId`
      // deserialization succeeds; the controller overrides with the URL value (Rule 17).
      body: JSON.stringify({ projectId, ...input }),
    }),

  delete: (projectId: string) =>
    fetchJson<void>(`/api/projects/${projectId}`, { method: "DELETE" }),
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
