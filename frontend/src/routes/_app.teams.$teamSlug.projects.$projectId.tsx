import { createFileRoute, useNavigate } from "@tanstack/react-router";
import { useEffect, useMemo, useState } from "react";

import { ApiError } from "@/api/request";
import { projectsApi } from "@/api/projects";
import type { RepositorySummary } from "@/api/types";
import { useProject, useProjects } from "@/hooks/use-projects";
import { useProviderInstances } from "@/hooks/use-credentials";
import { useRepositories, useUnbindRepository } from "@/hooks/use-repositories";
import { useConfirm } from "@/components/dialog";
import { AddRepoModal } from "@/_imported/ai-code-space/add-repo-modal";
import { ProviderMark } from "@/_imported/ai-code-space/content";
import { Ic } from "@/_imported/ai-code-space/icons";
import { VariableTablePanel } from "@/components/workflows/VariableTablePanel";
import { useQueryClient } from "@tanstack/react-query";

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
 * Project detail. `.ct-*` chrome matches the Repositories list page (head with
 * crumbs + title-row + tabs row), so the Project feels like the same kind of
 * surface — just with two tabs (Repositories + Variables) instead of provider
 * filters. The tab choice is URL-driven via <c>?tab=variables</c> for
 * shareable deep links.
 *
 * Repositories tab uses the same <c>.tbl</c> + <c>.repo-cell</c> markup as the
 * team-level RepositoryListPage; hover reveals an action toolbar: Open in
 * provider · Move to another project · Unbind. Row click goes to the existing
 * <c>/teams/{slug}/repositories/{fullPath}</c> detail page — Phase 3.0 didn't
 * change the repo-detail URL, only how operators reach it.
 *
 * "+ Add repository" on the Repositories tab opens the existing AddRepoModal
 * with this project pre-selected as the bind target.
 */
function ProjectDetailPage() {
  const { teamSlug, projectId } = Route.useParams();
  const { tab: tabParam } = Route.useSearch();
  const tab: ProjectTab = tabParam ?? "repositories";

  const navigate = useNavigate();
  const projectQuery = useProject(projectId);
  const reposQuery = useRepositories({ projectId });
  const providerInstances = useProviderInstances();
  const allProjects = useProjects();

  const instanceById = useMemo(() => new Map((providerInstances.data ?? []).map(i => [i.id, i])), [providerInstances.data]);

  const [addRepoOpen, setAddRepoOpen] = useState(false);
  const [moveTarget, setMoveTarget] = useState<RepositorySummary | null>(null);
  const unbind = useUnbindRepository();
  const confirm = useConfirm();

  const setTab = (next: ProjectTab) =>
    navigate({
      to: "/teams/$teamSlug/projects/$projectId",
      params: { teamSlug, projectId },
      search: next === "repositories" ? {} : { tab: next },
    });

  const askUnbind = async (r: RepositorySummary) => {
    const ok = await confirm({
      title: `Unbind ${r.fullPath}?`,
      message: "The repository will be removed from CodeSpace and its remote webhook deleted (best-effort). The repo on the provider isn't touched.",
      confirmLabel: "Unbind",
      destructive: true,
    });
    if (!ok) return;
    unbind.mutate(r.id);
  };

  if (projectQuery.isLoading) {
    return (
      <section className="ct">
        <div className="ct-body"><div className="ct-empty"><div className="ct-empty-h">Loading…</div></div></div>
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
          <a onClick={() => navigate({ to: "/teams/$teamSlug/projects", params: { teamSlug } })}>Projects</a>
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

      {tab === "repositories" && (
        <>
          <div className="ct-toolbar">
            <div className="ct-spacer" />
            <button className="btn btn-primary" onClick={() => setAddRepoOpen(true)}>
              <Ic.Plus size={14} /> Add repository
            </button>
          </div>

          <div className="ct-body">
            {reposQuery.isLoading && <div className="ct-empty"><div className="ct-empty-h">Loading…</div></div>}

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
                  Click <strong>+ Add repository</strong> above to pick from your provider's
                  accessible repositories. Multi-select supported — every selected repo
                  will be bound to this project.
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
                    <th className="col-right" />
                  </tr>
                </thead>
                <tbody>
                  {repos.map(r => {
                    const instance = instanceById.get(r.providerInstanceId);
                    return (
                      <tr
                        key={r.id}
                        data-status={r.status.toLowerCase()}
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
                        <td className="col-right">
                          <span className="row-act">
                            <button title="Open in provider" onClick={e => { e.stopPropagation(); window.open(r.webUrl, "_blank", "noopener"); }}>
                              <Ic.ArrowOut size={13} />
                            </button>
                            <button title="Move to another project" onClick={e => { e.stopPropagation(); setMoveTarget(r); }}>
                              <Ic.Folder size={13} />
                            </button>
                            <button
                              title="Unbind"
                              onClick={e => { e.stopPropagation(); void askUnbind(r); }}
                              disabled={unbind.isPending && unbind.variables === r.id}
                            >
                              <Ic.X size={13} />
                            </button>
                          </span>
                        </td>
                      </tr>
                    );
                  })}
                </tbody>
              </table>
            )}
          </div>
        </>
      )}

      {tab === "variables" && (
        <div className="ct-body">
          <VariableTablePanel
            scope="Project"
            projectId={projectId}
            refPrefix={`project.${project.slug}`}
            title={`Variables — ${project.name}`}
            subtitle={`Shared across every workflow that references project.${project.slug}.*. Secrets are AES-256-GCM encrypted and never returned by the API.`}
            tip={`Reference these from any workflow as {{project.${project.slug}.<name>}}. Plain values get re-resolved from live state at every run (including replays) — projects are shared config namespaces.`}
            emptyHint="No variables yet. Click + Add variable to create one — String type by default; pick Secret for encrypted values."
          />
        </div>
      )}

      {addRepoOpen && <AddRepoModal onClose={() => setAddRepoOpen(false)} presetProjectId={projectId} />}

      {moveTarget && (
        <MoveRepositoryModal
          repository={moveTarget}
          currentProjectId={projectId}
          allProjects={(allProjects.data ?? []).filter(p => p.id !== projectId)}
          onClose={() => setMoveTarget(null)}
        />
      )}
    </section>
  );
}

