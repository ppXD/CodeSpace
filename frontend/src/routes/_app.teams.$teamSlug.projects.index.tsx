import { createFileRoute, useNavigate } from "@tanstack/react-router";
import { useState } from "react";

import { ApiError } from "@/api/request";
import { useProjects } from "@/hooks/use-projects";
import { Ic } from "@/_imported/ai-code-space/icons";

/**
 * Phase 3.0 — team's project list page. Same density + chrome as the
 * Repositories list (`RepositoryListPage` in content.tsx): `.ct-*` shell,
 * crumbs + title-row, optional search toolbar, `.tbl` table body with one
 * row per project. Click-through goes to the project detail page where
 * Variables + Repositories live behind tabs.
 *
 * No tabs here — projects don't have a "kind" axis to filter by. The page
 * also exposes a "New project" action; the modal lives below as a tiny
 * inline form (no separate modal infrastructure needed for one slug + name).
 */
export const Route = createFileRoute("/_app/teams/$teamSlug/projects/")({
  validateSearch: (raw: Record<string, unknown>): { q?: string } => {
    if (typeof raw.q === "string" && raw.q.length > 0) return { q: raw.q };
    return {};
  },
  component: ProjectsListPage,
});

function ProjectsListPage() {
  const { teamSlug } = Route.useParams();
  const search = Route.useSearch();
  const query = search.q ?? "";
  const navigate = useNavigate();

  const projectsQuery = useProjects();
  const [createOpen, setCreateOpen] = useState(false);

  const rows = projectsQuery.data ?? [];
  const filtered = query
    ? rows.filter(p =>
        p.name.toLowerCase().includes(query.toLowerCase()) ||
        p.slug.toLowerCase().includes(query.toLowerCase()))
    : rows;

  const setQuery = (next: string) =>
    navigate({
      to: "/teams/$teamSlug/projects",
      params: { teamSlug },
      search: next.length > 0 ? { q: next } : {},
    });

  return (
    <section className="ct">
      <div className="ct-head">
        <div className="ct-crumbs">
          <span>Projects</span>
        </div>
        <div className="ct-title-row">
          <div>
            <h1 className="ct-title">Projects</h1>
            <div className="ct-sub">
              Each project owns a slice of repositories and variables. Workflows reference
              project variables as <code>{`{{project.<slug>.<name>}}`}</code>; the same
              workflow can read from several projects at once.
            </div>
          </div>
          <div className="ct-actions">
            <button className="btn btn-primary" onClick={() => setCreateOpen(true)}>
              <Ic.Plus size={14} /> New project
            </button>
          </div>
        </div>
      </div>

      <div className="ct-toolbar">
        <div className="ct-search">
          <Ic.Search size={13} />
          <input
            placeholder="Search projects…"
            value={query}
            onChange={e => setQuery(e.target.value)}
          />
        </div>
        <div className="ct-spacer" />
      </div>

      <div className="ct-body">
        {projectsQuery.isLoading && (
          <div className="ct-empty"><div className="ct-empty-h">Loading…</div></div>
        )}

        {projectsQuery.error instanceof ApiError && (
          <div className="cn-banner cn-banner-err" style={{ margin: 16 }}>
            <div className="cn-banner-h">Couldn't load projects</div>
            <div className="cn-banner-p">{projectsQuery.error.message}</div>
          </div>
        )}

        {!projectsQuery.isLoading && !projectsQuery.error && filtered.length === 0 && (
          <div className="ct-empty">
            <div className="ct-empty-h">No projects match your search</div>
            <div className="ct-empty-p">
              Clear the filter or create a new project with the button above.
            </div>
          </div>
        )}

        {!projectsQuery.isLoading && !projectsQuery.error && filtered.length > 0 && (
          <table className="tbl">
            <thead>
              <tr>
                <th style={{ width: "50%" }}>Project</th>
                <th>Repositories</th>
                <th>Variables</th>
                <th>Created</th>
              </tr>
            </thead>
            <tbody>
              {filtered.map(p => (
                <tr
                  key={p.id}
                  onClick={() => navigate({
                    to: "/teams/$teamSlug/projects/$projectId",
                    params: { teamSlug, projectId: p.id },
                  })}
                >
                  <td>
                    <div className="repo-cell">
                      <div className="repo-mark" data-p="project" style={{ width: 30, height: 30, borderRadius: 7 }}>
                        <Ic.Folder size={14} />
                      </div>
                      <div className="repo-info">
                        <div className="repo-name">{p.name}</div>
                        <div className="repo-path">
                          <span>{p.slug}</span>
                          {p.description && <><span>·</span><span>{p.description}</span></>}
                        </div>
                      </div>
                    </div>
                  </td>
                  <td>{p.activeRepositoryCount}</td>
                  <td>{p.activeVariableCount}</td>
                  <td className="synced">{formatRelative(p.createdDate)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </div>

      {createOpen && (
        <CreateProjectModal
          onClose={() => setCreateOpen(false)}
          onCreated={(id) => {
            setCreateOpen(false);
            navigate({
              to: "/teams/$teamSlug/projects/$projectId",
              params: { teamSlug, projectId: id },
            });
          }}
        />
      )}
    </section>
  );
}

/**
 * Tiny inline modal for creating a project. Slug + name + optional description,
 * mirroring the backend validator (^[A-Za-z0-9_-]{1,64}$). Same backdrop/dialog
 * styling as the other modals in the app (ConnectRemoteModal, AddRepoModal).
 */
function CreateProjectModal({ onClose, onCreated }: { onClose: () => void; onCreated: (projectId: string) => void }) {
  const [slug, setSlug] = useState("");
  const [name, setName] = useState("");
  const [description, setDescription] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [submitting, setSubmitting] = useState(false);
  const create = useCreateProject();

  const slugLooksOk = /^[A-Za-z0-9_-]{1,64}$/.test(slug);
  const canSubmit = slugLooksOk && name.trim().length > 0 && !submitting;

  const submit = async () => {
    setError(null);
    setSubmitting(true);
    try {
      const { id } = await create.mutateAsync({
        slug: slug.trim(),
        name: name.trim(),
        description: description.trim() || null,
      });
      onCreated(id);
    } catch (ex) {
      setError(ex instanceof Error ? ex.message : "Could not create project");
      setSubmitting(false);
    }
  };

  return (
    <div className="modal-back" onClick={onClose}>
      <div className="modal" onClick={e => e.stopPropagation()}>
        <div className="modal-head">
          <h3>New project</h3>
          <button className="modal-x" onClick={onClose}><Ic.X size={14} /></button>
        </div>
        <div className="modal-body">
          <div className="form-row">
            <label>Slug</label>
            <input
              autoFocus
              placeholder="my-product"
              value={slug}
              onChange={e => setSlug(e.target.value)}
            />
            <div className="form-hint">
              URL-safe identifier. Used in variable refs like
              <code> {`{{project.${slug || "my-product"}.x}}`}</code>. Cannot be changed later.
            </div>
          </div>
          <div className="form-row">
            <label>Name</label>
            <input
              placeholder="My Product"
              value={name}
              onChange={e => setName(e.target.value)}
            />
          </div>
          <div className="form-row">
            <label>Description (optional)</label>
            <input
              placeholder="Short summary for the team"
              value={description}
              onChange={e => setDescription(e.target.value)}
            />
          </div>
          {error && <div className="cn-banner cn-banner-err"><div className="cn-banner-p">{error}</div></div>}
        </div>
        <div className="modal-foot">
          <button className="btn btn-ghost" onClick={onClose} disabled={submitting}>Cancel</button>
          <button className="btn btn-primary" onClick={submit} disabled={!canSubmit}>
            {submitting ? "Creating…" : "Create project"}
          </button>
        </div>
      </div>
    </div>
  );
}

// Local helper imports kept at the bottom so the page component reads top-down.
import { useCreateProject } from "@/hooks/use-projects";

/** Same shape as RepositoryListPage's lastActivity formatter — kept inline so the
 *  page is self-contained while the small util gets factored out later. */
function formatRelative(iso: string): string {
  const ts = new Date(iso).getTime();
  if (!Number.isFinite(ts)) return "—";
  const seconds = Math.max(1, Math.floor((Date.now() - ts) / 1000));
  if (seconds < 60) return `${seconds}s ago`;
  const m = Math.floor(seconds / 60);
  if (m < 60) return `${m}m ago`;
  const h = Math.floor(m / 60);
  if (h < 24) return `${h}h ago`;
  const d = Math.floor(h / 24);
  if (d < 30) return `${d}d ago`;
  const mo = Math.floor(d / 30);
  if (mo < 12) return `${mo}mo ago`;
  return `${Math.floor(mo / 12)}y ago`;
}
