import { useState } from "react";

import { Ic } from "@/_imported/ai-code-space/icons";
import type { ModelCredentialSummary } from "@/api/modelCredentials";
import { ApiError } from "@/api/request";
import { useConfirm } from "@/components/dialog";
import { useCredentialedModelList, useModelCredentials, useRevokeModelCredential } from "@/hooks/use-model-credentials";
import { providerForm } from "@/lib/providerForms";

import { ModelCredentialModal } from "./ModelCredentialModal";
import { ModelCredentialModelsModal } from "./ModelCredentialModelsModal";
import { ProviderLogo } from "./ProviderLogo";

/** A credential's model count as a clickable card row that opens the models manager. */
function CredentialModelsRow({ credentialId, onManage }: { credentialId: string; onManage: () => void }) {
  const list = useCredentialedModelList(credentialId);
  const n = list.data?.length ?? 0;
  const label = list.isLoading ? "models…" : n === 0 ? "No models — add one" : `${n} ${n === 1 ? "model" : "models"}`;
  return (
    <button type="button" className="mc-card-row mc-card-modelsrow" onClick={onManage} title="Manage models">
      <Ic.Box size={12} />
      <span>{label}</span>
      <Ic.ChevronRight size={13} />
    </button>
  );
}

type ModalState = { mode: "add" } | { mode: "edit"; credential: ModelCredentialSummary } | null;

/**
 * Team Model Credentials settings. A card per credential (provider logo + masked key — never the secret),
 * with add / edit / revoke. The provider + base URL on the credential, plus a free model id on the agent
 * node, are the three knobs that point a harness at any compatible endpoint (a self-hosted Qwen/DeepSeek
 * gateway, OpenRouter, …).
 */
export function ModelCredentialsPage() {
  const creds = useModelCredentials();
  const revoke = useRevokeModelCredential();
  const confirm = useConfirm();
  const [modal, setModal] = useState<ModalState>(null);
  const [modelsFor, setModelsFor] = useState<ModelCredentialSummary | null>(null);

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

  // Rendered inside the Settings layout (it owns the "Settings" header + tab strip), so this is body-only.
  return (
    <>
      <div style={{ display: "flex", justifyContent: "flex-end", padding: "16px 16px 0" }}>
        <button className="btn btn-primary" onClick={() => setModal({ mode: "add" })}>
          <Ic.Plus size={14} /> Add credential
        </button>
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
        <div className="mc-grid">
          {rows.map((c) => (
            <div key={c.id} className="mc-card">
              <div className="mc-card-head">
                <ProviderLogo provider={c.provider} />
                <div className="mc-card-id">
                  <div className="mc-card-name" title={c.displayName}>{c.displayName}</div>
                  <div className="mc-card-prov">{providerForm(c.provider)?.label ?? c.provider}</div>
                </div>
                {c.status === "Active"
                  ? <span className="wf-trigger-chip">Active</span>
                  : <span className="wf-trigger-muted">{c.status}</span>}
              </div>

              <div className="mc-card-rows">
                <div className="mc-card-row">
                  <Ic.Key size={12} />
                  {c.keyHint
                    ? <code>{c.keyHint}</code>
                    : c.keyUnreadable
                      ? <span className="mc-key-dead" title="The stored key can no longer be decrypted — edit this credential to re-enter it.">key unreadable — re-enter</span>
                      : <span>no key</span>}
                </div>
                <div className="mc-card-row">
                  <Ic.Link size={12} />
                  <span title={c.baseUrl ?? undefined}>{c.baseUrl ?? "default endpoint"}</span>
                </div>
                <CredentialModelsRow credentialId={c.id} onManage={() => setModelsFor(c)} />
              </div>

              <div className="mc-card-foot">
                <button className="btn btn-ghost" onClick={() => setModal({ mode: "edit", credential: c })}>Edit</button>
                <button className="btn btn-ghost" onClick={() => onRevoke(c)} disabled={revoke.isPending}>Revoke</button>
              </div>
            </div>
          ))}
        </div>
      )}

      {modal?.mode === "add" && <ModelCredentialModal onClose={() => setModal(null)} />}
      {modal?.mode === "edit" && <ModelCredentialModal editing={modal.credential} onClose={() => setModal(null)} />}
      {modelsFor && <ModelCredentialModelsModal credential={modelsFor} onClose={() => setModelsFor(null)} />}
    </>
  );
}
