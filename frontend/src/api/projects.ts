import type { CreateProjectInput, ProjectSummary, UpdateProjectInput } from "./types";
import { fetchJson } from "./request";

/**
 * Projects API client. Phase 3.0 — projects are team-scoped containers for Repositories +
 * Variables. Every team has at least one project ("default") auto-created on team
 * provisioning. Tenant scope comes from <c>X-Team-Id</c>; the MediatR pipeline behaviour
 * vets membership before the backend handler runs.
 *
 * REST surface:
 *   GET    /api/projects                       → list summaries
 *   GET    /api/projects/{idOrSlug}            → single (resolves a GUID or the team-unique slug)
 *   POST   /api/projects                       → create (returns { id })
 *   PUT    /api/projects/{projectId}           → rename / re-describe (slug immutable)
 *   DELETE /api/projects/{projectId}           → soft-delete (cascades variables)
 */
export const projectsApi = {
  list: () => fetchJson<ProjectSummary[]>("/api/projects"),

  /** Resolve one project by ref — either its GUID (legacy link) or team-unique slug (clean URL). */
  get: (ref: string) => fetchJson<ProjectSummary>(`/api/projects/${encodeURIComponent(ref)}`),

  create: (input: CreateProjectInput) =>
    fetchJson<{ id: string }>("/api/projects", {
      method: "POST",
      body: JSON.stringify(input),
    }),

  update: (projectId: string, input: UpdateProjectInput) =>
    fetchJson<void>(`/api/projects/${projectId}`, {
      method: "PUT",
      body: JSON.stringify({ projectId, ...input }),
    }),

  remove: (projectId: string) =>
    fetchJson<void>(`/api/projects/${projectId}`, { method: "DELETE" }),

  /** Re-parent an existing repository under the target project. Idempotent. */
  moveRepositoryHere: (targetProjectId: string, repositoryId: string) =>
    fetchJson<void>(`/api/projects/${targetProjectId}/repositories/${repositoryId}/move-here`, {
      method: "POST",
    }),
};

/**
 * Mirrors backend `ProjectService.SlugifyName` — used purely as a preview while the
 * operator types the project name, so the modal can show "Variables will resolve as
 * `project.{slug}.X`" before submit. The authoritative slug is computed server-side;
 * never send this string up the wire.
 */
export function slugifyProjectName(name: string): string {
  if (!name.trim()) return "";
  const sb: string[] = [];
  let lastHyphen = true;
  for (const ch of name) {
    if (/[A-Za-z0-9_]/.test(ch)) {
      sb.push(ch.toLowerCase());
      lastHyphen = false;
    } else if (!lastHyphen) {
      sb.push("-");
      lastHyphen = true;
    }
  }
  const joined = sb.join("").replace(/-+$/g, "");
  return joined.length <= 64 ? joined : joined.slice(0, 64).replace(/-+$/g, "");
}
