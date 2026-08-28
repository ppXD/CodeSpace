import { useState } from "react";

import { Ic } from "@/_imported/ai-code-space/icons";
import { ApiError } from "@/api/request";
import type { StorageAdoptionResult, StorageAdoptionStatus } from "@/api/storageAdoptions";
import { useAdoptStorageDefault, useStorageAdoptions } from "@/hooks/use-storage-adoptions";

/**
 * The deployment's own answer for where a class of data goes, offered to this team.
 *
 * <p>It sits ABOVE the guided flow, and on the same recessed deployment ground as the installed-provider
 * catalog, because adopting means the three steps below never have to be done: the deployment already
 * chose a provider, wrote the credential and named the namespace. A team that adopts skips them; a team
 * that declines still has them.</p>
 *
 * <p>The card renders nothing at all when the deployment has authored no default anyone here could take.
 * An empty scope panel would imply an absence the operator is expected to fill from this screen, and
 * nobody can: templates are authored in the admin surface, by a different capability.</p>
 */
export function StorageDefaultAdoption({ mayManage }: { mayManage: boolean }) {
  const adoptions = useStorageAdoptions();
  const rows = (adoptions.data ?? []).filter((status) => status.defaultAvailable || status.adopted);

  if (adoptions.isPending || rows.length === 0) return null;

  return (
    <section className="stg-scope" aria-labelledby="storage-defaults-title" data-scope="deployment">
      <div className="stg-scope-head">
        <span className="stg-scope-lock" aria-hidden="true"><Ic.Lock size={12} /></span>
        <h3 className="stg-title" id="storage-defaults-title">Deployment defaults</h3>
      </div>
      <div className="stg-scope-note">
        Destinations this deployment has prepared for every team. Taking one gives this team its own namespace
        inside it, and the three steps below are then already done for that class of data.
      </div>

      <div className="cn-list" role="list" aria-label="Deployment storage defaults">
        {rows.map((status) => <AdoptionRow key={status.dataClassTypeKey} status={status} mayManage={mayManage} />)}
      </div>
    </section>
  );
}

function AdoptionRow({ status, mayManage }: { status: StorageAdoptionStatus; mayManage: boolean }) {
  const adopt = useAdoptStorageDefault();
  const [confirming, setConfirming] = useState(false);
  const result = adopt.data;

  return (
    <div className="cn-row" role="listitem">
      <div className="cn-row-head">
        <div className="cn-mark"><Ic.Storage size={13} /></div>
        <div className="cn-meta">
          <div className="cn-name">
            {status.displayName}
            {status.adopted && <span className="cn-status">On the deployment default</span>}
            {status.teamOwnsRoute && <span className="cn-status">This team chose its own</span>}
          </div>
          <div className="cn-sub">{describe(status)}</div>
        </div>
        {status.canAdopt && mayManage && !confirming && (
          <button type="button" className="btn" disabled={adopt.isPending} onClick={() => setConfirming(true)}>
            {adopt.isPending ? "Taking…" : "Use this"}
          </button>
        )}
      </div>

      {confirming && (
        <div className="stg-hint" role="group" aria-label={`Confirm ${status.displayName}`}>
          {status.adoptionIsIrreversible
            ? "This class keeps a durable home of its own today. Taking the deployment default moves it for good — a data route that has been activated can never be turned off or pointed back at local storage."
            : "This class has nowhere else to store its data, so taking the deployment default is what starts it working. The route can be repointed later, but never switched off."}
          <div style={{ display: "flex", gap: 8, marginTop: 8 }}>
            <button type="button" className="btn btn-primary" disabled={adopt.isPending} onClick={() => { setConfirming(false); adopt.mutate(status.dataClassTypeKey); }}>
              Use this default
            </button>
            <button type="button" className="btn" onClick={() => setConfirming(false)}>Cancel</button>
          </div>
        </div>
      )}

      {result && <div className="stg-hint" role="status">{outcomeMessage(result)}</div>}
      {adopt.error != null && <div className="stg-hint" role="alert">{errorMessage(adopt.error)}</div>}
    </div>
  );
}

/** The line under the name: what the deployment offers, and what this team has done about it. */
function describe(status: StorageAdoptionStatus): string {
  if (status.adopted && status.sourceRevision != null && status.templateRevision != null && status.templateRevision > status.sourceRevision) {
    // Worth saying, and worth saying carefully: what this team already wrote is unaffected. A read
    // resolves through the profile revision recorded when the bytes were stored, never today's policy.
    return "This team is on an earlier version of the deployment default. Everything already stored stays where it was written.";
  }
  if (status.adopted) return "This team is on the deployment default for this data.";
  if (status.teamOwnsRoute) return "This team already points this data somewhere it chose, and a deployment default never replaces that.";
  return "Available to this team.";
}

/**
 * Every outcome gets a sentence. The server answers 200 for all of them precisely so this screen can
 * distinguish them, so collapsing any into a generic failure here would throw away what it went to
 * the trouble of telling us.
 */
function outcomeMessage(result: StorageAdoptionResult): string {
  switch (result.outcome) {
    case "Adopted": return "Done. New data for this class now goes to the deployment default.";
    case "AlreadyAdopted": return "This team was already on it, so nothing changed.";
    case "TeamOwnsRoute": return "This team already points this data somewhere it chose, so the default was left alone.";
    case "NoTemplate": return "The deployment no longer offers a default for this data.";
    case "TemplateDisabled": return "The deployment has switched this default off.";
    case "DestinationUnusable": return `The destination would not accept a write, so nothing was changed: ${result.detail ?? "no reason given"}`;
    case "RaceLost": return "Someone else was setting this up at the same moment. Reload to see where it landed.";
  }
}

function errorMessage(error: unknown): string {
  if (error instanceof ApiError) return error.message;
  return error instanceof Error ? error.message : "Something went wrong.";
}
