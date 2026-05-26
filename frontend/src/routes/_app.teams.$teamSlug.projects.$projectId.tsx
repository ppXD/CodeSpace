import { createFileRoute, useNavigate } from "@tanstack/react-router";
import { useState } from "react";

import { ApiError } from "@/api/request";
import { useProject } from "@/hooks/use-projects";
import { useCredentials, useProviderInstances } from "@/hooks/use-credentials";
import { useRepositories } from "@/hooks/use-repositories";
import { ProviderMark } from "@/_imported/ai-code-space/content";
import { Ic } from "@/_imported/ai-code-space/icons";

export const Route = createFileRoute("/_app/teams/$teamSlug/projects/$projectId")({
  validateSearch: (raw: Record<string, unknown>): { tab?: ProjectTab } => {
    if (typeof raw.tab === "string") {
      const lower = raw.tab.toLowerCase();
      if (lower === "variables") return { tab: "variables" };
    }
    return {};
  },
  component: ProjectDetailPage,
});

type ProjectTab = "repositories" | "variables";

/**
 * Project detail. Same `.ct-*` chrome as the Repositories list — crumbs row at
 * top so the operator sees "Projects / {slug}", title row with the project's
 * human name + actions (Edit / Delete), then a tabs row for Repositories /
 * Variables. The tab is URL-driven (`?tab=variables`) so deep-linking into the
 * variables view is shareable.
 *
 * Repositories tab renders the same table shape as the global Repositories
 * list — same `.tbl` + `.repo-cell` markup so the visual identity matches.
 * Variables tab is a placeholder for now (project-scoped variable CRUD is a
 * follow-up commit).
 */
function ProjectDetailPage() {
  const { teamSlug, projectId } = Route.useParams();
  const { tab: tabParam } = Route.useSearch();
  const tab: ProjectTab = tabParam ?? "repositories";

  const navigate = useNavigate();
  const projectQuery = useProject(projectId);
  const reposQuery = useRepositories({ projectId });
  const providerInstances = useProviderInstances();

  const instanceById = new Map((providerInstances.data ?? []).map(i => [i.id, i]));

  const setTab = (next: ProjectTab) =>
    navigate({
      to: "/teams/$teamSlug/projects/$projectId",
      params: { teamSlug, projectId },
      search: next === "repositories" ? {} : { tab: next },
    });

  if (projectQuery.isLoading) {
    return (
      <section className="ct">
        <div className="ct-body">
          <div className="ct-empty"><div className="ct-empty-h">Loading…</div></div>
        </div>
      </section>
    );
  }

  if (projectQuery.error instanceof ApiError) {
    return (
      <section className="ct">
        <div className="ct-body">
          <div className="cn-banner cn-banner-err" style={{ margin: 16 }}>
            <div className="cn-banner-h">Couldn't load project</div>
            <div className="cn-banner-p">{projectQuery.error.message}</div>
          </div>
        </div>
      </section>
    );
  }

  if (!projectQuery.data) {
    return (
      <section className="ct">
        <div className="ct-body">
          <div className="ct-empty">
            <div className="ct-empty-h">Project not found</div>
            <div className="ct-empty-p">
              The project may have been deleted.{" "}
              <a onClick={() => navigate({ to: "/teams/$teamSlug/projects", params: { teamSlug } })}>
                Back to projects
              </a>
            </div>
          </div>
        </div>
      </section>
    );
  }

  const project = projectQuery.data;
  const repos = reposQuery.data ?? [];

  return (
    <section className="ct">
      <div className="ct-head">
        <div className="ct-crumbs">
          <a onClick={() => navigate({ to: "/teams/$teamSlug/projects", params: { teamSlug } })}>
            Projects
          </a>
          <span className="sep">/</span>
          <span className="cur">{project.slug}</span>
        </div>
        <div className="ct-title-row">
          <div>
            <h1 className="ct-title">{project.name}</h1>
            <div className="ct-sub">
              {project.description ?? `Project namespace ${project.slug} — owns ${project.activeRepositoryCount} repositor${project.activeRepositoryCount === 1 ? "y" : "ies"} and ${project.activeVariableCount} variable${project.activeVariableCount === 1 ? "" : "s"}.`}
            </div>
          </div>
        </div>
        <div className="ct-tabs">
          <div className="ct-tab" data-active={tab === "repositories"} onClick={() => setTab("repositories")}>
            Repositories
            <span className="ct-tab-c">{project.activeRepositoryCount}</span>
          </div>
          <div className="ct-tab" data-active={tab === "variables"} onClick={() => setTab("variables")}>
            Variables
            <span className="ct-tab-c">{project.activeVariableCount}</span>
          </div>
        </div>
      </div>

      <div className="ct-body">
        {tab === "repositories" && (
          <>
            {reposQuery.isLoading && (
              <div className="ct-empty"><div className="ct-empty-h">Loading…</div></div>
            )}

            {reposQuery.error instanceof ApiError && (
              <div className="cn-banner cn-banner-err" style={{ margin: 16 }}>
                <div className="cn-banner-h">Couldn't load repositories</div>
                <div className="cn-banner-p">{reposQuery.error.message}</div>
              </div>
            )}

            {!reposQuery.isLoading && !reposQuery.error && repos.length === 0 && (
              <div className="ct-empty">
                <div className="ct-empty-h">No repositories in this project yet</div>
                <div className="ct-empty-p">
                  Use the global "+ Add repository" flow and pick this project in the
                  bind step. New binds go to the team's <code>default</code> project
                  unless you change the picker.
                </div>
              </div>
            )}

            {repos.length > 0 && (
              <table className="tbl">
                <thead>
                  <tr>
                    <th style={{ width: "46%" }}>Repository</th>
                    <th>Source</th>
                    <th>Branch</th>
                    <th>Visibility</th>
                  </tr>
                </thead>
                <tbody>
                  {repos.map(r => {
                    const instance = instanceById.get(r.providerInstanceId);
                    return (
                      <tr
                        key={r.id}
                        onClick={() => navigate({
                          to: "/teams/$teamSlug/repositories/$repoFullPath",
                          params: { teamSlug, repoFullPath: encodeURIComponent(r.fullPath) },
                        })}
                      >
                        <td>
                          <div className="repo-cell">
                            {instance && <ProviderMark provider={instance.provider} />}
                            <div className="repo-info">
                              <div className="repo-name">{r.name}</div>
                              <div className="repo-path"><span>{r.fullPath}</span></div>
                            </div>
                          </div>
                        </td>
                        <td>{instance?.displayName ?? "—"}</td>
                        <td>{r.defaultBranch}</td>
                        <td>
                          <span className="repo-vis">
                            {r.visibility === "Private" ? <Ic.Lock size={10} />
                              : r.visibility === "Public" ? <Ic.Globe size={10} />
                                : <Ic.Users size={10} />}
                            {r.visibility.toLowerCase()}
                          </span>
                        </td>
                      </tr>
                    );
                  })}
                </tbody>
              </table>
            )}
          </>
        )}

        {tab === "variables" && (
          <div className="ct-empty">
            <div className="ct-empty-h">Project variables CRUD lands in a follow-up commit</div>
            <div className="ct-empty-p">
              Until then, manage workflow-scope variables from the workflow editor's
              Variables tab. The resolver already understands{" "}
              <code>{`{{project.${project.slug}.<name>}}`}</code> — the API + UI
              for creating those rows is wiring that needs one more pass.
            </div>
          </div>
        )}
      </div>
    </section>
  );
}
