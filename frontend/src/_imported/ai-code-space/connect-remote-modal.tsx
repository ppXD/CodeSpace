import { useEffect, useMemo, useRef, useState } from "react";
import { createPortal } from "react-dom";

import { ApiError, oauthApi, type AddProviderInstanceRequest, type UpdateProviderInstanceRequest } from "@/api/oauth";
import type { CredentialSummary, ProviderInstanceSummary, ProviderKind } from "@/api/types";
import { useAlert, useConfirm } from "@/components/dialog";
import { useAddGroupAccessToken, useAddProviderInstance, useCredentialCapabilities, useCredentials, useDeleteProviderInstance, useProviderDefaults, useProviderInstances, useRevokeCredential, useUpdateProviderInstance } from "@/hooks/use-credentials";
import { useMe } from "@/hooks/use-me";
import { OAuthFlowError, useOAuthFlow } from "@/hooks/use-oauth-flow";
import { providerSupportsTeamServiceCredential } from "@/lib/teamCredentials";

import { Ic } from "./icons";

/**
 * Providers modal — single view of every OAuth-enabled provider with the caller's
 * connection status baked into each row. Internally the backend has two tables
 * (provider_instance for the team-level OAuth-app config, credential for the user's
 * personal token), but the user never has to think about the split — a row is either
 * "Connected" or "Not connected" for them.
 *
 * Steps:
 *   1. "list"          — providers with per-row Connect / Disconnect action
 *   2. "addProvider"   — form to register a new GitHub/GitLab integration
 *   3. "editProvider"  — rotate OAuth secret / rename an existing integration
 *   4. "addTeamToken"  — mint a durable team-owned credential (GitLab group token)
 */

interface ConnectRemoteModalProps {
  onClose: () => void;
}

type Step = "list" | "addProvider" | "editProvider" | "addTeamToken";

/** The two credential audiences the list view splits into: my personal sign-ins vs the team's
 *  shared service credentials. Toggled by the tab bar at the top of the providers list. */
type ProviderTab = "personal" | "team";

const PROVIDER_THEME: Record<ProviderKind, { initials: string; label: string }> = {
  GitHub: { initials: "GH", label: "GitHub" },
  GitLab: { initials: "GL", label: "GitLab" },
  Git: { initials: "G", label: "Git" },
};

// Hardcoded fallbacks only used while the backend defaults request is in flight, OR if it
// fails. Source of truth is GET /api/provider-instances/defaults/{provider} which reads
// IProviderModule.DefaultOAuthScopes — that way scope renames in the backend land here on
// next render with zero coordination. Keep this list minimal to flag staleness early.
const FALLBACK_DEFAULTS: Record<"GitHub" | "GitLab", { baseUrl: string; defaultDisplayName: string; scopes: string[] }> = {
  GitHub: { baseUrl: "https://github.com", defaultDisplayName: "GitHub", scopes: ["repo", "read:user"] },
  GitLab: { baseUrl: "https://gitlab.com", defaultDisplayName: "GitLab", scopes: ["api"] },
};

// Capability → user-facing label mapping. Capability names come from the backend by C# type
// name (`IRepositoryCatalogCapability`, etc.) — we localise them here so the UI reads
// "Read repos · Webhooks" instead of leaking interface names.
const CAPABILITY_LABELS: Record<string, string> = {
  IRepositoryCatalogCapability: "Read repos",
  IWebhookRegistrationCapability: "Webhooks",
  ICredentialProbeCapability: "Identity"
};

