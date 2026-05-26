import { createFileRoute, useNavigate, Outlet } from "@tanstack/react-router";

import { useProjects } from "@/hooks/use-projects";
import { Ic } from "@/_imported/ai-code-space/icons";

export const Route = createFileRoute("/_app/teams/$teamSlug/projects")({
  component: ProjectsPage,
});

/**
 * Phase 3.0 — team's project list. Workflows still live at team scope (reusable
 * across projects via <c>project.{slug}.X</c> variable refs), so they don't
 * appear here; Repositories DO live inside a project so the cards expose the
 * count + a click-through into the project detail page where the Repositories
 * tab is.
 */
function ProjectsPage() {
  const { teamSlug } = Route.useParams();
  const projectsQuery = useProjects();
  const navigate = useNavigate();

  // Nested routes (project detail) render through this Outlet; if the URL is
  // exactly /teams/{slug}/projects we render the list inline.
  const projects = projectsQuery.data ?? [];

  return (
    <div className="content">
      <Outlet />

      <div className="content-head">
        <div className="content-head-title">
          <Ic.Folder size={18} />
          <span>Projects</span>
        </div>
        <div className="content-head-sub">
          {projects.length} active in this team — each Project owns a slice of
          Repositories + Variables. Workflows reference project variables as
          <code> project.{`{slug}`}.{`{name}`}</code>.
        </div>
      </div>

      {projectsQuery.isLoading && <div className="empty-state">Loading projects…</div>}

      {projectsQuery.isError && (
        <div className="empty-state">
          Could not load projects: {(projectsQuery.error as Error).message}
        </div>
      )}

      {projectsQuery.isSuccess && projects.length === 0 && (
        <div className="empty-state">No projects yet — a Default one is auto-created on team provisioning.</div>
      )}

      {projects.length > 0 && (
        <div className="repo-table">
          {projects.map(p => (
            <div
              key={p.id}
              className="repo-row"
              onClick={() => navigate({
                to: "/teams/$teamSlug/projects/$projectId",
                params: { teamSlug, projectId: p.id },
              })}
            >
              <div className="repo-row-main">
                <div className="repo-row-title">
                  <Ic.Folder size={14} />
                  <span className="repo-row-name">{p.name}</span>
                  <span className="repo-row-slug">{p.slug}</span>
                </div>
                {p.description && <div className="repo-row-desc">{p.description}</div>}
              </div>
              <div className="repo-row-meta">
                <span title="Active repositories">{p.activeRepositoryCount} repos</span>
                <span title="Active variables">{p.activeVariableCount} vars</span>
              </div>
            </div>
          ))}
        </div>
      )}
    </div>
  );
}
