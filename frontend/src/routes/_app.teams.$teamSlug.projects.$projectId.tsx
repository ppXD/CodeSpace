import { createFileRoute, useNavigate } from "@tanstack/react-router";
import { useState } from "react";

import { useProject } from "@/hooks/use-projects";
import { useRepositories } from "@/hooks/use-repositories";
import { Ic } from "@/_imported/ai-code-space/icons";

export const Route = createFileRoute("/_app/teams/$teamSlug/projects/$projectId")({
  component: ProjectDetailPage,
});

type Tab = "repositories" | "variables";

/**
 * Project detail page. Two-tab layout: Repositories (the project's bound git repos —
 * Phase 3.0 moved them here from the team-level Repositories list) + Variables (the
 * project-scoped variable namespace referenced by workflows as
 * <c>project.{slug}.{name}</c>).
 *
 * <para>Variable CRUD is hosted by the existing variable panel infrastructure; this
 * page passes the project id as the variable scope when the operator opens the
 * Variables tab. For now the Variables tab is a placeholder card pointing operators
 * at the workflow editor's per-team variables UI — a follow-up commit promotes the
 * existing TeamVariablesPanel into a scope-parameterised component reusable here.</para>
 */
function ProjectDetailPage() {
  const { teamSlug, projectId } = Route.useParams();
  const projectQuery = useProject(projectId);
  const navigate = useNavigate();
  const [tab, setTab] = useState<Tab>("repositories");

  const repos = useRepositories({ projectId });

  if (projectQuery.isLoading) return <div className="content empty-state">Loading project…</div>;
  if (projectQuery.isError || !projectQuery.data) {
    return (
      <div className="content empty-state">
        Project not found.{" "}
        <a onClick={() => navigate({ to: "/teams/$teamSlug/projects", params: { teamSlug } })}>
          Back to projects
        </a>
      </div>
    );
  }

  const project = projectQuery.data;

  return (
    <div className="content">
      <div className="content-head">
        <div className="content-head-title">
          <a
            onClick={() => navigate({ to: "/teams/$teamSlug/projects", params: { teamSlug } })}
            className="link-back"
          >
            <Ic.ArrowLeft size={14} />
          </a>
          <Ic.Folder size={18} />
          <span>{project.name}</span>
          <span className="repo-row-slug">{project.slug}</span>
        </div>
        {project.description && <div className="content-head-sub">{project.description}</div>}
      </div>

      <div className="tab-bar">
        <button
          className="tab-bar-item"
          data-active={tab === "repositories"}
          onClick={() => setTab("repositories")}
        >
          <Ic.Repo size={13} />
          <span>Repositories</span>
          <span className="tab-bar-count">{project.activeRepositoryCount}</span>
        </button>
        <button
          className="tab-bar-item"
          data-active={tab === "variables"}
          onClick={() => setTab("variables")}
        >
          <Ic.Key size={13} />
          <span>Variables</span>
          <span className="tab-bar-count">{project.activeVariableCount}</span>
        </button>
      </div>

      {tab === "repositories" && (
        <div className="tab-panel">
          {repos.isLoading && <div className="empty-state">Loading repositories…</div>}
          {repos.isSuccess && repos.data.length === 0 && (
            <div className="empty-state">
              This project has no repositories yet. Use the global "+ Add repository" flow
              and pick this project in the bind step.
            </div>
          )}
          {repos.data && repos.data.length > 0 && (
            <div className="repo-table">
              {repos.data.map(r => (
                <div
                  key={r.id}
                  className="repo-row"
                  onClick={() => navigate({
                    to: "/teams/$teamSlug/repositories/$repoFullPath",
                    params: { teamSlug, repoFullPath: encodeURIComponent(r.fullPath) },
                  })}
                >
                  <div className="repo-row-main">
                    <div className="repo-row-title">
                      <Ic.Repo size={14} />
                      <span className="repo-row-name">{r.name}</span>
                    </div>
                    <div className="repo-row-desc">{r.fullPath}</div>
                  </div>
                  <div className="repo-row-meta">
                    <span>{r.defaultBranch}</span>
                  </div>
                </div>
              ))}
            </div>
          )}
        </div>
      )}

      {tab === "variables" && (
        <div className="tab-panel">
          <div className="empty-state">
            Project variables CRUD lands in a follow-up commit — until then, manage
            workflow variables from the workflow editor's Variables tab.
          </div>
        </div>
      )}
    </div>
  );
}
