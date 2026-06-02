import { useState } from "react";

import { useProviderInstances } from "@/hooks/use-credentials";
import { useMyProviderIdentities, useUnlinkIdentity } from "@/hooks/use-identities";

import { IdentityLinkModal } from "./IdentityLinkModal";

/**
 * "Connected identities" settings surface (Model B). Lists the team's provider instances and, per
 * instance, whether the caller has linked their OWN identity — with Connect (opens
 * {@link IdentityLinkModal}) / Disconnect. Linking proactively here means a later review the user
 * approves is attributed to them, not the shared connection credential.
 */
export function ConnectedIdentities() {
  const { data: instances = [], isLoading: instancesLoading } = useProviderInstances();
  const { data: identities = [], isLoading: identitiesLoading } = useMyProviderIdentities();
  const unlink = useUnlinkIdentity();
  const [connecting, setConnecting] = useState<{ id: string; label: string } | null>(null);

  const byInstance = new Map(identities.map((i) => [i.providerInstanceId, i]));

  return (
    <div className="acs-identities">
      <div className="acs-identities-head">
        <div className="acs-identities-title">Connected identities</div>
        <div className="acs-identities-sub">Link your own provider accounts so reviews and other actions you trigger are attributed to you — not a shared token.</div>
      </div>

      {instancesLoading || identitiesLoading ? (
        <div className="acs-identities-muted">Loading…</div>
      ) : instances.length === 0 ? (
        <div className="acs-identities-muted">No provider instances yet — connect one in repository settings first.</div>
      ) : (
        <ul className="acs-identity-list">
          {instances.map((inst) => {
            const identity = byInstance.get(inst.id);
            const label = `${inst.provider} · ${inst.displayName}`;
            return (
              <li key={inst.id} className="acs-identity-row">
                <div className="acs-identity-meta">
                  <span className="acs-identity-provider">{label}</span>
                  {identity ? (
                    <span className="acs-identity-user">
                      @{identity.providerUsername}
                      {identity.credentialStatus !== "Active" && <span className="acs-identity-stale"> · {identity.credentialStatus.toLowerCase()}</span>}
                    </span>
                  ) : (
                    <span className="acs-identity-none">Not connected</span>
                  )}
                </div>
                {identity ? (
                  <button className="btn btn-ghost" disabled={unlink.isPending} onClick={() => unlink.mutate(identity.id)}>Disconnect</button>
                ) : (
                  <button className="btn btn-primary" onClick={() => setConnecting({ id: inst.id, label })}>Connect</button>
                )}
              </li>
            );
          })}
        </ul>
      )}

      {connecting && (
        <IdentityLinkModal
          providerInstanceId={connecting.id}
          providerLabel={connecting.label}
          onClose={() => setConnecting(null)}
        />
      )}
    </div>
  );
}