export function ConnectRemoteModal({ onClose }: ConnectRemoteModalProps) {
  const [step, setStep] = useState<Step>("list");
  const [editingInstance, setEditingInstance] = useState<ProviderInstanceSummary | null>(null);
  const [teamTokenInstance, setTeamTokenInstance] = useState<ProviderInstanceSummary | null>(null);
  const [providerTab, setProviderTab] = useState<ProviderTab>("personal");
  const [connectingId, setConnectingId] = useState<string | null>(null);
  const [errorByInstance, setErrorByInstance] = useState<Record<string, string>>({});

  const me = useMe();
  const credentials = useCredentials();
  const instances = useProviderInstances();
  const revoke = useRevokeCredential();
  const runOAuth = useOAuthFlow();
  const confirm = useConfirm();

  // Show ALL providers (no oauthEnabled filter). Rows distinguish OAuth-ready vs
  // needs-setup via per-row status — hiding non-OAuth providers leaves the user in a
  // dead-end state where they can't even see the broken instance to fix it.
  const allProviders = useMemo(() => instances.data ?? [], [instances.data]);

  // Per-instance, the current user's most-recent active OAuth credential. Drives the
  // Connect vs Disconnect rendering per row.
  const myCredentialByInstance = useMemo(() => {
    const map = new Map<string, CredentialSummary>();
    const myId = me.data?.id;
    if (!myId) return map;

    const candidates = (credentials.data ?? [])
      .filter(c => c.authType === "OAuth" && c.ownerUserId === myId && c.status === "Active")
      .sort((a, b) => b.createdDate.localeCompare(a.createdDate));

    for (const c of candidates) {
      if (!map.has(c.providerInstanceId)) map.set(c.providerInstanceId, c);
    }
    return map;
  }, [credentials.data, me.data?.id]);

  // Active team-service credentials (owned by the team, not a person). Shown in the modal's
  // "Team" tab, grouped under the provider each belongs to — a distinct identity from a personal sign-in.
  const teamServiceCreds = useMemo(
    () => (credentials.data ?? []).filter(c => c.ownership === "TeamService" && c.status === "Active"),
    [credentials.data],
  );

  // Close on Escape.
  useEffect(() => {
    const handler = (e: KeyboardEvent) => { if (e.key === "Escape") onClose(); };
    window.addEventListener("keydown", handler);
    return () => window.removeEventListener("keydown", handler);
  }, [onClose]);

  const connect = async (instance: ProviderInstanceSummary) => {
    setConnectingId(instance.id);
    setErrorByInstance(prev => ({ ...prev, [instance.id]: "" }));

    try {
      const displayName = `${me.data?.name ?? "user"}'s ${instance.displayName}`;
      await runOAuth({ providerInstanceId: instance.id, displayName });
      // Hook invalidates ['credentials']; row re-renders with "Connected" automatically.
    } catch (err) {
      const message =
        err instanceof OAuthFlowError ? mapFlowErrorMessage(err)
          : err instanceof ApiError ? err.message
            : err instanceof Error ? err.message
              : "Authorization failed.";
      setErrorByInstance(prev => ({ ...prev, [instance.id]: message }));
    } finally {
      setConnectingId(null);
    }
  };

  const disconnect = async (credential: CredentialSummary) => {
    // Preview impact BEFORE confirming — the user should know how many bound repos will
    // lose their auth source. Fetched on demand (not pre-cached) because it's only needed
    // at click time. Failure of the preview is non-fatal: we fall back to a generic
    // confirm message so the operator can still proceed.
    let usageMsg = "Your OAuth token will be revoked. You can reconnect at any time.";
    try {
      const usage = await oauthApi.getCredentialUsage(credential.id);
      if (usage.activeRepositoryCount > 0) {
        const n = usage.activeRepositoryCount;
        usageMsg = `Your OAuth token will be revoked. ${n} repositor${n === 1 ? "y" : "ies"} bound through this credential will be marked as needing a new credential (event webhooks keep working — you can re-link or unbind later).`;
      }
    } catch {
      // ignore — confirm with generic copy
    }

    const ok = await confirm({
      title: "Disconnect this provider?",
      message: usageMsg,
      confirmLabel: "Disconnect",
      destructive: true,
    });
    if (!ok) return;
    revoke.mutate(credential.id);
  };

  // Revoke a team-service credential. Same usage-aware confirm as the personal disconnect, but
  // team-worded — repos bound through it (if any) get marked needs-credential, webhooks keep working.
  const revokeTeamCredential = async (credential: CredentialSummary) => {
    let usageMsg = "This team service credential will be revoked. You can add a new one anytime.";
    try {
      const usage = await oauthApi.getCredentialUsage(credential.id);
      if (usage.activeRepositoryCount > 0) {
        const n = usage.activeRepositoryCount;
        usageMsg = `This team service credential will be revoked. ${n} repositor${n === 1 ? "y" : "ies"} bound through it will be marked as needing a new credential (event webhooks keep working — re-link or unbind later).`;
      }
    } catch {
      // ignore — confirm with generic copy
    }

    const ok = await confirm({ title: "Revoke this team credential?", message: usageMsg, confirmLabel: "Revoke", destructive: true });
    if (!ok) return;
    revoke.mutate(credential.id);
  };

  return createPortal(
    <>
      {/* Backdrop is non-interactive — clicking outside the modal must not close it
          (too easy to misfire when the user reaches for an input). Only the X icon,
          the Cancel button, or Escape closes the modal. */}
      <div className="mdl-mask" />
      <div className="mdl" role="dialog" aria-modal="true">
        {step === "list" && (
          <ProvidersStep
            providers={allProviders}
            loading={instances.isLoading || credentials.isLoading || me.isLoading}
            error={instances.error ?? credentials.error}
            myCredentialByInstance={myCredentialByInstance}
            teamServiceCreds={teamServiceCreds}
            tab={providerTab}
            onTabChange={setProviderTab}
            connectingId={connectingId}
            revokingId={revoke.isPending ? revoke.variables ?? null : null}
            errors={errorByInstance}
            onConnect={connect}
            onDisconnect={disconnect}
            onAddProvider={() => setStep("addProvider")}
            onEditProvider={(instance) => { setEditingInstance(instance); setStep("editProvider"); }}
            onAddTeamToken={(instance) => { setTeamTokenInstance(instance); setStep("addTeamToken"); }}
            onRevokeTeamCred={revokeTeamCredential}
            onClose={onClose}
          />
        )}

        {step === "addProvider" && (
          <AddProviderStep
            onBack={() => setStep("list")}
            onClose={onClose}
            onCreated={() => setStep("list")}
          />
        )}

        {step === "editProvider" && editingInstance && (
          <EditProviderStep
            instance={editingInstance}
            onBack={() => { setStep("list"); setEditingInstance(null); }}
            onClose={onClose}
            onSaved={() => { setStep("list"); setEditingInstance(null); }}
          />
        )}

        {step === "addTeamToken" && teamTokenInstance && (
          <AddTeamTokenStep
            instance={teamTokenInstance}
            onBack={() => { setStep("list"); setTeamTokenInstance(null); }}
            onClose={onClose}
            onCreated={() => { setStep("list"); setTeamTokenInstance(null); }}
          />
        )}
      </div>
    </>,
    document.body,
  );
}

// ── Providers list step ────────────────────────────────────────────────────────

interface ProvidersStepProps {
  providers: ProviderInstanceSummary[];
  loading: boolean;
  error: unknown;
  myCredentialByInstance: Map<string, CredentialSummary>;
  teamServiceCreds: CredentialSummary[];
  tab: ProviderTab;
  onTabChange: (tab: ProviderTab) => void;
  connectingId: string | null;
  revokingId: string | null;
  errors: Record<string, string>;
  onConnect: (instance: ProviderInstanceSummary) => void;
  onDisconnect: (credential: CredentialSummary) => void;
  onAddProvider: () => void;
  onEditProvider: (instance: ProviderInstanceSummary) => void;
  onAddTeamToken: (instance: ProviderInstanceSummary) => void;
  onRevokeTeamCred: (credential: CredentialSummary) => void;
  onClose: () => void;
}

