import { useEffect, useMemo, useState } from "react";
import { createPortal } from "react-dom";

import { Ic } from "@/_imported/ai-code-space/icons";
import { ApiError } from "@/api/request";
import type { CredentialSummary, RelinkSuggestion } from "@/api/types";
import { useCredentials, useRelinkCandidates, useRelinkSuggestions } from "@/hooks/use-credentials";
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
  /** The provider connection the repositories live under: the backend offers the credentials of this connection and of any other connection to the same host. */
  providerInstanceId: string;
  /** The credential they are moving off, when there is one: it is left out of the list, and the connection made before it was disconnected is flagged. */
  fromCredentialId?: string | null;
  onClose: () => void;
  /** Called once every repository is on the new credential, before the dialog closes. */
  onRelinked?: () => void;
  /** Offered when nothing is left to pick: the Providers dialog is where a working credential gets connected. */
  onOpenProviders?: () => void;
}

/** One row the dialog offers: the credential, whose it is, and — when the list spans connections — which connection it is on. */
interface Offer {
  id: string;
  displayName: string;
  ownerLabel: string;
  teamService: boolean;
  connectedBefore: boolean;
  connectionName: string | null;
  /** Picking it moves the repository to another connection to the same host. */
  changesConnection: boolean;
  /** Teammates whose acting-as on the repository stops working if it is picked — they act as themselves through the connection it leaves. */
  strandedActAs: string[];
}

/**
 * Pick the credential a repository (or several) runs through from now on. The backend says which credentials may take it: the caller's own and
 * the team's service tokens — never a teammate's personal token, which would make the repository act as that person and stop the day they leave
 * — on the repository's connection and on any other connection to the same host, already in the order to offer them (the repository's own
 * connection first, the caller's own before the team's). A credential on another connection moves the repository to that connection when it is picked,
 * which is said beside it, together with whose acting-as on the repository that strands. The dialog filters nothing of its own.
 *
 * Only against an API that predates that list (it answers the route with a 404) does the dialog build the list itself, as it did: the credentials of
 * the repositories' own connection that the caller may bind with, ordered by the suggestions for the credential being left.
 */