// ── Move repository to another project ────────────────────────────────────────

function MoveRepositoryModal({ repository, currentProjectId, allProjects, onClose }: { repository: RepositorySummary; currentProjectId: string; allProjects: Array<{ id: string; slug: string; name: string }>; onClose: () => void }) {
  const [target, setTarget] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [submitting, setSubmitting] = useState(false);
  const qc = useQueryClient();

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => { if (e.key === "Escape") onClose(); };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [onClose]);

  const submit = async () => {
    if (!target) return;
    setError(null);
    setSubmitting(true);
    try {
      await projectsApi.moveRepositoryHere(target, repository.id);
      // Invalidate both the source + target project's repo lists, plus the
      // projects index page's counts, so the UI snaps to the new state.
      await qc.invalidateQueries({ queryKey: ["repositories"] });
      await qc.invalidateQueries({ queryKey: ["projects"] });
      await qc.invalidateQueries({ queryKey: ["project", currentProjectId] });
      await qc.invalidateQueries({ queryKey: ["project", target] });
      onClose();
    } catch (ex) {
      setError(ex instanceof Error ? ex.message : "Could not move repository");
      setSubmitting(false);
    }
  };

  return (
    <>
      <div className="mdl-mask" />
      <div className="mdl" role="dialog" aria-modal="true">
        <div className="mdl-head">
          <div className="mdl-title-wrap">
            <div className="mdl-title">Move {repository.fullPath}</div>
            <div className="mdl-sub">Pick the destination project. The repository moves over without unbinding from the provider.</div>
          </div>
          <button className="mdl-x" onClick={onClose} aria-label="Close"><Ic.X size={14} /></button>
        </div>
        <div className="mdl-body">
          {allProjects.length === 0 ? (
            <div className="ct-empty">
              <div className="ct-empty-h">No other projects in this team</div>
              <div className="ct-empty-p">Create another project first from the Projects list page.</div>
            </div>
          ) : (
            <div className="cred-list">
              {allProjects.map(p => {
                const selected = target === p.id;
                return (
                  <button key={p.id} className="cred-row" data-active={selected} onClick={() => setTarget(p.id)}>
                    <div className="repo-mark" data-p="project" style={{ width: 26, height: 26, borderRadius: 6, display: "flex", alignItems: "center", justifyContent: "center" }}>
                      <Ic.Folder size={13} />
                    </div>
                    <div className="cred-row-meta">
                      <div className="cred-row-name">{p.name}</div>
                      <div className="cred-row-sub">{p.slug}</div>
                    </div>
                    {selected && <Ic.Check size={14} />}
                  </button>
                );
              })}
            </div>
          )}
          {error && <div className="cn-banner cn-banner-err" style={{ marginTop: 12 }}><div className="cn-banner-p">{error}</div></div>}
        </div>
        <div className="mdl-foot">
          <div className="ct-spacer" />
          <button className="btn btn-ghost" onClick={onClose} disabled={submitting}>Cancel</button>
          <button className="btn btn-primary" onClick={submit} disabled={!target || submitting}>
            {submitting ? "Moving…" : "Move repository"}
          </button>
        </div>
      </div>
    </>
  );
}