function ProvidersStep({ providers, loading, error, myCredentialByInstance, teamServiceCreds, tab, onTabChange, connectingId, revokingId, errors, onConnect, onDisconnect, onAddProvider, onEditProvider, onAddTeamToken, onRevokeTeamCred, onClose }: ProvidersStepProps) {
  return (
    <>
      <div className="mdl-head">
        <div className="mdl-title-wrap">
          <div className="mdl-title">Providers</div>
          {/* Description intentionally minimal. The previous paragraph explained
              the team-vs-user split and the platform's read/comment/listen verbs;
              that detail is documentation territory and the list below (with its
              per-row Connect / Disconnect buttons) is self-explanatory once you
              see it. Keep this single line as a one-glance positioning hint. */}
          <div className="mdl-sub">Connect a Git host so the team can read repos and listen for events.</div>
        </div>
        <button className="mdl-x" onClick={onClose} title="Close"><Ic.X size={14} /></button>
      </div>

      <div className="mdl-body">
        {loading && <div className="cn-loading"><Ic.Clock size={14} /> Loading…</div>}

        {error instanceof Error && !loading && (
          <div className="cn-banner cn-banner-err">
            <div className="cn-banner-h">Couldn't load providers</div>
            <div className="cn-banner-p">{error.message}</div>
          </div>
        )}

        {!loading && !error && providers.length === 0 && (
          <div className="cn-empty">
            <div className="cn-empty-h">No providers yet</div>
            <div className="cn-empty-p">Add your first GitHub or GitLab integration. After that, anyone on the team signs in with their own account here — no shared tokens, no copy-paste of secrets.</div>
            <button className="btn btn-primary" onClick={onAddProvider}><Ic.Plus size={13} /> Add provider</button>
          </div>
        )}

        {!loading && !error && providers.length > 0 && (
          <>
            {/* Both tabs list the SAME providers — they differ only in the credential dimension
                (your personal sign-in vs the team's shared tokens). "Add provider" is tab-neutral
                (a new provider shows in both), so it sits on its own action row above the tabs. */}
            <div className="mdl-action-row">
              <button className="btn" onClick={onAddProvider}><Ic.Plus size={14} /> Add provider</button>
            </div>
            <div className="cn-tabs" role="tablist">
              <button className="cn-tab" role="tab" aria-selected={tab === "personal"} data-active={tab === "personal"} onClick={() => onTabChange("personal")}>Personal</button>
              <button className="cn-tab" role="tab" aria-selected={tab === "team"} data-active={tab === "team"} onClick={() => onTabChange("team")}>Team</button>
            </div>

            {tab === "personal" && (
              <div className="cn-list">
                {providers.map(instance => {
                  const myCred = myCredentialByInstance.get(instance.id);
                  const isConnecting = connectingId === instance.id;
                  const isDisconnecting = Boolean(myCred) && revokingId === myCred!.id;
                  return (
                    <ProviderRow
                      key={instance.id}
                      instance={instance}
                      theme={PROVIDER_THEME[instance.provider]}
                      myCred={myCred}
                      isConnecting={isConnecting}
                      isDisconnecting={isDisconnecting}
                      errorMsg={errors[instance.id]}
                      onConnect={onConnect}
                      onDisconnect={onDisconnect}
                      onEdit={onEditProvider}
                    />
                  );
                })}
              </div>
            )}

            {tab === "team" && (
              <>
                <div className="cn-section-p">Shared tokens owned by the team — repos bound through them survive anyone leaving. Any member can bind with these; no one shares a personal account.</div>
                <div className="cn-list">
                  {providers.map(instance => (
                    <TeamProviderGroup
                      key={instance.id}
                      instance={instance}
                      theme={PROVIDER_THEME[instance.provider]}
                      creds={teamServiceCreds.filter(c => c.providerInstanceId === instance.id)}
                      revokingId={revokingId}
                      onAdd={onAddTeamToken}
                      onRevoke={onRevokeTeamCred}
                    />
                  ))}
                </div>
              </>
            )}
          </>
        )}
      </div>

      <div className="mdl-foot">
        {/* Foot-info MUST stay short — the primary button on the right takes
            growing real estate ("Add provider", future "Connect all", etc.)
            and .mdl-foot-info uses ellipsis truncation, so prose gets cut. */}
        <div className="mdl-foot-info">{providers.length} provider{providers.length === 1 ? "" : "s"}</div>
        <button className="btn" onClick={onClose}>Done</button>
      </div>
    </>
  );
}

// ── Provider row + capability badges ───────────────────────────────────────────

interface ProviderRowProps {
  instance: ProviderInstanceSummary;
  theme: { initials: string; label: string };
  myCred: CredentialSummary | undefined;
  isConnecting: boolean;
  isDisconnecting: boolean;
  errorMsg: string | undefined;
  onConnect: (instance: ProviderInstanceSummary) => void;
  onDisconnect: (credential: CredentialSummary) => void;
  onEdit: (instance: ProviderInstanceSummary) => void;
}

function ProviderRow({ instance, theme, myCred, isConnecting, isDisconnecting, errorMsg, onConnect, onDisconnect, onEdit }: ProviderRowProps) {
  // Compact row: name + status on line 1, URL + purpose hint on line 2. Capability detail
  // and connection time live in tooltips and warning chips — out of the main flow unless
  // something needs attention. Keeps every row the same height regardless of state.
  return (
    <div className="cn-row">
      <div className="cn-mark" data-p={instance.provider.toLowerCase()}>{theme.initials}</div>
      <div className="cn-meta">
        <div className="cn-name">
          {instance.displayName}
          <span className="cn-name-prov">{theme.label}</span>
          {!instance.oauthEnabled && (
            <span className="cn-status cn-status-warn" title="Provider has no OAuth client ID / secret yet. Click Configure OAuth to set them up.">
              <Ic.Triangle size={10} /> needs OAuth setup
            </span>
          )}
          {myCred && (
            <span className="cn-status cn-status-active" title={`Connected ${formatRelative(myCred.createdDate)}`}>
              <span className="cn-status-dot" /> connected
            </span>
          )}
          {myCred && <CredentialCapabilityWarnings credentialId={myCred.id} />}
        </div>
        <div className="cn-sub">
          <span title={instance.baseUrl}>{instance.baseUrl}</span>
          <span className="cn-sub-purpose" title={purposeFor(instance.provider, Boolean(myCred), instance.oauthEnabled)}>
            · {purposeFor(instance.provider, Boolean(myCred), instance.oauthEnabled)}
          </span>
        </div>
        {errorMsg && <div className="cn-row-err">{errorMsg}</div>}
      </div>
      <div className="cn-cta">
        {/* Primary action depends on state:
            • OAuth not configured yet → "Configure OAuth" (opens edit form)
            • OAuth ready + I'm not connected → "Connect"
            • OAuth ready + I'm connected → "Disconnect" */}
        {!instance.oauthEnabled ? (
          <button className="btn btn-primary" onClick={() => onEdit(instance)}>
            <Ic.Key size={13} /> Configure OAuth
          </button>
        ) : myCred ? (
          <button className="btn btn-ghost" onClick={() => onDisconnect(myCred)} disabled={isDisconnecting}>
            {isDisconnecting ? <><Ic.Clock size={13} /> Disconnecting…</> : "Disconnect"}
          </button>
        ) : (
          <button className="btn btn-primary" onClick={() => onConnect(instance)} disabled={isConnecting}>
            {isConnecting ? <><Ic.Clock size={13} /> Connecting…</> : <><Ic.Link size={13} /> Connect</>}
          </button>
        )}
        <ProviderRowMenu instance={instance} hasCredential={Boolean(myCred)} onEdit={onEdit} />
      </div>
    </div>
  );
}

