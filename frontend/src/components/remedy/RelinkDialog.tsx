import { useEffect, useMemo, useState } from "react";
import { createPortal } from "react-dom";

import { Ic } from "@/_imported/ai-code-space/icons";
import { ApiError } from "@/api/request";
import { useCredentials, useRelinkSuggestions } from "@/hooks/use-credentials";
import { useMe } from "@/hooks/use-me";
import { useRelinkRepositoryCredential } from "@/hooks/use-repositories";
import { bindableCredentials, credentialOwnershipLabel, sortCredentialsByPreference } from "@/lib/credentialOrdering";
import { isLiveCredential } from "@/lib/credentialHealth";

export interface RelinkRepository {
  id: string;
  fullPath: string;
}

interface RelinkDialogProps {
  /** The repositories to move onto the credential that is picked — one, or every one a run touched. */
  repositories: RelinkRepository[];
  /** Only a credential of the repositories' own provider connection can take them; a token never talks to another host. */
  providerInstanceId: string;
  /** The credential they are moving off, when there is one: it is asked for suggestions, which put the person's own connection first. */
  fromCredentialId?: string | null;
  onClose: () => void;
  /** Called once every repository is on the new credential, before the dialog closes. */
  onRelinked?: () => void;
  /** Offered when nothing is left to pick: the Providers dialog is where a working credential gets connected. */
  onOpenProviders?: () => void;
}

/**
 * Pick the credential a repository (or several) runs through from now on. Only credentials the caller may bind with are offered —
 * their own and the team's service tokens, exactly the set the Add Repository picker offers — because a repository bound through a
 * teammate's personal token acts as that person, and stops the day they leave. Suggestions from the backend order the list (the
 * connection made before the dead one was disconnected comes first) and never widen it.
 */
export function RelinkDialog({ repositories, providerInstanceId, fromCredentialId = null, onClose, onRelinked, onOpenProviders }: RelinkDialogProps) {
  const credentials = useCredentials();
  const me = useMe();
  const suggestions = useRelinkSuggestions(fromCredentialId);
  const relink = useRelinkRepositoryCredential();
  const [error, setError] = useState<string | null>(null);
  const [pickedId, setPickedId] = useState<string | null>(null);

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => { if (e.key === "Escape") onClose(); };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [onClose]);

  const candidates = useMemo(() => {
    const live = (credentials.data ?? []).filter(c => c.providerInstanceId === providerInstanceId && c.id !== fromCredentialId && isLiveCredential(c));
    const rank = new Map((suggestions.data ?? []).map((s, index) => [s.credentialId, index]));

    return sortCredentialsByPreference(bindableCredentials(live, me.data?.id)).sort((a, b) => (rank.get(a.id) ?? Number.MAX_SAFE_INTEGER) - (rank.get(b.id) ?? Number.MAX_SAFE_INTEGER));
  }, [credentials.data, me.data?.id, providerInstanceId, fromCredentialId, suggestions.data]);

  const connectedBefore = useMemo(() => new Set((suggestions.data ?? []).filter(s => s.connectedBeforeDisconnect).map(s => s.credentialId)), [suggestions.data]);

  const pick = async (credentialId: string) => {
    setError(null);
    setPickedId(credentialId);

    try {
      for (const repository of repositories) await relink.mutateAsync({ repositoryId: repository.id, credentialId });
      onRelinked?.();
      onClose();
    } catch (err) {
      setPickedId(null);
      setError(err instanceof ApiError || err instanceof Error ? err.message : "Couldn't re-link.");
    }
  };

  const what = repositories.length === 1 ? repositories[0].fullPath : `${repositories.length} repositories`;

  return createPortal(
    <>
      <div className="mdl-mask" onClick={onClose} />
      <div className="mdl" role="dialog" aria-modal="true" aria-label="Re-link credential">
        <div className="mdl-head">
          <div className="mdl-title-wrap">
            <div className="mdl-title">Re-link {what}</div>
            <div className="mdl-sub">Pick the credential {repositories.length === 1 ? "it" : "they"} should run through from now on.</div>
          </div>
          <button className="mdl-x" onClick={onClose} title="Close"><Ic.X size={14} /></button>
        </div>

        <div className="mdl-body">
          {repositories.length > 1 && (
            <ul className="rmd-repos" aria-label="Repositories to re-link">
              {repositories.map(r => <li key={r.id}>{r.fullPath}</li>)}
            </ul>
          )}

          {credentials.isLoading && <div className="cn-loading"><Ic.Clock size={14} /> Loading…</div>}

          {!credentials.isLoading && candidates.length === 0 && (
            <div className="cn-empty">
              <div className="cn-empty-h">No other working credential</div>
              <div className="cn-empty-p">Nothing connected for this provider can take {repositories.length === 1 ? "this repository" : "these repositories"} yet — only your own connections and the team&rsquo;s service tokens are offered. Connect one, then come back and pick it here.</div>
              {onOpenProviders && <button className="btn" onClick={onOpenProviders}><Ic.Plus size={13} /> Open Providers…</button>}
            </div>
          )}

          {candidates.length > 0 && (
            <div className="rmd-candidates" role="list">
              {candidates.map(c => (
                <button key={c.id} className="rmd-candidate" role="listitem" onClick={() => void pick(c.id)} disabled={relink.isPending}>
                  <span className="rmd-candidate-main">
                    <Ic.Key size={13} />
                    <span className="rmd-candidate-name">{c.displayName}</span>
                    <span className="cn-cred-owner" data-team-service={c.ownership === "TeamService"}>{credentialOwnershipLabel(c)}</span>
                    {connectedBefore.has(c.id) && <span className="cn-status">connected before the old one was disconnected</span>}
                  </span>
                  <span className="rmd-candidate-go">{pickedId === c.id ? "Re-linking…" : "Use this"}</span>
                </button>
              ))}
            </div>
          )}

          {error && <div className="cn-banner cn-banner-err" role="alert" style={{ marginTop: 12 }}><div className="cn-banner-p">{error}</div></div>}
        </div>

        <div className="mdl-foot">
          <button className="btn btn-ghost" onClick={onClose} disabled={relink.isPending}>Cancel</button>
        </div>
      </div>
    </>,
    document.body,
  );
}
