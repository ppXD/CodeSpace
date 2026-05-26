import { useState } from "react";
import { createFileRoute, useNavigate } from "@tanstack/react-router";

import { Ic } from "@/_imported/ai-code-space/icons";
import { ApiError } from "@/api/request";
import { useCreateProject, useDeleteProject, useProjects } from "@/hooks/use-projects";

/**
 * Projects list page. A Project is a Variable namespace — workflows reference its variables
 * as <c>project.{slug}.{var}</c>. Project has no FK to workflow / repo / run; it exists
 * purely to group variables for shared LLM/HTTP/etc. configuration across workflows.
 */
export const Route = createFileRoute("/_app/teams/$teamSlug/projects/")({
  component: ProjectsListPage,
});

function ProjectsListPage() {
  const { teamSlug } = Route.useParams();
  const navigate = useNavigate();
  const projects = useProjects();
  const create = useCreateProject();
  const remove = useDeleteProject();

  const [showCreate, setShowCreate] = useState(false);
  const [draftSlug, setDraftSlug] = useState("");
  const [draftName, setDraftName] = useState("");
  const [draftDescription, setDraftDescription] = useState("");
  const [createError, setCreateError] = useState<string | null>(null);

  const rows = projects.data ?? [];

  const handleCreate = async () => {
    setCreateError(null);
    try {
      const result = await create.mutateAsync({
        slug: draftSlug.trim(),
        name: draftName.trim(),
        description: draftDescription.trim() || null,
      });
      setShowCreate(false);
      setDraftSlug("");
      setDraftName("");
      setDraftDescription("");
      navigate({
        to: "/teams/$teamSlug/projects/$projectId",
        params: { teamSlug, projectId: result.projectId },
      });
    } catch (e) {
      if (e instanceof ApiError) setCreateError(e.message);
      else setCreateError("Create failed");
    }
  };

  const handleDelete = (projectId: string, name: string) => {
    if (confirm(`Delete project "${name}"? Its variables will be soft-deleted too. Workflows referencing project.${name}.X will fail validation on next save.`)) {
      remove.mutate(projectId);
    }
  };

  return (
    <section className="ct">
      <div className="ct-head" style={{ paddingBottom: 18 }}>
        <div className="ct-crumbs">
          <span>Projects</span>
        </div>
        <div className="ct-title-row">
          <h1 className="ct-title">Projects</h1>
          <div className="ct-actions">
            <button className="btn btn-primary" onClick={() => setShowCreate(true)}>
              <Ic.Plus size={14} /> Add project
            </button>
          </div>
        </div>
        <p className="ct-subtle" style={{ marginTop: 8 }}>
          A Project is a variable namespace. Workflows reference variables as <code>project.&#123;slug&#125;.&#123;name&#125;</code>.
        </p>
      </div>

      <div className="ct-body">
        {projects.isLoading && (
          <div className="ct-empty"><div className="ct-empty-h">Loading…</div></div>
        )}

        {projects.error instanceof ApiError && (
          <div className="cn-banner cn-banner-err" style={{ margin: 16 }}>
            <div className="cn-banner-h">Couldn't load projects</div>
            <div className="cn-banner-p">{projects.error.message}</div>
          </div>
        )}

        {!projects.isLoading && !projects.error && rows.length === 0 && (
          <div className="ct-empty">
            <div className="ct-empty-h">No projects yet</div>
            <div className="ct-empty-p">Click <strong>Add project</strong> to create your first variable namespace.</div>
          </div>
        )}

        {rows.length > 0 && (
          <table className="ct-table">
            <thead>
              <tr>
                <th>Slug</th>
                <th>Name</th>
                <th>Description</th>
                <th style={{ width: 80 }}></th>
              </tr>
            </thead>
            <tbody>
              {rows.map((p) => (
                <tr
                  key={p.id}
                  style={{ cursor: "pointer" }}
                  onClick={() => navigate({ to: "/teams/$teamSlug/projects/$projectId", params: { teamSlug, projectId: p.id } })}
                >
                  <td><code>{p.slug}</code></td>
                  <td>{p.name}</td>
                  <td className="ct-subtle">{p.description ?? ""}</td>
                  <td>
                    <button
                      className="btn btn-ghost btn-sm"
                      onClick={(e) => { e.stopPropagation(); handleDelete(p.id, p.name); }}
                    >
                      <Ic.Trash size={12} />
                    </button>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </div>

      {showCreate && (
        <div className="md-backdrop" onClick={() => setShowCreate(false)}>
          <div className="md-card" onClick={(e) => e.stopPropagation()} style={{ maxWidth: 480 }}>
            <div className="md-head">
              <h2 className="md-title">New project</h2>
              <button className="btn btn-ghost btn-sm" onClick={() => setShowCreate(false)}>
                <Ic.X size={14} />
              </button>
            </div>
            <div className="md-body" style={{ display: "flex", flexDirection: "column", gap: 12 }}>
              <label className="fld">
                <span className="fld-label">Slug</span>
                <input
                  className="fld-input"
                  value={draftSlug}
                  onChange={(e) => setDraftSlug(e.target.value)}
                  placeholder="Backend"
                  autoFocus
                />
                <span className="fld-hint">Used in variable refs: <code>project.{draftSlug || "{slug}"}.X</code>. Alphanumeric + underscore + hyphen, 1–64 chars.</span>
              </label>
              <label className="fld">
                <span className="fld-label">Name</span>
                <input
                  className="fld-input"
                  value={draftName}
                  onChange={(e) => setDraftName(e.target.value)}
                  placeholder="Backend Services"
                />
              </label>
              <label className="fld">
                <span className="fld-label">Description (optional)</span>
                <textarea
                  className="fld-input"
                  rows={2}
                  value={draftDescription}
                  onChange={(e) => setDraftDescription(e.target.value)}
                />
              </label>
              {createError && (
                <div className="cn-banner cn-banner-err">
                  <div className="cn-banner-p">{createError}</div>
                </div>
              )}
            </div>
            <div className="md-foot">
              <button className="btn btn-ghost" onClick={() => setShowCreate(false)}>Cancel</button>
              <button
                className="btn btn-primary"
                onClick={handleCreate}
                disabled={!draftSlug.trim() || !draftName.trim() || create.isPending}
              >
                {create.isPending ? "Creating…" : "Create"}
              </button>
            </div>
          </div>
        </div>
      )}
    </section>
  );
}