// ── Team service credentials (Team tab) ─────────────────────────────────────────

/**
 * One provider's team service credentials, grouped under it in the Team tab. GitLab providers
 * can mint a group token in place ("+ Add team token"); others show why they can't yet (GitHub
 * needs an App installation — not built). Each existing token carries its own Revoke.
 */
function TeamProviderGroup({ instance, theme, creds, revokingId, onAdd, onRevoke }: {
  instance: ProviderInstanceSummary;
  theme: { initials: string; label: string };
  creds: CredentialSummary[];
  revokingId: string | null;
  onAdd: (instance: ProviderInstanceSummary) => void;
  onRevoke: (credential: CredentialSummary) => void;
}) {
  const supportsTeamToken = providerSupportsTeamServiceCredential(instance.provider);

  return (
    <div className="cn-tg">
      <div className="cn-tg-head">
        <div className="cn-mark" data-p={instance.provider.toLowerCase()}>{theme.initials}</div>
        <div className="cn-tg-name">{instance.displayName}<span className="cn-name-prov">{theme.label}</span></div>
      </div>

      {/* Tokens + add/coming-soon, indented to line up under the provider name. A provider can
          hold many team tokens (different groups, rotation) — they stack here, each revocable. */}
      <div className="cn-tg-items">
        {creds.map(c => (
          <div key={c.id} className="cn-tg-cred">
            <Ic.Key size={12} />
            <span className="cn-tg-cred-name" title={c.displayName}>{c.displayName}</span>
            <button className="btn btn-ghost" onClick={() => onRevoke(c)} disabled={revokingId === c.id}>
              {revokingId === c.id ? "Revoking…" : "Revoke"}
            </button>
          </div>
        ))}

        {supportsTeamToken ? (
          <button className="btn btn-ghost cn-tg-add" onClick={() => onAdd(instance)}><Ic.Plus size={13} /> Add team token</button>
        ) : (
          <div className="cn-tg-note">Team tokens use a {theme.label} App — coming soon. For now, members connect personally on the Personal tab.</div>
        )}
      </div>
    </div>
  );
}

/**
 * Kebab menu — every row gets one with Edit + Remove. Remove is destructive (cascade
 * revokes credentials) so it's red-tinted and asks for confirm. The menu uses a click-
 * away listener to dismiss; same pattern as the sidebar user popover.
 */
function ProviderRowMenu({ instance, hasCredential, onEdit }: { instance: ProviderInstanceSummary; hasCredential: boolean; onEdit: (instance: ProviderInstanceSummary) => void }) {
  const [open, setOpen] = useState(false);
  const [pos, setPos] = useState<{ top: number; right: number } | null>(null);
  const triggerRef = useRef<HTMLButtonElement>(null);
  const del = useDeleteProviderInstance();
  const confirm = useConfirm();
  const alert = useAlert();

  // The popover lives in `.mdl-body` which has `overflow-y: auto` — when this row is the
  // last in the list, an absolute-positioned popover gets clipped by the scroll container
  // and disappears under the modal footer. Portal it to <body> with `position: fixed` so
  // it escapes the clipping ancestor. Compute coords at open time from the trigger's
  // bounding rect, and flip ABOVE the trigger when there isn't enough room below.
  useEffect(() => {
    if (!open || !triggerRef.current) return;

    const POP_HEIGHT = 90;   // approx — two items at ~42px each + 6px padding
    const POP_GAP = 4;

    const rect = triggerRef.current.getBoundingClientRect();
    const spaceBelow = window.innerHeight - rect.bottom;
    const flipUp = spaceBelow < POP_HEIGHT + POP_GAP + 16;

    setPos({
      top: flipUp ? rect.top - POP_HEIGHT - POP_GAP : rect.bottom + POP_GAP,
      right: window.innerWidth - rect.right,
    });
  }, [open]);

  // Close on outside click + on any scroll (popover anchor would drift otherwise — easier
  // to dismiss than to track multiple scroll containers).
  useEffect(() => {
    if (!open) return;
    const onClick = (e: MouseEvent) => {
      const t = e.target as HTMLElement;
      if (!t.closest(".cn-row-menu") && !t.closest(".cn-row-pop-portal")) setOpen(false);
    };
    const onScroll = () => setOpen(false);
    window.addEventListener("click", onClick);
    window.addEventListener("scroll", onScroll, true);
    return () => {
      window.removeEventListener("click", onClick);
      window.removeEventListener("scroll", onScroll, true);
    };
  }, [open]);

  const remove = async () => {
    setOpen(false);

    // Fetch impact preview first — how many repos and credentials are tied to this
    // provider. Drives the confirm copy and whether to offer the cascade. Failure here
    // is non-fatal: we proceed with conservative defaults so a transient blip doesn't
    // block the operator entirely.
    let repoCount = 0;
    let credCount = hasCredential ? 1 : 0;
    try {
      const usage = await oauthApi.getProviderInstanceUsage(instance.id);
      repoCount = usage.activeRepositoryCount;
      credCount = usage.activeCredentialCount;
    } catch {
      // ignore — conservative defaults already set
    }

    // Cascade path only activates when there ARE bound repos. The confirm copy spells
    // out exactly what's at stake (repo soft-delete + credential revoke) so the
    // "Unbind all and remove" button is never a surprise click.
    const cascade = repoCount > 0;
    const message = buildRemoveProviderMessage(repoCount, credCount);
    const confirmLabel = cascade ? "Unbind all and remove" : "Remove provider";

    const ok = await confirm({
      title: `Remove ${instance.displayName}?`,
      message,
      confirmLabel,
      destructive: true,
    });
    if (!ok) return;

    try {
      await del.mutateAsync({ id: instance.id, force: cascade });
    } catch (err) {
      await alert({
        title: "Couldn't remove provider",
        message: err instanceof Error ? err.message : "Unexpected error.",
        variant: "error",
      });
    }
  };

  return (
    <div className="cn-row-menu">
      <button ref={triggerRef} className="btn btn-ghost btn-icon" onClick={(e) => { e.stopPropagation(); setOpen(o => !o); }} title="More actions">
        <Ic.More size={14} />
      </button>
      {open && pos && createPortal(
        <div className="cn-row-pop cn-row-pop-portal" style={{ position: "fixed", top: pos.top, right: pos.right }}>
          <button className="sb-pop-item" onClick={() => { setOpen(false); onEdit(instance); }}>
            <Ic.Settings size={13} /> Edit settings
          </button>
          {/* GitHub's OAuth App authorize endpoint silently re-issues tokens once a user
              has authorized the app — they don't get the consent screen on reconnect, so
              they can't change which organizations are granted access. We send
              prompt=consent in the URL to nudge GitHub, but for stubborn cases (or to
              grant access to NEW orgs) the operator needs to manage the authorization
              directly on GitHub. This link goes straight to the per-app management page,
              same URL pattern on github.com AND GitHub Enterprise. */}
          {instance.provider === "GitHub" && instance.oauthEnabled && (
            <ManageOrgAccessOnGitHubItem instanceId={instance.id} onAfter={() => setOpen(false)} />
          )}
          <button className="sb-pop-item sb-pop-item-danger" onClick={remove} disabled={del.isPending}>
            <Ic.X size={13} /> {del.isPending ? "Removing…" : "Remove provider"}
          </button>
        </div>,
        document.body,
      )}
    </div>
  );
}

