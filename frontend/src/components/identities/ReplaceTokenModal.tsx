import { useEffect, useState } from "react";
import { createPortal } from "react-dom";

import { Ic } from "@/_imported/ai-code-space/icons";
import { ApiError } from "@/api/request";
import type { CredentialSummary } from "@/api/types";
import { useReplaceCredentialToken } from "@/hooks/use-credentials";

interface ReplaceTokenModalProps {
  /** The credential whose token is replaced. It keeps its id, so everything bound through it keeps working. */
  credential: Pick<CredentialSummary, "id" | "displayName">;
  /** Human label for the dialog header, e.g. "GitLab · gitlab.com". */
  providerLabel: string;
  onClose: () => void;
  /** Called after the token was replaced (before close). */
  onReplaced?: () => void;
}

/**
 * Paste a new token for a credential that already exists — the one a dead token left showing errors. The backend probes it
 * against the provider first and refuses a token that belongs to a different account (a different account is a new
 * connection, not a replacement), so a refusal here says what to do instead and the dialog stays open. Warm-theme `.mdl`
 * shell, like the connect-identity dialog it sits beside.
 */
export function ReplaceTokenModal({ credential, providerLabel, onClose, onReplaced }: ReplaceTokenModalProps) {
  const [token, setToken] = useState("");
  const [error, setError] = useState<string | null>(null);
  const replace = useReplaceCredentialToken();

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => { if (e.key === "Escape") onClose(); };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [onClose]);

  const submit = () => {
    if (token.trim() === "" || replace.isPending) return;
    setError(null);
    replace.mutate(
      { credentialId: credential.id, token: token.trim() },
      {
        onSuccess: () => { onReplaced?.(); onClose(); },
        onError: (e) => setError(e instanceof ApiError ? e.message : "Could not replace this token."),
      },
    );
  };

  return createPortal(
    <>
      <div className="mdl-mask" onClick={onClose} />
      <div className="mdl" role="dialog" aria-modal="true" aria-label="Replace token">
        <div className="mdl-head">
          <div className="mdl-title-wrap">
            <div className="mdl-title">Replace token for {providerLabel}</div>
            <div className="mdl-sub">Paste a new token for the same account. {credential.displayName} keeps its repositories and webhooks; nothing is moved.</div>
          </div>
          <button className="mdl-x" onClick={onClose} title="Close"><Ic.X size={14} /></button>
        </div>

        <div className="mdl-body">
          <div className="wf-form">
            <div className="wf-form-row">
              <span className="wf-form-label">New access token</span>
              <input
                className="wf-form-input"
                type="password"
                value={token}
                onChange={(e) => setToken(e.target.value)}
                onKeyDown={(e) => { if (e.key === "Enter") submit(); }}
                placeholder="glpat-… / ghp_…"
                autoFocus
              />
              {error && <span className="wf-form-help wf-form-help-err">{error}</span>}
            </div>
          </div>
        </div>

        <div className="mdl-foot">
          <button className="btn btn-ghost" onClick={onClose}>Cancel</button>
          <button className="btn btn-primary" onClick={submit} disabled={token.trim() === "" || replace.isPending}>
            {replace.isPending ? "Replacing…" : "Replace token"}
          </button>
        </div>
      </div>
    </>,
    document.body,
  );
}
