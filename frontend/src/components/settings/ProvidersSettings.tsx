import { useMemo, useState } from "react";

import { Ic } from "@/_imported/ai-code-space/icons";
import { ConnectRemoteModal } from "@/_imported/ai-code-space/connect-remote-modal";
import { ApiError } from "@/api/request";
import type { ProviderKind } from "@/api/types";
import { useCredentials, useProviderInstances } from "@/hooks/use-credentials";
import { useMe } from "@/hooks/use-me";

/**
 * Settings → Providers — a team-level overview of the Git hosts the team has connected, mirroring the
 * "Connect remote" entry on a project page. Management (add / edit / connect / disconnect / team tokens)
 * stays in the one shared `ConnectRemoteModal` so there's a single source of truth and the same calling
 * logic everywhere; this tab just surfaces it from Settings too and shows the providers at a glance.
 */
const PROVIDER_INITIALS: Record<ProviderKind, string> = { GitHub: "GH", GitLab: "GL", Git: "G" };

export function ProvidersSettings() {
  const [manageOpen, setManageOpen] = useState(false);
  const instances = useProviderInstances();
  const credentials = useCredentials();
  const me = useMe();

  const loading = instances.isLoading || credentials.isLoading || me.isLoading;
  const rows = instances.data ?? [];

  // Per-instance, whether the current user has an active personal credential on it — same "connected for me"
  // signal the modal's Personal tab shows, so the summary agrees with what they'll see when they open it.
  const connectedInstanceIds = useMemo(() => {
    const set = new Set<string>();
    const myId = me.data?.id;
    if (!myId) return set;
    for (const c of credentials.data ?? []) {
      if (c.ownerUserId === myId && c.status === "Active") set.add(c.providerInstanceId);
    }
    return set;
  }, [credentials.data, me.data?.id]);

  // Rendered inside the Settings layout (it owns the "Settings" header + tab strip), so this is body-only.
  return (
    <>
      <div style={{ display: "flex", justifyContent: "flex-end", padding: "4px 16px 0" }}>
        <button className="btn btn-primary" onClick={() => setManageOpen(true)}>
          <Ic.Link size={14} /> Manage providers
        </button>
      </div>

      <div className="cn-banner" style={{ margin: 16 }}>
        <div className="cn-banner-h">Connected Git hosts</div>
        <div className="cn-banner-p">
          Providers are team-wide GitHub / GitLab integrations. Each member then signs in with their own account —
          no shared tokens. The same management opens from a project page via <strong>Connect remote</strong>.
        </div>
      </div>

      {loading && <div className="ct-empty"><div className="ct-empty-h">Loading…</div></div>}

      {instances.error instanceof ApiError && (
        <div className="cn-banner cn-banner-err" style={{ margin: 16 }}>
          <div className="cn-banner-h">Couldn't load providers</div>
          <div className="cn-banner-p">{instances.error.message}</div>
        </div>
      )}

      {!loading && !instances.error && rows.length === 0 && (
        <div className="ct-empty">
          <div className="ct-empty-h">No providers yet</div>
          <div className="ct-empty-p">Add your first GitHub or GitLab integration so the team can read repos and listen for events.</div>
          <button className="btn btn-primary" style={{ marginTop: 12 }} onClick={() => setManageOpen(true)}>
            <Ic.Plus size={13} /> Add provider
          </button>
        </div>
      )}

      {!loading && !instances.error && rows.length > 0 && (
        <div className="cn-list" style={{ margin: 16 }}>
          {rows.map((inst) => {
            const connected = connectedInstanceIds.has(inst.id);
            return (
              <div className="cn-row" key={inst.id}>
                <div className="cn-row-head">
                  <div className="cn-mark" data-p={inst.provider.toLowerCase()}>{PROVIDER_INITIALS[inst.provider]}</div>
                  <div className="cn-meta">
                    <div className="cn-name">
                      {inst.displayName}
                      <span className="cn-name-prov">{inst.provider}</span>
                      {!inst.oauthEnabled && (
                        <span className="cn-status cn-status-warn" title="No OAuth app configured — members connect with a personal token, or set up OAuth in Manage providers.">
                          <Ic.Triangle size={10} /> no OAuth app
                        </span>
                      )}
                      {connected
                        ? <span className="cn-status cn-status-active"><span className="cn-status-dot" /> connected</span>
                        : <span className="cn-status">not connected</span>}
                    </div>
                    <div className="cn-sub"><span title={inst.baseUrl}>{inst.baseUrl}</span></div>
                  </div>
                </div>
              </div>
            );
          })}
        </div>
      )}

      {manageOpen && <ConnectRemoteModal onClose={() => setManageOpen(false)} />}
    </>
  );
}