/**
 * Opens GitHub's per-app authorization page in a new tab. Needs the live OAuth client_id
 * from the backend (the list response doesn't include the client secret, only an
 * oauthEnabled flag), so we fetch the full provider-instance detail-style data on click.
 * Alternative would be to ship client_id in the list response — but it's not needed
 * anywhere else and shipping it adds a small surface area for credential-id sniffing.
 *
 * Implementation note: ProviderInstanceSummary already carries baseUrl, so the URL
 * template `{baseUrl}/settings/connections/applications/{client_id}` works for both
 * github.com and GitHub Enterprise without extra lookup — we just need client_id.
 * We pull it via the existing GET /api/provider-instances/defaults endpoint? No that's
 * for module defaults. We don't currently expose client_id on the wire — simplest is to
 * just open `{baseUrl}/settings/applications` (the user's full authorized-apps list) and
 * let them find ours. Less ideal but no new endpoint required.
 */
function ManageOrgAccessOnGitHubItem({ instanceId, onAfter }: { instanceId: string; onAfter: () => void }) {
  const instances = useProviderInstances();
  const instance = instances.data?.find(i => i.id === instanceId);
  if (!instance) return null;

  // GitHub's authorized apps page — works on github.com AND GitHub Enterprise. The user
  // lands here, finds the row for our OAuth app, and from there can grant or revoke org
  // access. We can't deep-link to the specific app without leaking client_id to the SPA,
  // so the one-extra-click of "find the right row" is the right tradeoff vs an extra
  // backend endpoint just for this hint.
  const manageUrl = `${instance.baseUrl.replace(/\/$/, "")}/settings/applications`;

  return (
    <a
      className="sb-pop-item"
      href={manageUrl}
      target="_blank"
      rel="noopener noreferrer"
      onClick={onAfter}
    >
      <Ic.ArrowOut size={13} /> Manage org access on GitHub
    </a>
  );
}

/**
 * Shows ONLY capabilities that are missing scopes. The common case (everything works) is
 * indicated by the "connected" status alone — green dot + that's it. Showing "✓ Read repos"
 * for every capability in the happy path was just noise that bloated row height; warnings
 * matter, ✓✓✓ doesn't.
 *
 * Inline in the title line as small amber chips so they sit next to "connected" and the
 * row doesn't grow a third line just for them. Tooltip names the missing scope(s).
 */
function CredentialCapabilityWarnings({ credentialId }: { credentialId: string }) {
  const caps = useCredentialCapabilities(credentialId);

  if (!caps.data) return null;
  const missing = caps.data.capabilities.filter(c => !c.isAvailable);
  if (missing.length === 0) return null;

  return (
    <>
      {missing.map(cap => {
        const label = CAPABILITY_LABELS[cap.capability] ?? cap.capability;
        return (
          <span
            key={cap.capability}
            className="cn-status cn-status-warn"
            title={`${label} unavailable — missing scope(s): ${cap.missingScopes.join(", ")}`}
          >
            <Ic.Triangle size={10} /> {label}
          </span>
        );
      })}
    </>
  );
}

// ── Add provider step ──────────────────────────────────────────────────────────

interface AddProviderStepProps {
  onBack: () => void;
  onClose: () => void;
  onCreated: () => void;
}

