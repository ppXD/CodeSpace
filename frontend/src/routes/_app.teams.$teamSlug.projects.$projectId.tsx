import { useEffect, useState } from "react";
import { createFileRoute, useNavigate } from "@tanstack/react-router";

import { Ic } from "@/_imported/ai-code-space/icons";
import { ApiError } from "@/api/request";
import { VariableTablePanel } from "@/components/workflows/VariableTablePanel";
import { useDeleteProject, useProject, useUpdateProject } from "@/hooks/use-projects";

/**
 * Project detail page. Shows project metadata (slug is immutable; name + description are
 * editable) and a Variables panel scoped to this project — values addressable from
 * workflows as <code>project.{slug}.{name}</code>.
 */
export const Route = createFileRoute("/_app/teams/$teamSlug/projects/$projectId")({
  component: ProjectDetailPage,
});

function ProjectDetailPage() {
  const { teamSlug, projectId } = Route.useParams();
  const navigate = useNavigate();
  const project = useProject(projectId);
  const update = useUpdateProject(projectId);
  const remove = useDeleteProject();

  const [nameDraft, setNameDraft] = useState("");
  const [descDraft, setDescDraft] = useState("");
  const [dirty, setDirty] = useState(false);

  useEffect(() => {
    if (project.data) {
      setNameDraft(project.data.name);
      setDescDraft(project.data.description ?? "");
      setDirty(false);
    }
  }, [project.data]);

  const handleSave = async () => {
    await update.mutateAsync({ name: nameDraft.trim(), description: descDraft.trim() || null });
    setDirty(false);
  };

  const handleDelete = async () => {
    if (!project.data) return;
    if (!confirm(`Delete project "${project.data.name}"? Variables will be soft-deleted; workflows referencing project.${project.data.slug}.X will fail validation on next save.`)) return;
    await remove.mutateAsync(projectId);
    navigate({ to: "/teams/$teamSlug/projects", params: { teamSlug } });
  };

  if (project.isLoading) {
    return <section className="ct"><div className="ct-empty"><div className="ct-empty-h">Loading…</div></div></section>;
  }

  if (project.error instanceof ApiError) {
    return (
      <section className="ct">
        <div className="cn-banner cn-banner-err" style={{ margin: 16 }}>
          <div className="cn-banner-h">Couldn't load project</div>
          <div className="cn-banner-p">{project.error.message}</div>
        </div>
      </section>
    );
  }

  if (!project.data) return null;

  const p = project.data;

  return (
    <section className="ct">
      <div className="ct-head" style={{ paddingBottom: 18 }}>
        <div className="ct-crumbs">
          <a onClick={() => navigate({ to: "/teams/$teamSlug/projects", params: { teamSlug } })} style={{ cursor: "pointer" }}>Projects</a>
          <span>/</span>
          <span>{p.slug}</span>
        </div>
        <div className="ct-title-row">
          <h1 className="ct-title">{p.name}</h1>
          <div className="ct-actions">
            <button className="btn btn-ghost" onClick={handleDelete} disabled={remove.isPending}>
              <Ic.Trash size={14} /> Delete
            </button>
          </div>
        </div>
        <p className="ct-subtle" style={{ marginTop: 8 }}>
          Variables in this project are addressable from workflows as <code>project.{p.slug}.&#123;name&#125;</code>.
        </p>
      </div>

      <div className="ct-body" style={{ display: "flex", flexDirection: "column", gap: 24, padding: 16 }}>
        {/* Project metadata */}
        <div style={{ display: "flex", flexDirection: "column", gap: 12, maxWidth: 600 }}>
          <label className="fld">
            <span className="fld-label">Slug</span>
            <input className="fld-input" value={p.slug} disabled readOnly />
            <span className="fld-hint">Immutable. Changing the slug would invalidate every <code>project.{p.slug}.X</code> reference.</span>
          </label>
          <label className="fld">
            <span className="fld-label">Name</span>
            <input
              className="fld-input"
              value={nameDraft}
              onChange={(e) => { setNameDraft(e.target.value); setDirty(true); }}
            />
          </label>
          <label className="fld">
            <span className="fld-label">Description</span>
            <textarea
              className="fld-input"
              rows={2}
              value={descDraft}
              onChange={(e) => { setDescDraft(e.target.value); setDirty(true); }}
            />
          </label>
          {dirty && (
            <div>
              <button className="btn btn-primary" onClick={handleSave} disabled={update.isPending || !nameDraft.trim()}>
                {update.isPending ? "Saving…" : "Save changes"}
              </button>
            </div>
          )}
        </div>

        {/* Variables for this project */}
        <VariableTablePanel
          scope="Project"
          projectId={projectId}
          refPrefix={`project.${p.slug}`}
          title="Variables"
          subtitle={`Available to any workflow as project.${p.slug}.{name}`}
          tip={`Use {{ project.${p.slug}.NAME }} in workflow nodes to reference these values. Secrets are encrypted at rest; their plaintext is never returned by the API.`}
          emptyHint="No variables yet. Add one to expose it via project."
        />
      </div>
    </section>
  );
}