export function RelinkDialog({ repositories, providerInstanceId, fromCredentialId = null, onClose, onRelinked, onOpenProviders }: RelinkDialogProps) {
  const candidates = useRelinkCandidates(providerInstanceId, fromCredentialId);
  const olderApi = candidates.data === null;
  const credentials = useCredentials(olderApi);
  const me = useMe();
  const suggestions = useRelinkSuggestions(fromCredentialId, olderApi);
  const relink = useRelinkRepositoryCredential();
  const [error, setError] = useState<string | null>(null);
  const [pickedId, setPickedId] = useState<string | null>(null);

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => { if (e.key === "Escape") onClose(); };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [onClose]);

  const offers = useMemo(() => {
    if (candidates.data) return offersFromBackend(candidates.data, providerInstanceId, me.data?.name);

    return olderApi ? offersFromCredentialList(credentials.data ?? [], suggestions.data ?? [], providerInstanceId, fromCredentialId, me.data?.id) : [];
  }, [candidates.data, olderApi, credentials.data, suggestions.data, providerInstanceId, fromCredentialId, me.data?.id, me.data?.name]);

  const loading = candidates.isLoading || (olderApi && credentials.isLoading);
  const loadError = candidates.isError ? candidates.error.message : null;

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

          {loading && <div className="cn-loading"><Ic.Clock size={14} /> Loading…</div>}

          {loadError && <div className="cn-banner cn-banner-err" role="alert"><div className="cn-banner-p">Couldn&rsquo;t load the credentials this could be re-linked to: {loadError}</div></div>}

          {!loading && !loadError && offers.length === 0 && (
            <div className="cn-empty">
              <div className="cn-empty-h">No other working credential</div>
              <div className="cn-empty-p">Nothing connected for this provider can take {repositories.length === 1 ? "this repository" : "these repositories"} yet — only your own connections and the team&rsquo;s service tokens are offered. Connect one, then come back and pick it here.</div>
              {onOpenProviders && <button className="btn" onClick={onOpenProviders}><Ic.Plus size={13} /> Open Providers…</button>}
            </div>
          )}

          {offers.length > 0 && (
            <div className="rmd-candidates" role="list">
              {offers.map(o => (
                <button key={o.id} className="rmd-candidate" role="listitem" onClick={() => void pick(o.id)} disabled={relink.isPending}>
                  <span className="rmd-candidate-body">
                    <span className="rmd-candidate-main">
                      <Ic.Key size={13} />
                      <span className="rmd-candidate-name">{o.displayName}</span>
                      <span className="cn-cred-owner" data-team-service={o.teamService}>{o.ownerLabel}</span>
                      {o.connectionName && <span className="cn-status" data-connection="true">on {o.connectionName}</span>}
                      {o.connectedBefore && <span className="cn-status">connected before the old one was disconnected</span>}
                    </span>
                    {o.changesConnection && <span className="rmd-candidate-note">{moveNote(o, repositories.length)}</span>}
                  </span>
                  <span className="rmd-candidate-go">{pickedId === o.id ? "Re-linking…" : "Use this"}</span>
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

/**
 * The backend's list as the rows to show, in the order it gave. The connection a row is on is named when the list reaches past the repository's own
 * connection — counted with the repository's own connection among those it spans, so a list whose every credential is on one OTHER connection names it
 * too — and a team with a single connection sees what it always saw.
 */
function offersFromBackend(candidates: RelinkSuggestion[], providerInstanceId: string, myName: string | undefined): Offer[] {
  const spansConnections = new Set([providerInstanceId, ...candidates.map(c => c.providerInstanceId ?? providerInstanceId)]).size > 1;

  return candidates.map(c => ({
    id: c.credentialId,
    displayName: c.displayName,
    ownerLabel: c.ownership === "TeamService" ? "Team service" : myName ?? "You",
    teamService: c.ownership === "TeamService",
    connectedBefore: c.connectedBeforeDisconnect,
    connectionName: spansConnections && c.providerInstanceName ? c.providerInstanceName : null,
    changesConnection: c.providerInstanceId != null && c.providerInstanceId !== providerInstanceId,
    strandedActAs: c.strandedActAs ?? [],
  }));
}

/** What picking a credential on another connection does, said beside it: the repository moves there, and acting as the people whose identity lives only on the connection it leaves stops working until they connect. */
function moveNote(offer: Offer, repositoryCount: number): string {
  const connection = offer.connectionName ?? "that connection";
  const moves = `Moves ${repositoryCount === 1 ? "the repository" : "these repositories"} to ${connection}, another connection to the same host.`;

  return offer.strandedActAs.length === 0 ? moves : `${moves} Acting as ${listNames(offer.strandedActAs)} on ${repositoryCount === 1 ? "this repository" : "these repositories"} stops working until they connect ${connection}.`;
}

/** "Ada", "Ada and Bo", "Ada, Bo and Cy", "Ada, Bo, Cy and 2 others" — never a wall of names. */
function listNames(names: string[]): string {
  const shown = 3;

  if (names.length === 1) return names[0];
  if (names.length <= shown) return `${names.slice(0, -1).join(", ")} and ${names[names.length - 1]}`;

  const others = names.length - shown;

  return `${names.slice(0, shown).join(", ")} and ${others} other${others === 1 ? "" : "s"}`;
}

/** What the dialog did before the backend sent the list: the connection's own live credentials the caller may bind with, ordered by the suggestions. */
function offersFromCredentialList(credentials: CredentialSummary[], suggestions: RelinkSuggestion[], providerInstanceId: string, fromCredentialId: string | null, myId: string | undefined): Offer[] {
  const live = credentials.filter(c => c.providerInstanceId === providerInstanceId && c.id !== fromCredentialId && isLiveCredential(c));
  const rank = new Map(suggestions.map((s, index) => [s.credentialId, index]));
  const connectedBefore = new Set(suggestions.filter(s => s.connectedBeforeDisconnect).map(s => s.credentialId));

  return sortCredentialsByPreference(bindableCredentials(live, myId))
    .sort((a, b) => (rank.get(a.id) ?? Number.MAX_SAFE_INTEGER) - (rank.get(b.id) ?? Number.MAX_SAFE_INTEGER))
    .map(c => ({ id: c.id, displayName: c.displayName, ownerLabel: credentialOwnershipLabel(c), teamService: c.ownership === "TeamService", connectedBefore: connectedBefore.has(c.id), connectionName: null, changesConnection: false, strandedActAs: [] }));
}