function AddProviderStep({ onBack, onClose, onCreated }: AddProviderStepProps) {
  const add = useAddProviderInstance();

  const [provider, setProvider] = useState<"GitHub" | "GitLab">("GitHub");
  const [displayName, setDisplayName] = useState<string>(FALLBACK_DEFAULTS.GitHub.defaultDisplayName);
  const [baseUrl, setBaseUrl] = useState<string>(FALLBACK_DEFAULTS.GitHub.baseUrl);
  const [displayNameTouched, setDisplayNameTouched] = useState(false);
  const [baseUrlTouched, setBaseUrlTouched] = useState(false);
  const [clientId, setClientId] = useState("");
  const [clientSecret, setClientSecret] = useState("");

  // Live defaults from backend IProviderModule — scope list, base URL, callback URL.
  // Falls back to FALLBACK_DEFAULTS only if the request fails (network blip, etc.) so the
  // form is always usable.
  const defaults = useProviderDefaults(provider);
  const effectiveScopes = defaults.data?.defaultOAuthScopes ?? FALLBACK_DEFAULTS[provider].scopes;
  const effectiveCallbackUrl = defaults.data?.oAuthCallbackUrl ?? `${window.location.origin}/api/credentials/oauth/callback`;

  const switchProvider = (next: "GitHub" | "GitLab") => {
    setProvider(next);
    if (!displayNameTouched) setDisplayName(FALLBACK_DEFAULTS[next].defaultDisplayName);
    if (!baseUrlTouched) setBaseUrl(FALLBACK_DEFAULTS[next].baseUrl);
  };

  const submit = async (e: React.FormEvent) => {
    e.preventDefault();
    if (!displayName.trim() || !baseUrl.trim() || !clientId.trim() || !clientSecret || add.isPending) return;

    const request: AddProviderInstanceRequest = {
      provider,
      displayName: displayName.trim(),
      baseUrl: baseUrl.trim().replace(/\/$/, ""),
      oauthClientId: clientId.trim(),
      oauthClientSecret: clientSecret,
      oauthDefaultScopes: effectiveScopes,
    };

    try {
      await add.mutateAsync(request);
      onCreated();
    } catch {
      // error surfaced via add.error below
    }
  };

  // OAuth client ID + secret are mandatory at Add time. The earlier "leave both blank →
  // PAT-only provider" path created confusing half-state rows visible in the modal but
  // not connectable; we removed it intentionally. Legacy rows without OAuth still exist
  // (recovered via the Configure OAuth / Edit flow), but new Add must be fully configured.
  const submitDisabled = !displayName.trim() || !baseUrl.trim() || !clientId.trim() || !clientSecret || add.isPending;
  const errorMessage =
    add.error instanceof ApiError ? add.error.message
      : add.error instanceof Error ? add.error.message
        : null;

  return (
    <>
      <div className="mdl-head">
        <button className="mdl-back" onClick={onBack} title="Back" disabled={add.isPending}><Ic.ChevronLeft size={16} /></button>
        <div className="mdl-title-wrap">
          <div className="mdl-title">Add provider</div>
          <div className="mdl-sub">Team-wide GitHub or GitLab integration. Shared across all members.</div>
        </div>
        <button className="mdl-x" onClick={onClose} title="Close" disabled={add.isPending}><Ic.X size={14} /></button>
      </div>

      <form className="mdl-body cn-form" onSubmit={submit}>
        <div className="cn-radio-row">
          {(["GitHub", "GitLab"] as const).map(p => {
            const theme = PROVIDER_THEME[p];
            const active = provider === p;
            return (
              <label key={p} className="cn-radio-card" data-active={active}>
                <input type="radio" name="provider" value={p} checked={active} onChange={() => switchProvider(p)} disabled={add.isPending} />
                <div className="cn-pv-mark" data-p={p.toLowerCase()}>{theme.initials}</div>
                <span className="cn-radio-label">{theme.label}</span>
              </label>
            );
          })}
        </div>

        <label className="cn-field">
          <span className="cn-field-l">Display name</span>
          <input
            className="cn-field-i"
            value={displayName}
            onChange={e => { setDisplayName(e.target.value); setDisplayNameTouched(true); }}
            placeholder={`e.g. ${FALLBACK_DEFAULTS[provider].defaultDisplayName}`}
            disabled={add.isPending}
          />
        </label>

        <label className="cn-field">
          <span className="cn-field-l">Base URL</span>
          <input
            className="cn-field-i"
            value={baseUrl}
            onChange={e => { setBaseUrl(e.target.value); setBaseUrlTouched(true); }}
            placeholder="https://github.com"
            disabled={add.isPending}
          />
          <span className="cn-field-h">Use your install URL for self-hosted GitLab / GitHub Enterprise.</span>
        </label>

        <div className="cn-divider" />

        <label className="cn-field">
          <span className="cn-field-l">OAuth client ID</span>
          <input
            className="cn-field-i"
            value={clientId}
            onChange={e => setClientId(e.target.value)}
            spellCheck={false}
            autoComplete="off"
            disabled={add.isPending}
            required
          />
        </label>

        <label className="cn-field">
          <span className="cn-field-l">OAuth client secret</span>
          <input
            type="password"
            className="cn-field-i"
            value={clientSecret}
            onChange={e => setClientSecret(e.target.value)}
            spellCheck={false}
            autoComplete="off"
            disabled={add.isPending}
            required
          />
          <span className="cn-field-h">Redirect URL: <code>{effectiveCallbackUrl}</code></span>
        </label>

        <div className="cn-scopes-note">
          <span className="cn-scopes-note-l">Will request scope{effectiveScopes.length === 1 ? "" : "s"}:</span>
          {effectiveScopes.map(s => <code key={s} className="cn-scope-chip">{s}</code>)}
        </div>

        {errorMessage && (
          <div className="cn-state cn-state-err">
            <span>{errorMessage}</span>
          </div>
        )}
      </form>

      <div className="mdl-foot">
        <div className="mdl-foot-info">Any team member can add a provider</div>
        <div style={{ display: "flex", gap: 8 }}>
          <button className="btn" onClick={onBack} disabled={add.isPending}>Cancel</button>
          <button className="btn btn-primary cn-submit" disabled={submitDisabled} onClick={submit}>
            {add.isPending ? <><Ic.Clock size={13} /> Saving…</> : <><Ic.Check size={13} /> Save</>}
          </button>
        </div>
      </div>
    </>
  );
}

// ── Edit provider step ─────────────────────────────────────────────────────────

interface EditProviderStepProps {
  instance: ProviderInstanceSummary;
  onBack: () => void;
  onClose: () => void;
  onSaved: () => void;
}

/**
 * PATCH-style edit form for an existing provider. Reuses the Add layout but:
 *   • Provider kind is fixed (cannot change post-creation)
 *   • The OAuth secret input shows a placeholder hint instead of pre-filling — we never
 *     ship the stored secret to the client. Leaving it blank preserves the existing value.
 *   • Used both for "fill in missing OAuth" on a non-OAuth-ready instance AND for rotating
 *     credentials or renaming a working one.
 */
