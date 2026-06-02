import { useEffect, useState } from "react";
import { createPortal } from "react-dom";

import { Ic } from "@/_imported/ai-code-space/icons";
import { ApiError } from "@/api/request";
import { useLinkIdentityByPat } from "@/hooks/use-identities";

interface IdentityLinkModalProps {
  /** The provider instance to link the caller's identity on. */
  providerInstanceId: string;
  /** Human label for the dialog header, e.g. "GitLab · gitlab.com". */
  providerLabel: string;
  onClose: () => void;
  /** Called after a successful link (before close) — e.g. to retry the action that prompted it. */
  onLinked?: () => void;
}

/**
 * Generic "connect your identity" dialog (Model B). Paste a personal access token; the backend
 * probes it (whoami) before storing. Reused by the proactive Settings list AND — in a later PR —
 * by the global `actor_identity_required` interceptor, so the link UX is identical everywhere.
 * Warm-theme `.mdl` shell.
 */
export function IdentityLinkModal({ providerInstanceId, providerLabel, onClose, onLinked }: IdentityLinkModalProps) {
  const [token, setToken] = useState("");
  const [error, setError] = useState<string | null>(null);
  const link = useLinkIdentityByPat();

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => { if (e.key === "Escape") onClose(); };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [onClose]);

  const submit = () => {
    if (token.trim() === "" || link.isPending) return;
    setError(null);
    link.mutate(
      { providerInstanceId, accessToken: token.trim() },
      {
        onSuccess: () => { onLinked?.(); onClose(); },
        onError: (e) => setError(e instanceof ApiError ? e.message : "Could not link this token."),
      },
    );
  };

  return createPortal(
    <>
      <div className="mdl-mask" onClick={onClose} />
      <div className="mdl" role="dialog" aria-modal="true" aria-label="Connect identity">
        <div className="mdl-head">
          <div className="mdl-title-wrap">
            <div className="mdl-title">Connect to {providerLabel}</div>
            <div className="mdl-sub">Paste a personal access token. Actions you trigger will be attributed to this account.</div>
          </div>
          <button className="mdl-x" onClick={onClose} title="Close"><Ic.X size={14} /></button>
        </div>

        <div className="mdl-body">
          <div className="wf-form">
            <div className="wf-form-row">
              <span className="wf-form-label">Personal access token</span>
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
          <button className="btn btn-primary" onClick={submit} disabled={token.trim() === "" || link.isPending}>
            {link.isPending ? "Connecting…" : "Connect"}
          </button>
        </div>
      </div>
    </>,
    document.body,
  );
}
