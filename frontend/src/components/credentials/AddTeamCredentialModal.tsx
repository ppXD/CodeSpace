import { useState } from "react";

import type { ProviderInstanceSummary } from "@/api/types";
import { useAddGroupAccessToken } from "@/hooks/use-credentials";
import { Ic } from "@/_imported/ai-code-space/icons";

/**
 * Guided, foolproof "add a team service credential" — paste a GitLab Group Access Token, which is
 * owned by the group (not a person) so a repo bound through it survives anyone leaving. Three short
 * steps tell the operator exactly where to get the token. GitLab-only for now (the one provider with
 * a paste-able group token); GitHub gets its App flow later. Admin-gated by the caller.
 */
export function AddTeamCredentialModal({ instances, onClose, onAdded }: { instances: ProviderInstanceSummary[]; onClose: () => void; onAdded: () => void }) {
  const gitlab = instances.filter((i) => i.provider === "GitLab");

  const [providerInstanceId, setProviderInstanceId] = useState(gitlab[0]?.id ?? "");
  const [displayName, setDisplayName] = useState("");
  const [token, setToken] = useState("");
  const add = useAddGroupAccessToken();

  const canSubmit = providerInstanceId.length > 0 && displayName.trim().length > 0 && token.trim().length > 0 && !add.isPending;

  const submit = async () => {
    if (!canSubmit) return;
    try {
      await add.mutateAsync({ providerInstanceId, displayName: displayName.trim(), token: token.trim() });
      onAdded();
    } catch {
      /* surfaced via add.error below */
    }
  };

  return (
    <>
      <div className="mdl-mask" />
      <div className="mdl" role="dialog" aria-modal="true">
        <div className="mdl-head">
          <div className="mdl-title-wrap">
            <div className="mdl-title">Add a team service credential</div>
            <div className="mdl-sub">A GitLab Group Access Token owned by the team — it survives anyone leaving.</div>
          </div>
          <button className="mdl-x" onClick={onClose} aria-label="Close"><Ic.X size={14} /></button>
        </div>
        <div className="mdl-body">
          {gitlab.length === 0 ? (
            <div className="ct-empty">
              <div className="ct-empty-h">No GitLab connection yet</div>
              <div className="ct-empty-p">Connect a GitLab provider first, then add a team credential.</div>
            </div>
          ) : (
            <>
              <div className="form-hint" style={{ marginBottom: 12 }}>
                In GitLab: open your <strong>group → Settings → Access Tokens</strong>, create one with role
                {" "}<strong>Maintainer</strong> and scopes <code>api</code>, <code>write_repository</code>, then paste it below.
              </div>

              {gitlab.length > 1 && (
                <div className="form-row">
                  <label>GitLab connection</label>
                  <select value={providerInstanceId} onChange={(e) => setProviderInstanceId(e.target.value)}>
                    {gitlab.map((i) => <option key={i.id} value={i.id}>{i.displayName}</option>)}
                  </select>
                </div>
              )}

              <div className="form-row">
                <label>Name</label>
                <input autoFocus value={displayName} onChange={(e) => setDisplayName(e.target.value)} placeholder="Acme team · GitLab" />
              </div>

              <div className="form-row">
                <label>Group Access Token</label>
                <input type="password" value={token} onChange={(e) => setToken(e.target.value)} placeholder="glpat-…" />
              </div>

              {add.error instanceof Error && <div className="cn-banner cn-banner-err"><div className="cn-banner-p">{add.error.message}</div></div>}
            </>
          )}
        </div>
        <div className="mdl-foot">
          <div className="ct-spacer" />
          <button className="btn btn-ghost" onClick={onClose} disabled={add.isPending}>Cancel</button>
          <button className="btn btn-primary" onClick={submit} disabled={!canSubmit}>{add.isPending ? "Adding…" : "Add team credential"}</button>
        </div>
      </div>
    </>
  );
}