function EditProviderStep({ instance, onBack, onClose, onSaved }: EditProviderStepProps) {
  const update = useUpdateProviderInstance();

  const [displayName, setDisplayName] = useState(instance.displayName);
  const [baseUrl, setBaseUrl] = useState(instance.baseUrl);
  const [clientId, setClientId] = useState(""); // intentionally blank — we don't fetch the existing value (kept simple; you can paste to overwrite)
  const [clientSecret, setClientSecret] = useState("");

  // Live defaults from backend module — only used to suggest the default scope set when
  // the operator is "filling in OAuth" on a non-configured provider. For an already-OAuth-
  // ready provider we leave scopes alone unless the user explicitly edits them.
  const defaults = useProviderDefaults(instance.provider);
  const effectiveCallbackUrl = defaults.data?.oAuthCallbackUrl ?? `${window.location.origin}/api/credentials/oauth/callback`;

  // Two distinct validity rules depending on the provider's existing state:
  //   • Configuring a non-OAuth row (oauthEnabled=false) → BOTH client_id and secret are
  //     required to save. We're filling in the missing pair, not patching one of them.
  //   • Editing an OAuth-ready row → both blank is fine (means "keep existing"). Filling
  //     in just one is ambiguous (changing client_id without rotating the secret usually
  //     breaks token exchange) so we require both-or-neither here too.
  const clientIdFilled = clientId.trim().length > 0;
  const secretFilled = clientSecret.length > 0;
  const oauthPairIncomplete = clientIdFilled !== secretFilled;
  const configuringFresh = !instance.oauthEnabled;
  const missingRequiredForConfig = configuringFresh && (!clientIdFilled || !secretFilled);
  const blanksOnImmutable = !displayName.trim() || !baseUrl.trim();
  const submitDisabled = update.isPending || blanksOnImmutable || oauthPairIncomplete || missingRequiredForConfig;

  const submit = async (e: React.FormEvent) => {
    e.preventDefault();
    if (submitDisabled) return;

    // Build a minimal PATCH body — only send fields the operator actually changed. Empty
    // OAuth secret stays out of the payload so the backend's "leave alone" path fires.
    const patch: UpdateProviderInstanceRequest = {};
    if (displayName.trim() !== instance.displayName) patch.displayName = displayName.trim();
    if (baseUrl.trim().replace(/\/$/, "") !== instance.baseUrl) patch.baseUrl = baseUrl.trim().replace(/\/$/, "");
    if (clientIdFilled) patch.oauthClientId = clientId.trim();
    if (secretFilled) patch.oauthClientSecret = clientSecret;
    // If we're fixing a non-OAuth provider (no existing client_id), also send default scopes.
    if (configuringFresh && clientIdFilled && defaults.data?.defaultOAuthScopes) {
      patch.oauthDefaultScopes = defaults.data.defaultOAuthScopes;
    }

    try {
      await update.mutateAsync({ id: instance.id, input: patch });
      onSaved();
    } catch {
      // surfaced via update.error below
    }
  };

  const errorMessage =
    update.error instanceof ApiError ? update.error.message
      : update.error instanceof Error ? update.error.message
        : null;

  const theme = PROVIDER_THEME[instance.provider];

  return (
    <>
      <div className="mdl-head">
        <button className="mdl-back" onClick={onBack} title="Back" disabled={update.isPending}><Ic.ChevronLeft size={16} /></button>
        <div className="mdl-title-wrap">
          <div className="mdl-title">Edit {theme.label} provider</div>
          <div className="mdl-sub">{instance.oauthEnabled ? "Update settings or rotate the OAuth secret." : "Fill in the OAuth client ID and secret to enable Connect for team members."}</div>
        </div>
        <button className="mdl-x" onClick={onClose} title="Close" disabled={update.isPending}><Ic.X size={14} /></button>
      </div>

      <form className="mdl-body cn-form" onSubmit={submit}>
        <label className="cn-field">
          <span className="cn-field-l">Display name</span>
          <input
            className="cn-field-i"
            value={displayName}
            onChange={e => setDisplayName(e.target.value)}
            disabled={update.isPending}
          />
        </label>

        <label className="cn-field">
          <span className="cn-field-l">Base URL</span>
          <input
            className="cn-field-i"
            value={baseUrl}
            onChange={e => setBaseUrl(e.target.value)}
            disabled={update.isPending}
          />
          <span className="cn-field-h">Use your install URL for self-hosted GitLab / GitHub Enterprise.</span>
        </label>

        <div className="cn-divider" />

        <label className="cn-field">
          <span className="cn-field-l">OAuth client ID {instance.oauthEnabled && <span className="cn-field-tag">stored</span>}</span>
          <input
            className="cn-field-i"
            value={clientId}
            onChange={e => setClientId(e.target.value)}
            placeholder={instance.oauthEnabled ? "leave blank to keep the existing value" : "paste from your OAuth app"}
            spellCheck={false}
            autoComplete="off"
            disabled={update.isPending}
          />
        </label>

        <label className="cn-field">
          <span className="cn-field-l">OAuth client secret {instance.oauthEnabled && <span className="cn-field-tag">stored</span>}</span>
          <input
            type="password"
            className="cn-field-i"
            value={clientSecret}
            onChange={e => setClientSecret(e.target.value)}
            placeholder={instance.oauthEnabled ? "leave blank to keep the existing value" : "paste from your OAuth app"}
            spellCheck={false}
            autoComplete="off"
            disabled={update.isPending}
          />
          <span className="cn-field-h">Redirect URL: <code>{effectiveCallbackUrl}</code></span>
        </label>

        {oauthPairIncomplete && (
          <div className="cn-state cn-state-err">
            <span>Provide both client ID and secret, or leave both blank to keep the existing values.</span>
          </div>
        )}

        {errorMessage && (
          <div className="cn-state cn-state-err">
            <span>{errorMessage}</span>
          </div>
        )}
      </form>

      <div className="mdl-foot">
        <div className="mdl-foot-info">{instance.provider} · {instance.baseUrl}</div>
        <div style={{ display: "flex", gap: 8 }}>
          <button className="btn" onClick={onBack} disabled={update.isPending}>Cancel</button>
          <button className="btn btn-primary cn-submit" disabled={submitDisabled} onClick={submit}>
            {update.isPending ? <><Ic.Clock size={13} /> Saving…</> : <><Ic.Check size={13} /> Save</>}
          </button>
        </div>
      </div>
    </>
  );
}

// ── Add team service token step ────────────────────────────────────────────────

interface AddTeamTokenStepProps {
  instance: ProviderInstanceSummary;
  onBack: () => void;
  onClose: () => void;
  onCreated: () => void;
}

