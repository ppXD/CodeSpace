import { useState } from "react";

import { Ic } from "@/_imported/ai-code-space/icons";
import type { ModelCredentialSummary } from "@/api/modelCredentials";
import { ApiError } from "@/api/request";
import { useConfirm } from "@/components/dialog";
import { useModelCredentials, useRevokeModelCredential } from "@/hooks/use-model-credentials";
import { providerForm } from "@/lib/providerForms";

import { ModelCredentialModal } from "./ModelCredentialModal";

type ModalState = { mode: "add" } | { mode: "edit"; credential: ModelCredentialSummary } | null;

/**
 * Team Model Credentials settings. Lists the team's model keys (masked — never the secret) and lets an
 * operator add / edit / revoke them. Same `ct` + `tbl` rhythm as the Agents / Workflows lists. The
 * provider + base URL + a free model id on the agent node are the three knobs that let a team point a
 * harness at any compatible endpoint (a self-hosted Qwen/DeepSeek gateway, OpenRouter, …).
 */
export function ModelCredentialsPage() {
  const creds = useModelCredentials();
  const revoke = useRevokeModelCredential();
  const confirm = useConfirm();
  const [modal, setModal] = useState<ModalState>(null);

  const rows = creds.data ?? [];

  const onRevoke = async (c: ModelCredentialSummary) => {
    const ok = await confirm({
      title: `Revoke "${c.displayName}"?`,
      message: "Agent runs that resolve to this credential will fall back to a team default or the operator key, or fail if none applies. This can't be undone.",
      confirmLabel: "Revoke",
      destructive: true,
    });
    if (ok) revoke.mutate(c.id);
  };

  return (
    <section className="ct">
      <div className="ct-head" style={{ paddingBottom: 18 }}>
        <div className="ct-crumbs"><span className="cur">Model credentials</span></div>
        <div className="ct-title-row" style={{ display: "flex", justifyContent: "space-between", alignItems: "center" }}>
          <h1 className="ct-title">Model credentials</h1>
          <button className="btn btn-primary" onClick={() => setModal({ mode: "add" })}>
            <Ic.Plus size={14} /> Add credential
          </button>
        </div>
      </div>

      <div className="ct-body">
        <div className="cn-banner" style={{ margin: 16 }}>
          <div className="cn-banner-h">How agents authenticate</div>
          <div className="cn-banner-p">
            A run uses the credential pinned on its node or agent, else the team's credential for that provider, else the
            operator's global key. With none configured, runs rely on the operator key — add a team credential here to override it.
          </div>
        </div>

        {creds.isLoading && <div className="ct-empty"><div className="ct-empty-h">Loading…</div></div>}

        {creds.error instanceof ApiError && (
          <div className="cn-banner cn-banner-err" style={{ margin: 16 }}>
            <div className="cn-banner-h">Couldn't load model credentials</div>
            <div className="cn-banner-p">{creds.error.message}</div>
          </div>
        )}

        {!creds.isLoading && !creds.error && rows.length === 0 && (
          <div className="ct-empty">
            <div className="ct-empty-h">No model credentials yet</div>
            <div className="ct-empty-p">Add a provider key (Anthropic, OpenAI, OpenRouter, a self-hosted gateway, …) so this team's agents authenticate with its own key.</div>
          </div>
        )}

        {!creds.isLoading && !creds.error && rows.length > 0 && (
          <table className="tbl">
            <thead>
              <tr>
                <th style={{ width: "26%" }}>Credential</th>
                <th>Provider</th>
                <th>Key</th>
                <th>Base URL</th>
                <th>Status</th>
                <th />
              </tr>
            </thead>
            <tbody>
              {rows.map((c) => (
                <tr key={c.id}>
                  <td>
                    <div className="repo-cell">
                      <div className="repo-mark" style={{ background: "var(--accent-soft)", color: "var(--accent)" }}><Ic.Key size={14} /></div>
                      <div className="repo-info"><div className="repo-name">{c.displayName}</div></div>
                    </div>
                  </td>
                  <td><span className="wf-version">{providerForm(c.provider)?.label ?? c.provider}</span></td>
                  <td>{c.keyHint ? <span className="wf-version" style={{ fontFamily: "var(--font-mono)" }}>{c.keyHint}</span> : <span className="wf-trigger-muted">no key</span>}</td>
                  <td>{c.baseUrl ? <span className="wf-trigger-muted">{c.baseUrl}</span> : <span className="wf-trigger-muted">default</span>}</td>
                  <td>{c.status === "Active" ? <span className="wf-trigger-chip">Active</span> : <span className="wf-trigger-muted">{c.status}</span>}</td>
                  <td style={{ textAlign: "right", whiteSpace: "nowrap" }}>
                    <button className="btn btn-ghost btn-sm" onClick={() => setModal({ mode: "edit", credential: c })}>Edit</button>
                    <button className="btn btn-ghost btn-sm" onClick={() => onRevoke(c)} disabled={revoke.isPending}>Revoke</button>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </div>

      {modal?.mode === "add" && <ModelCredentialModal onClose={() => setModal(null)} />}
      {modal?.mode === "edit" && <ModelCredentialModal editing={modal.credential} onClose={() => setModal(null)} />}
    </section>
  );
}
