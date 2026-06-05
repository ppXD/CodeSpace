import { createFileRoute, useNavigate } from "@tanstack/react-router";

import { Ic } from "@/_imported/ai-code-space/icons";
import { ApiError } from "@/api/request";
import { useConfirm } from "@/components/dialog/dialog-context";
import { useCreateEmptyWorkflow, useCreateWorkflowFromTemplate, WORKFLOW_TEMPLATES, type WorkflowTemplate } from "@/hooks/use-workflow-templates";
import { useDeleteWorkflow, useSetWorkflowEnabled, useWorkflows } from "@/hooks/use-workflows";

/**
 * Workflows list. Same compact header rhythm as the Repositories page — title + a
 * single primary action, no marketing copy. The "+ Add workflow" button creates a
 * minimal seed definition (trigger → terminal) and immediately jumps the user into
 * the visual editor; no template prompt, no modal.
 */
export const Route = createFileRoute("/_app/teams/$teamSlug/workflows/")({
  component: WorkflowsListPage,
});

function WorkflowsListPage() {
  const { teamSlug } = Route.useParams();
  const navigate = useNavigate();
  const workflows = useWorkflows();
  const setEnabled = useSetWorkflowEnabled();
  const remove = useDeleteWorkflow();
  const createEmpty = useCreateEmptyWorkflow();
  const fromTemplate = useCreateWorkflowFromTemplate();
  const confirm = useConfirm();

  const rows = workflows.data ?? [];

  const handleAdd = async () => {
    const result = await createEmpty.mutateAsync();
    // The canvas IS the workflow detail (Dify pattern) — no separate /edit route.
    navigate({
      to: "/teams/$teamSlug/workflows/$workflowId",
      params: { teamSlug, workflowId: result.id },
    });
  };

  const handleTemplate = async (template: WorkflowTemplate) => {
    const result = await fromTemplate.mutateAsync(template);
    navigate({
      to: "/teams/$teamSlug/workflows/$workflowId",
      params: { teamSlug, workflowId: result.id },
    });
  };

  const handleDelete = async (workflowId: string, name: string) => {
    // Themed confirm dialog instead of the browser's native window.confirm —
    // matches every other destructive action in the SPA (see dialog-context).
    const ok = await confirm({
      title: "Delete workflow?",
      message: (
        <>
          <strong>{name}</strong> will be removed. Runs already in flight will finish on their own; new triggers stop firing immediately.
        </>
      ),
      confirmLabel: "Delete",
      destructive: true,
    });
    if (!ok) return;
    remove.mutate(workflowId);
  };

  return (
    <section className="ct">
      {/* paddingBottom on ct-head: without tabs the title row would sit flush against
          the table border below — Repositories gets its breathing room from the tabs
          strip. We add explicit bottom padding here so the rhythm matches. */}
      <div className="ct-head" style={{ paddingBottom: 18 }}>
        <div className="ct-crumbs">
          <span className="cur">Workflows</span>
        </div>
        <div className="ct-title-row">
          <h1 className="ct-title">Workflows</h1>
          <div className="ct-actions">
            <details className="wf-tpl-menu">
              <summary className="btn">Start from template</summary>
              <div className="wf-tpl-menu-panel">
                {WORKFLOW_TEMPLATES.map((t) => (
                  <button
                    key={t.id}
                    type="button"
                    className="wf-tpl-item"
                    disabled={fromTemplate.isPending}
                    onClick={() => handleTemplate(t)}
                  >
                    <span className="wf-tpl-item-name">{t.name}</span>
                    <span className="wf-tpl-item-desc">{t.description}</span>
                  </button>
                ))}
              </div>
            </details>
            <button className="btn btn-primary" onClick={handleAdd} disabled={createEmpty.isPending}>
              <Ic.Plus size={14} /> {createEmpty.isPending ? "Creating…" : "Add workflow"}
            </button>
          </div>
        </div>
      </div>

      <div className="ct-body">
        {workflows.isLoading && (
          <div className="ct-empty"><div className="ct-empty-h">Loading…</div></div>
        )}

        {workflows.error instanceof ApiError && (
          <div className="cn-banner cn-banner-err" style={{ margin: 16 }}>
            <div className="cn-banner-h">Couldn't load workflows</div>
            <div className="cn-banner-p">{workflows.error.message}</div>
          </div>
        )}

        {!workflows.isLoading && !workflows.error && rows.length === 0 && (
          <div className="ct-empty">
            <div className="ct-empty-h">No workflows yet</div>
            <div className="ct-empty-p">Click <strong>Add workflow</strong> to open the canvas editor.</div>
          </div>
        )}

        {!workflows.isLoading && !workflows.error && rows.length > 0 && (
          <table className="tbl">
            <thead>
              <tr>
                <th style={{ width: "44%" }}>Workflow</th>
                <th>Triggers</th>
                <th>Version</th>
                <th>Updated</th>
                <th className="col-right" />
              </tr>
            </thead>
            <tbody>
              {rows.map((w) => (
                <tr
                  key={w.id}
                  data-status={w.enabled ? "active" : "paused"}
                  onClick={() =>
                    navigate({
                      to: "/teams/$teamSlug/workflows/$workflowId",
                      params: { teamSlug, workflowId: w.id },
                    })
                  }
                >
                  <td>
                    <div className="repo-cell">
                      <div
                        className="repo-mark"
                        style={{ background: "var(--accent-soft)", color: "var(--accent)" }}
                      >
                        <Ic.Workflow size={14} />
                      </div>
                      <div className="repo-info">
                        <div className="repo-name">
                          {w.name}
                          {!w.enabled && <span className="wf-badge wf-badge-disabled">disabled</span>}
                        </div>
                        {/* Wrap in .repo-path-desc so a long description ellipsis-truncates
                            on one line instead of wrapping the row taller. See the CSS rule
                            near .repo-info for the bounding model. */}
                        {w.description && <div className="repo-path"><span className="repo-path-desc" title={w.description}>{w.description}</span></div>}
                      </div>
                    </div>
                  </td>
                  <td>
                    <div className="wf-triggers">
                      {w.activationTypeKeys.length === 0 ? (
                        <span className="wf-trigger-muted">manual only</span>
                      ) : (
                        w.activationTypeKeys.map((t) => (
                          <span key={t} className="wf-trigger-chip">{t}</span>
                        ))
                      )}
                    </div>
                  </td>
                  <td><span className="wf-version">v{w.latestVersion}</span></td>
                  <td>{formatRelative(w.lastModifiedDate)}</td>
                  <td className="col-right" onClick={(e) => e.stopPropagation()}>
                    <div className="wf-row-actions">
                      <button
                        className="btn btn-ghost"
                        onClick={() =>
                          navigate({
                            to: "/teams/$teamSlug/workflows/$workflowId/runs",
                            params: { teamSlug, workflowId: w.id },
                          })
                        }
                        title="View runs"
                      >
                        <Ic.Clock size={13} />
                      </button>
                      <button
                        className="btn btn-ghost"
                        onClick={() => setEnabled.mutate({ workflowId: w.id, enabled: !w.enabled })}
                        title={w.enabled ? "Disable workflow" : "Enable workflow"}
                      >
                        {w.enabled ? <Ic.Pause size={13} /> : <Ic.Play size={13} />}
                      </button>
                      <button
                        className="btn btn-ghost"
                        onClick={() => handleDelete(w.id, w.name)}
                        title="Delete workflow"
                      >
                        <Ic.Trash size={13} />
                      </button>
                    </div>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </div>
    </section>
  );
}

function formatRelative(iso: string): string {
  const date = new Date(iso);
  const seconds = Math.floor((Date.now() - date.getTime()) / 1000);

  if (seconds < 60) return `${seconds}s ago`;
  if (seconds < 3600) return `${Math.floor(seconds / 60)}m ago`;
  if (seconds < 86400) return `${Math.floor(seconds / 3600)}h ago`;
  if (seconds < 86400 * 30) return `${Math.floor(seconds / 86400)}d ago`;

  return date.toLocaleDateString();
}