/**
 * Mint a team-owned service credential — a GitLab Group Access Token that belongs to the group,
 * not a person, so repos bound through it survive anyone leaving. Reuses the exact .cn-form field
 * styling as Add/Edit provider so all three forms read identically. Scoped to the provider the
 * operator opened it from (the Team-tab row), so there's no instance picker. GitLab-only for now;
 * GitHub gets its App-installation flow later.
 */
function AddTeamTokenStep({ instance, onBack, onClose, onCreated }: AddTeamTokenStepProps) {
  const add = useAddGroupAccessToken();

  const [displayName, setDisplayName] = useState("");
  const [token, setToken] = useState("");

  const theme = PROVIDER_THEME[instance.provider];

  const submitDisabled = !displayName.trim() || !token.trim() || add.isPending;

  const submit = async (e: React.FormEvent) => {
    e.preventDefault();
    if (submitDisabled) return;

    try {
      await add.mutateAsync({ providerInstanceId: instance.id, displayName: displayName.trim(), token: token.trim() });
      onCreated();
    } catch {
      // surfaced via add.error below
    }
  };

  const errorMessage =
    add.error instanceof ApiError ? add.error.message
      : add.error instanceof Error ? add.error.message
        : null;

  return (
    <>
      <div className="mdl-head">
        <button className="mdl-back" onClick={onBack} title="Back" disabled={add.isPending}><Ic.ChevronLeft size={16} /></button>
        <div className="mdl-title-wrap">
          <div className="mdl-title">Add team service token</div>
          <div className="mdl-sub">A {theme.label} group token owned by the team — repos bound through it survive anyone leaving.</div>
        </div>
        <button className="mdl-x" onClick={onClose} title="Close" disabled={add.isPending}><Ic.X size={14} /></button>
      </div>

      <form className="mdl-body cn-form" onSubmit={submit}>
        <label className="cn-field">
          <span className="cn-field-l">Name</span>
          <input
            className="cn-field-i"
            autoFocus
            value={displayName}
            onChange={e => setDisplayName(e.target.value)}
            placeholder={`e.g. Acme team · ${theme.label}`}
            disabled={add.isPending}
          />
          <span className="cn-field-h">Shown in the credential picker when binding repositories.</span>
        </label>

        <label className="cn-field">
          <span className="cn-field-l">Group access token</span>
          <input
            type="password"
            className="cn-field-i"
            value={token}
            onChange={e => setToken(e.target.value)}
            placeholder="glpat-…"
            spellCheck={false}
            autoComplete="off"
            disabled={add.isPending}
          />
          <span className="cn-field-h">In {theme.label}: <strong>group → Settings → Access Tokens</strong> → role <code>Maintainer</code>, scopes <code>api</code> + <code>write_repository</code>.</span>
        </label>

        {errorMessage && (
          <div className="cn-state cn-state-err">
            <span>{errorMessage}</span>
          </div>
        )}
      </form>

      <div className="mdl-foot">
        <div className="mdl-foot-info">{theme.label} · {instance.baseUrl}</div>
        <div style={{ display: "flex", gap: 8 }}>
          <button className="btn" onClick={onBack} disabled={add.isPending}>Cancel</button>
          <button className="btn btn-primary cn-submit" disabled={submitDisabled} onClick={submit}>
            {add.isPending ? <><Ic.Clock size={13} /> Adding…</> : <><Ic.Check size={13} /> Add token</>}
          </button>
        </div>
      </div>
    </>
  );
}

// ── helpers ──────────────────────────────────────────────────────────────────

/**
 * Builds the confirm message for "Remove provider?" so the operator sees the exact
 * cascade up-front: how many repos get unbound, how many credentials get revoked.
 * Branch tree:
 *   • 0 repos + 0 creds  → "Just an empty provider, no side effects."
 *   • 0 repos + N creds  → mention the credential revoke
 *   • N repos (any creds) → mention BOTH and switch to the explicit cascade button
 */
function buildRemoveProviderMessage(repoCount: number, credCount: number): string {
  if (repoCount === 0 && credCount === 0) {
    return "The provider entry will be soft-deleted. This can't be undone.";
  }
  if (repoCount === 0) {
    const c = credCount === 1 ? "credential" : "credentials";
    return `${credCount} connected ${c} will be revoked. The provider entry will be soft-deleted. This can't be undone.`;
  }
  const r = repoCount === 1 ? "repository" : "repositories";
  const credPart = credCount > 0 ? ` ${credCount} connected ${credCount === 1 ? "credential" : "credentials"} will be revoked.` : "";
  return `${repoCount} ${r} bound to this provider will be unbound (remote webhooks deleted best-effort).${credPart} This can't be undone.`;
}

function formatRelative(iso: string) {
  const then = new Date(iso).getTime();
  const seconds = Math.max(0, Math.round((Date.now() - then) / 1000));

  if (seconds < 60) return "just now";
  if (seconds < 3600) return `${Math.floor(seconds / 60)} min ago`;
  if (seconds < 86400) return `${Math.floor(seconds / 3600)} hr ago`;
  return `${Math.floor(seconds / 86400)} d ago`;
}

/**
 * One-line "what this row does for me right now" hint shown next to the URL. Three states
 * surface different verbs so the operator can scan the list and immediately spot the row
 * that needs attention vs. the ones that are already useful.
 *
 * The intent is "一目了然" — a glance is enough. No clicking, no reading the full status
 * chip, no parsing capability tags.
 */
function purposeFor(provider: ProviderKind, isMineConnected: boolean, oauthEnabled: boolean): string {
  if (!oauthEnabled) return "Set up OAuth so the team can connect their accounts.";
  if (isMineConnected) return `Wires your ${provider} repos · webhooks · PR comments into CodeSpace.`;
  return `Sign in with your ${provider} account to bring your repos in.`;
}

function mapFlowErrorMessage(err: OAuthFlowError) {
  switch (err.code) {
    case "popup_blocked": return "Your browser blocked the OAuth popup. Allow popups for this site and try again.";
    case "cancelled":     return "You closed the authorization window. No connection was made.";
    case "init_failed":   return `Couldn't start the OAuth flow: ${err.message}`;
    case "provider_error": return err.providerError ? `Provider rejected: ${err.providerError}` : err.message;
    case "timeout":       return "The authorization took too long. Try again.";
    default:              return err.message;
  }
}
