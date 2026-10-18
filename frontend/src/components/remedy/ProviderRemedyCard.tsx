import { useState } from "react";

import { Ic } from "@/_imported/ai-code-space/icons";
import type { ProviderRemedy, RemedyAction, RemedyActionKind } from "@/api/types";

import { useProviderFixes } from "./useProviderFixes";

interface ProviderRemedyCardProps {
  remedy: ProviderRemedy;
  /** Runs the failed turn again. Absent for a card with no run behind it (a refused delivery started none), which then offers no Re-run. */
  onRerun?: () => void | Promise<void>;
  rerunning?: boolean;
  /** The raw engine error, kept behind "Show raw error" exactly as the diagnostic card it stands in for keeps it. */
  rawDetail?: string | null;
  /** Called after a fix landed — the caller re-reads whatever it shows. */
  onFixed?: () => void;
}

/**
 * What is wrong with a provider connection, and the buttons that fix it — rendered from the backend's remedy, never from a code
 * this page maps to words of its own. The title and cause are the backend's; so is each action's label and whether the person
 * looking may press it. Only what the caller may do is a button. What is not theirs is said as a line instead — which fix, and what it
 * takes — with the people who can do it named, so a Member or a Viewer learns that this is fixable, and by whom, rather than meet a
 * card of dead buttons.
 *
 * Once what failed works again the card says so and puts Re-run first — the repair is done, the run only needs another go. Until then
 * there is no Re-run to press: nothing about the run changes until the connection does.
 */
export function ProviderRemedyCard({ remedy, onRerun, rerunning = false, rawDetail = null, onFixed }: ProviderRemedyCardProps) {
  const fix = useProviderFixes({ onFixed });
  const [showRaw, setShowRaw] = useState(false);
  const credential = remedy.credential ?? null;
  // What the caller may do is a button; what they may not is a line saying whose it is. A disabled button says "fixable" and then refuses.
  const notYours = remedy.actions.filter(a => !a.enabled && a.kind !== "rerun");
  const [primary, ...rest] = remedy.actions.filter(a => a.enabled && (a.kind !== "rerun" || onRerun));

  const press = (kind: RemedyActionKind) => {
    if (kind === "rerun") return void onRerun?.();

    if (kind === "open_providers") return fix.openProviders();
    if (kind === "edit_provider_url") return remedy.providerInstance ? fix.editProviderAddress(remedy.providerInstance.id, remedy.redirectsTo) : fix.openProviders();

    if (kind === "relink") {
      const instanceId = remedy.providerInstance?.id ?? credential?.providerInstanceId;
      const repositories = remedy.repositories.filter(r => !r.credentialWorks);

      return instanceId && repositories.length > 0 ? fix.relink(repositories.map(r => ({ id: r.id, fullPath: r.fullPath })), instanceId, credential?.id ?? null) : fix.openProviders();
    }

    if (!credential) return fix.openProviders();

    const target = { id: credential.id, displayName: credential.displayName, providerInstanceId: credential.providerInstanceId, authType: credential.authType };

    return kind === "reconnect" ? void fix.reconnect(target) : fix.replaceToken(target);
  };

  const buttonFor = (action: RemedyAction, isPrimary: boolean) => (
    <button
      key={action.kind}
      type="button"
      className={isPrimary && action.kind !== "open_providers" ? "room-btn-primary" : action.kind === "open_providers" ? "room-btn-ghost" : "room-btn"}
      disabled={(action.kind === "rerun" && rerunning) || (action.kind === "reconnect" && fix.reconnectingId === credential?.id)}
      onClick={() => press(action.kind)}
    >
      {actionIcon(action.kind)}
      {action.kind === "rerun" && rerunning ? "Re-running…" : action.kind === "reconnect" && fix.reconnectingId === credential?.id ? "Reconnecting…" : action.label}
    </button>
  );

  return (
    <div className={`room-err rmd${remedy.resolved ? " rmd-fixed" : " room-err-attn"}`} role={remedy.resolved ? "status" : "alert"} aria-label="Connection problem" data-code={remedy.code}>
      <span className="room-err-av">{remedy.resolved ? <Ic.Check size={16} /> : <Ic.Triangle size={16} />}</span>

      <div className="room-err-main">
        <div className="room-err-title">{remedy.title}</div>
        <p className="room-err-text">{remedy.cause}</p>

        {(credential || remedy.repositories.length > 0) && (
          <div className="rmd-subjects">
            {credential && (
              <span className="rmd-subject" title={credential.retired ? "Retired" : `${credential.health.replace("_", " ")}`}>
                <Ic.Key size={11} /> {credential.displayName}
                <span className="cn-cred-owner" data-team-service={credential.ownership === "TeamService"}>{credential.ownership === "TeamService" ? "Team service" : credential.ownerName ?? "Personal"}</span>
              </span>
            )}
            {remedy.repositories.map(r => (
              <span key={r.id} className="rmd-subject" data-works={r.credentialWorks}>
                <Ic.Repo size={11} /> {r.fullPath}
                {r.credentialWorks && <Ic.Check size={11} />}
              </span>
            ))}
          </div>
        )}

        <div className="room-err-actions">
          {primary && buttonFor(primary, true)}
          {rest.map(action => buttonFor(action, false))}
        </div>

        {notYours.length > 0 && (
          <div className="rmd-ask" role="note">
            <div>Not yours to do:</div>
            <ul className="rmd-ask-list">
              {notYours.map(a => <li key={a.kind}><strong>{a.label}</strong>{a.disabledReason ? ` — ${a.disabledReason}` : ""}</li>)}
            </ul>
            {remedy.askPeople.length > 0 && <div>Ask {joinPeople(remedy.askPeople.map(p => `${p.name} (${p.role})`))}.</div>}
          </div>
        )}

        {rawDetail && rawDetail !== remedy.cause && (
          <button type="button" className="room-err-raw-toggle" onClick={() => setShowRaw(s => !s)}>
            <Ic.ChevronRight size={12} /> {showRaw ? "Hide raw error" : "Show raw error"}
          </button>
        )}
        {showRaw && rawDetail && <pre className="room-err-raw">{rawDetail}</pre>}
      </div>

      {fix.dialogs}
    </div>
  );
}

function actionIcon(kind: RemedyActionKind) {
  switch (kind) {
    case "relink": return <Ic.Link size={13} />;
    case "reconnect":
    case "replace_token": return <Ic.Key size={13} />;
    case "edit_provider_url": return <Ic.Settings size={13} />;
    case "open_providers": return <Ic.Globe size={13} />;
    case "rerun": return <Ic.Play size={12} />;
  }
}

/** "Ada", "Ada or Bob", "Ada, Bob or Cy" — a short list a person can read as a sentence. */
function joinPeople(names: string[]): string {
  if (names.length <= 1) return names[0] ?? "";

  return `${names.slice(0, -1).join(", ")} or ${names[names.length - 1]}`;
}
