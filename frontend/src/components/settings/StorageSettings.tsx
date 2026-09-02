import { useEffect, useRef, useState, type ReactNode } from "react";
import { createPortal } from "react-dom";

import { Ic } from "@/_imported/ai-code-space/icons";
import { ApiError } from "@/api/request";
import type { StorageCredentialMetadata, StorageProfileDetail, StorageProfileProbeResult, StorageProfileState, StorageProfileSummary, StorageProviderModuleSummary } from "@/api/storage";
import { useAppendStorageProfileRevision, useCreateStorageProfile, usePlacementIntegrity, useProbeStorageProfile, useSetStorageProfileState, useStorageCredentials, useStorageProfile, useStorageProfiles, useStorageProviderModules } from "@/hooks/use-storage";
import { useStorageRoutes } from "@/hooks/use-storage-routes";
import { TeamPermissions, useTeamPermissions } from "@/hooks/use-team-management";
import { SchemaForm } from "@/components/workflows/SchemaForm";
import { StorageCredentialSettings } from "./StorageCredentialSettings";
import { StorageDefaultAdoption } from "./StorageDefaultAdoption";
import { StorageRouteSettings } from "./StorageRouteSettings";
import { PlacementIntegrityNotice } from "./PlacementIntegrityNotice";
import { AddDestinationDialog } from "./AddDestinationDialog";
import { probeFailureGuidance } from "./storageProbeGuidance";
import { StoragePlacementDrain } from "./StoragePlacementDrain";
import { StorageHealthBadge } from "./StorageHealthBadge";
import { StorageStep, type StorageStepState } from "./StorageStep";

/**
 * Settings → Storage. One ordered flow over a dependency chain the server enforces: a credential
 * carries a provider's secret, a profile names where bytes live, and a data route points one class
 * of data at an Active profile. The step still to do is the only one with an accent.
 *
 * <p>The installed-provider catalog is a different scope tier — set by the deployment, never edited
 * here — so it sits below the flow on its own recessed ground rather than as a fourth peer.</p>
 */
export function StorageSettings() {
  const providers = useStorageProviderModules();
  const credentials = useStorageCredentials();
  const profiles = useStorageProfiles();
  const routes = useStorageRoutes();
  const integrity = usePlacementIntegrity();
  const mayManage = useTeamPermissions().can(TeamPermissions.StorageManage);
  const [createOpen, setCreateOpen] = useState(false);
  const [addDestinationOpen, setAddDestinationOpen] = useState(false);
  const [managedProfileId, setManagedProfileId] = useState<string | null>(null);
  const providerRows = providers.data ?? [];
  const profileRows = profiles.data ?? [];
  const credentialRows = credentials.data ?? [];
  const providerError = errorMessage(providers.error);
  const profileError = errorMessage(profiles.error);

  // A Storage Credential can only ever hold a provider's secret inputs. With none declared anywhere in
  // this deployment's catalog there is nothing for the step to collect, so it is absent rather than empty.
  const credentialStepApplies = providerRows.some(providerHasSecretInputs);
  const activeCredentials = credentialRows.filter((credential) => credential.state === "Active");
  const activeProfiles = profileRows.filter((profile) => profile.state === "Active");
  const activeRoutes = (routes.data ?? []).filter((route) => route.state === "Active");
  const routeLocked = !profiles.isLoading && profileError == null && activeProfiles.length === 0;

  const done = {
    credential: activeCredentials.length > 0,
    profile: profileError == null && activeProfiles.length > 0,
    route: activeRoutes.length > 0,
  };
  // The active step is the first one still to do that nothing is stopping. Everything after it stays
  // reachable — being later in the order is not a refusal.
  const next = ([
    credentialStepApplies && !done.credential ? "credential" : null,
    !done.profile ? "profile" : null,
    !done.route && !routeLocked ? "route" : null,
  ] as const).find((step): step is "credential" | "profile" | "route" => step != null) ?? null;

  const stepState = (step: "credential" | "profile" | "route"): StorageStepState => {
    if (done[step]) return "done";
    if (step === "route" && routeLocked) return "locked";
    return step === next ? "active" : "upcoming";
  };

  const profileState = stepState("profile");
  const activatable = profileRows.find((profile) => profile.state !== "Retired");

  return (
    <div aria-labelledby="storage-settings-title">
      {/* The one primary action on this page. It answers the whole question - key, address, and what lands there -
          in one dialog that tests the destination BEFORE recording anything, so a mistyped secret costs a retry
          rather than a credential and a profile neither of which can be deleted. The step-by-step flow below stays
          for the things it is still the only way to reach: repairing a destination in place, stopping one, and
          reading its history. */}
      {mayManage && (
        <div style={{ display: "flex", justifyContent: "flex-end", padding: "16px 16px 0" }}>
          <button type="button" className="btn btn-primary" onClick={() => setAddDestinationOpen(true)}>
            <Ic.Plus size={14} /> Add a destination
          </button>
        </div>
      )}
      {addDestinationOpen && (
        <AddDestinationDialog
          providers={providerRows}
          onClose={() => setAddDestinationOpen(false)}
          onCreated={() => setAddDestinationOpen(false)}
        />
      )}

      <div className="cn-banner" style={{ margin: 16 }}>
        <h2 className="cn-banner-h" id="storage-settings-title">Artifact storage</h2>
        <div className="cn-banner-p">
          Once a data route is Active, the next write for that data class lands on the profile it names. Until then
          each class keeps the home it already has, which differs by class.
        </div>
        {/* Deliberately silent about reads: every storage query declares the same permission server-side, so
            promising a read-only view would be a claim this screen cannot keep. */}
        {!mayManage && <div className="cn-banner-p">Changing anything here needs the storage.manage permission, which you do not hold in this team.</div>}
        {/* The rest of this page describes where the NEXT write goes. This is the only line about what happened to
            the writes already made, which no amount of probing a healthy destination would reveal. */}
        <PlacementIntegrityNotice integrity={integrity.data} />
      </div>

      {/* Above the flow: adopting means the three steps below are already done for that class. */}
      <StorageDefaultAdoption mayManage={mayManage} />

      <div className="stg-flow" style={{ margin: 16 }}>
        <StorageCredentialSettings providers={providerRows} state={stepState("credential")} />

        <StorageStep
          step="profile"
          title="Storage profiles"
          titleId="storage-profiles-title"
          state={profileState}
          line={profileLine(profileRows, activeProfiles, profiles.isLoading, profileError)}
          action={mayManage ? (
            <>
              {/* With a profile already drafted, the accented act is activating it — a route may only
                  target an Active profile. Creating another stays available beside it. */}
              {activeProfiles.length === 0 && activatable && (
                <button type="button" className="btn" onClick={() => setManagedProfileId(activatable.id)}>Activate {activatable.stableName}</button>
              )}
              <button type="button" className="btn" disabled={providers.isLoading || providerError != null || providerRows.length === 0} onClick={() => setCreateOpen(true)}>Create storage profile</button>
            </>
          ) : undefined}
        >
          {profiles.isLoading && <LoadingMessage>Loading storage profiles…</LoadingMessage>}

          {profileError && <ErrorBanner title="Couldn't load storage profiles" message={profileError} />}

          {!profiles.isLoading && !profileError && profileRows.length === 0 && (
            <div className="stg-hint">A profile names a provider and its non-secret configuration. It carries no data of its own until a route points at it.</div>
          )}

          {!profiles.isLoading && !profileError && profileRows.length > 0 && (
            <>
              <div className="cn-list" role="list" aria-label="Storage profiles">
                {profileRows.map((profile) => (
                  <StorageProfileRow key={profile.id} profile={profile} provider={providerRows.find((provider) => provider.typeKey === profile.providerTypeKey)} onManage={mayManage ? () => setManagedProfileId(profile.id) : undefined} />
                ))}
              </div>
              {profiles.hasNextPage && (
                <button type="button" className="btn" disabled={profiles.isFetchingNextPage} onClick={() => profiles.fetchNextPage()}>
                  {profiles.isFetchingNextPage ? "Loading more profiles…" : "Load more profiles"}
                </button>
              )}
            </>
          )}
        </StorageStep>

        <StorageRouteSettings profiles={profileRows} state={stepState("route")} />
      </div>

      <section className="stg-scope" aria-labelledby="storage-providers-title" data-scope="deployment">
        <div className="stg-scope-head">
          <span className="stg-scope-lock" aria-hidden="true"><Ic.Lock size={12} /></span>
          <h3 className="stg-title" id="storage-providers-title">Installed providers</h3>
        </div>
        <div className="stg-scope-note">
          Set by this deployment. Installing or removing a provider module is a deployment change, not a team setting,
          and it never moves data that is already stored.
        </div>

        {providers.isLoading && <LoadingMessage>Loading storage providers…</LoadingMessage>}

        {providerError && <ErrorBanner title="Couldn't load storage providers" message={providerError} />}

        {!providers.isLoading && !providerError && providerRows.length === 0 && (
          <div className="stg-hint">No provider module is installed, so no storage profile can be created in this deployment.</div>
        )}

        {!providers.isLoading && !providerError && providerRows.length > 0 && (
          <div className="cn-list" role="list" aria-label="Installed storage providers">
            {providerRows.map((provider) => <StorageProviderRow key={provider.typeKey} provider={provider} />)}
          </div>
        )}
      </section>

      {createOpen && <CreateStorageProfileDialog providers={providerRows} credentials={credentialRows} onClose={() => setCreateOpen(false)} />}
      {managedProfileId && <ManageStorageProfileDialog profileId={managedProfileId} providers={providerRows} credentials={credentialRows} onClose={() => setManagedProfileId(null)} />}
    </div>
  );
}

/** The one line the profile step shows: what state the team's profiles are in, not what to do about it. */
function profileLine(all: StorageProfileSummary[], active: StorageProfileSummary[], loading: boolean, error: string | null): string {
  if (error) return "The storage profiles could not be read.";
  if (loading) return "Loading storage profiles…";
  if (all.length === 0) return "No storage profiles configured";
  if (active.length === 0) return `${countLabel(all.length, "profile")}, none Active — a data route can only target an Active profile`;
  if (all.length === 1) return `${active[0].stableName} · Active · revision ${active[0].currentRevision}`;
  return `${countLabel(all.length, "profile")}, ${active.length} Active`;
}

function countLabel(count: number, noun: string): string {
  return `${count} ${noun}${count === 1 ? "" : "s"}`;
}

function StorageProfileRow({ profile, provider, onManage }: { profile: StorageProfileSummary; provider?: StorageProviderModuleSummary; onManage?: () => void }) {
  return (
    <div className="cn-row" role="listitem">
      <div className="cn-row-head">
        <div className="cn-mark">{providerInitials(provider?.displayName ?? profile.stableName)}</div>
        <div className="cn-meta">
          <div className="cn-name">
            {profile.stableName}
            <span className={profile.state === "Active" ? "cn-status cn-status-active" : profile.state === "Retired" ? "cn-status cn-status-revoked" : "cn-status"}>{profile.state}</span>
            <span className="cn-status">Revision {profile.currentRevision}</span>
            {/* Whether this destination is taking bytes, as of the last time anything asked. Rendered next to the
                lifecycle state because Active says an operator turned it on, not that it works. */}
            <StorageHealthBadge health={profile.health} currentRevision={profile.currentRevision} />
          </div>
          <div className="cn-sub">
            <span>{provider?.displayName ?? profile.providerTypeKey}</span>
            {provider && <span>{profile.providerTypeKey}</span>}
          </div>
        </div>
        {onManage && <button type="button" className="btn" aria-label={`Manage ${profile.stableName}`} onClick={onManage}>Manage</button>}
      </div>
    </div>
  );
}

function StorageProviderRow({ provider }: { provider: StorageProviderModuleSummary }) {
  const requiresCredential = providerRequiresCredential(provider);
  const hasSecretInputs = providerHasSecretInputs(provider);
  const credentialStatus = requiresCredential ? "Storage Credential required" : hasSecretInputs ? "Optional Storage Credential" : "No secret inputs";

  return (
    <div className="cn-row" role="listitem">
      <div className="cn-row-head">
        <div className="cn-mark">{providerInitials(provider.displayName)}</div>
        <div className="cn-meta" style={{ flex: 1 }}>
          <div className="cn-name">
            {provider.displayName}
            <span className="cn-status">{provider.typeKey}</span>
            <span className="cn-status cn-status-active"><span className="cn-status-dot" /> Profile schema ready</span>
            <span className={requiresCredential ? "cn-status cn-status-warn" : "cn-status"}>{credentialStatus}</span>
          </div>
          <div className="cn-sub" aria-label={`${provider.displayName} capabilities`}>
            {provider.capabilities.length === 0 ? "No optional capabilities declared" : provider.capabilities.map(capabilityLabel).join(" · ")}
          </div>
        </div>
      </div>
    </div>
  );
}

function CreateStorageProfileDialog({ providers, credentials, onClose }: { providers: StorageProviderModuleSummary[]; credentials: StorageCredentialMetadata[]; onClose: () => void }) {
  const [providerTypeKey, setProviderTypeKey] = useState(providers[0]?.typeKey ?? "");
  const [stableName, setStableName] = useState("");
  const selectedProvider = providers.find((provider) => provider.typeKey === providerTypeKey);
  const [config, setConfig] = useState<Record<string, unknown>>(() => defaultsFromSchema(selectedProvider?.configSchema));
  const [formError, setFormError] = useState<string | null>(null);
  const [credentialRef, setCredentialRef] = useState("");
  const create = useCreateStorageProfile();
  const normalizedName = stableName.trim().toLowerCase();
  const stableNameValid = /^[a-z0-9][a-z0-9-]{0,127}$/.test(normalizedName);
  const configValid = selectedProvider != null && requiredValuesPresent(selectedProvider.configSchema, config);
  const requiresCredential = selectedProvider != null && providerRequiresCredential(selectedProvider);

  const chooseProvider = (typeKey: string) => {
    const provider = providers.find((candidate) => candidate.typeKey === typeKey);
    setProviderTypeKey(typeKey);
    setConfig(defaultsFromSchema(provider?.configSchema));
    setCredentialRef("");
    setFormError(null);
  };

  const submit = () => {
    if (!selectedProvider || !stableNameValid || !configValid || create.isPending) return;
    setFormError(null);
    create.mutate({ stableName: normalizedName, providerTypeKey, nonSecretConfig: cleanConfig(config), ...(credentialRef ? { credentialRef } : {}) }, {
      onSuccess: onClose,
      onError: (error) => setFormError(errorMessage(error) ?? "Couldn't create the storage profile."),
    });
  };

  return (
    <ModalFrame label="Create storage profile" title="Create storage profile" subtitle="Creates revision 1 in Draft state. Only non-secret provider configuration is collected here." onClose={onClose}>
      <div className="mdl-body">
        <div className="wf-form">
          <div className="wf-form-row">
            <label className="wf-form-label" htmlFor="storage-profile-stable-name">Stable name</label>
            <input id="storage-profile-stable-name" className="wf-form-input" value={stableName} onChange={(event) => setStableName(event.target.value)} placeholder="primary-artifacts" autoFocus />
            <span className="wf-form-help">Lowercase letters, digits, and hyphens. This identity cannot be renamed.</span>
          </div>

          <div className="wf-form-row">
            <label className="wf-form-label" htmlFor="storage-profile-provider">Provider</label>
            <select id="storage-profile-provider" className="wf-form-input" value={providerTypeKey} onChange={(event) => chooseProvider(event.target.value)}>
              {providers.map((provider) => <option key={provider.typeKey} value={provider.typeKey}>{provider.displayName}</option>)}
            </select>
          </div>

          {selectedProvider && (
            <div role="group" aria-label="Non-secret configuration">
              <SchemaForm schema={selectedProvider.configSchema} value={config} onChange={setConfig} />
            </div>
          )}

          {requiresCredential && (
            <CredentialNotice>
              This provider requires a Storage Credential before activation. You may link an active credential now or keep the new profile in Draft.
            </CredentialNotice>
          )}

          {selectedProvider && providerHasSecretInputs(selectedProvider) && <CredentialSelector providerTypeKey={providerTypeKey} credentials={credentials} value={credentialRef} onChange={setCredentialRef} />}

          {formError && <div className="cn-banner cn-banner-err" role="alert"><div className="cn-banner-p">{formError}</div></div>}
        </div>
      </div>
      <div className="mdl-foot">
        <button type="button" className="btn btn-ghost" onClick={onClose}>Cancel</button>
        <button type="button" className="btn btn-primary" disabled={!stableNameValid || !configValid || create.isPending} onClick={submit}>{create.isPending ? "Creating…" : "Create Draft"}</button>
      </div>
    </ModalFrame>
  );
}

function ManageStorageProfileDialog({ profileId, providers, credentials, onClose }: { profileId: string; providers: StorageProviderModuleSummary[]; credentials: StorageCredentialMetadata[]; onClose: () => void }) {
  const profile = useStorageProfile(profileId);
  const [actionError, setActionError] = useState<string | null>(null);
  const label = profile.data?.stableName ?? "storage profile";

  return (
    <ModalFrame label={`Manage storage profile ${label}`} title={profile.data?.stableName ?? "Storage profile"} subtitle="Append-only revisions and lifecycle. An Active route that names this profile follows it, so what changes here changes where the next write lands." onClose={onClose}>
      <div className="mdl-body">
        {profile.isLoading && <LoadingMessage>Loading profile…</LoadingMessage>}
        {profile.error && (
          <>
            <ErrorBanner title="Couldn't load storage profile" message={errorMessage(profile.error) ?? "The profile could not be loaded."} />
            <button type="button" className="btn" onClick={() => profile.refetch()}>Retry</button>
          </>
        )}
        {actionError && <div className="cn-banner cn-banner-err" role="alert"><div className="cn-banner-p">{actionError}</div></div>}
        {profile.data && (
          <StorageProfileEditor key={`${profile.data.xmin}:${profile.data.currentRevision}:${profile.data.state}`} detail={profile.data} providers={providers} credentials={credentials} onActionError={setActionError} />
        )}
      </div>
      <div className="mdl-foot">
        <span className="mdl-foot-info">Takes effect on the next write</span>
        <button type="button" className="btn" onClick={onClose}>Close</button>
      </div>
    </ModalFrame>
  );
}

function StorageProfileEditor({ detail, providers, credentials, onActionError }: { detail: StorageProfileDetail; providers: StorageProviderModuleSummary[]; credentials: StorageCredentialMetadata[]; onActionError: (message: string | null) => void }) {
  const appendRevision = useAppendStorageProfileRevision();
  const setState = useSetStorageProfileState();
  const probe = useProbeStorageProfile();
  const currentRevision = detail.revisions.find((revision) => revision.revision === detail.currentRevision);
  const [providerTypeKey, setProviderTypeKey] = useState(currentRevision?.providerTypeKey ?? "");
  const [config, setConfig] = useState<Record<string, unknown>>(() => currentRevision?.nonSecretConfig ?? {});
  const [credentialRef, setCredentialRef] = useState(() => typeof currentRevision?.credentialRef === "string" ? currentRevision.credentialRef : "");
  const [confirmRetire, setConfirmRetire] = useState(false);
  const [probeRevision, setProbeRevision] = useState("current");
  const [probeAccess, setProbeAccess] = useState<"read" | "write">("write");
  const [probeResult, setProbeResult] = useState<BoundStorageProfileProbeResult | null>(null);
  const [probeError, setProbeError] = useState<string | null>(null);
  const probeController = useRef<AbortController | null>(null);
  const selectedProvider = providers.find((provider) => provider.typeKey === providerTypeKey);
  const currentProvider = providers.find((provider) => provider.typeKey === currentRevision?.providerTypeKey);
  const currentCredentialRef = typeof currentRevision?.credentialRef === "string" && currentRevision.credentialRef !== "" ? currentRevision.credentialRef : undefined;
  const selectedCredentialRef = credentialRef || undefined;
  const currentNeedsCredential = currentProvider == null || providerRequiresCredential(currentProvider);
  const selectedNeedsCredential = selectedProvider != null && providerRequiresCredential(selectedProvider);
  const activationBlocked = currentProvider == null || (currentNeedsCredential && currentCredentialRef == null);
  const activeRevisionWouldLoseCredential = detail.state === "Active" && selectedNeedsCredential && selectedCredentialRef == null;
  const configValid = selectedProvider != null && requiredValuesPresent(selectedProvider.configSchema, config);
  const profileMutationPending = appendRevision.isPending || setState.isPending;
  const pending = profileMutationPending || probe.isPending;
  const retired = detail.state === "Retired";

  useEffect(() => () => probeController.current?.abort(), []);

  if (!currentRevision) {
    return <ErrorBanner title="Current revision unavailable" message="Refresh the profile before making changes." />;
  }

  const chooseProvider = (typeKey: string) => {
    const provider = providers.find((candidate) => candidate.typeKey === typeKey);
    setProviderTypeKey(typeKey);
    setConfig(typeKey === currentRevision.providerTypeKey ? currentRevision.nonSecretConfig : defaultsFromSchema(provider?.configSchema));
    setCredentialRef(typeKey === currentRevision.providerTypeKey ? currentCredentialRef ?? "" : "");
    onActionError(null);
  };

  const clearProbe = () => {
    probeController.current?.abort();
    probeController.current = null;
    probe.reset();
    setProbeResult(null);
    setProbeError(null);
  };

  const selectProbeRevision = (value: string) => {
    clearProbe();
    setProbeRevision(value);
  };

  const selectProbeAccess = (value: "read" | "write") => {
    clearProbe();
    setProbeAccess(value);
  };

  const runProbe = () => {
    if (pending || probeController.current != null) return;
    const requestedRevision = probeRevision === "current" ? null : Number(probeRevision);
    const boundRevision = requestedRevision ?? detail.currentRevision;
    const writeAccess = probeAccess === "write";
    const controller = new AbortController();
    probeController.current = controller;
    setProbeResult(null);
    setProbeError(null);
    probe.mutate({ profileId: detail.id, input: { profileRevision: requestedRevision, verifyWriteAccess: writeAccess }, signal: controller.signal }, {
      onSuccess: (result) => {
        if (controller.signal.aborted || probeController.current !== controller) return;
        const revisionMatches = result.profileRevision == null || result.profileRevision === boundRevision;
        if (result.profileId !== detail.id || !revisionMatches || result.writeAccessRequested !== writeAccess) {
          setProbeError("The probe response did not match the requested profile revision. Refresh the profile and try again.");
          return;
        }
        setProbeResult({ profileId: detail.id, profileRevision: boundRevision, result });
      },
      onError: () => {
        if (!controller.signal.aborted && probeController.current === controller) setProbeError("The runtime probe request couldn't be completed. Try again.");
      },
      onSettled: () => {
        if (probeController.current === controller) probeController.current = null;
      },
    });
  };

  const append = () => {
    if (!selectedProvider || !configValid || retired || activeRevisionWouldLoseCredential || pending) return;
    clearProbe();
    onActionError(null);
    appendRevision.mutate({
      profileId: detail.id,
      input: {
        expectedXmin: detail.xmin,
        expectedCurrentRevision: detail.currentRevision,
        providerTypeKey,
        nonSecretConfig: cleanConfig(config),
        ...(selectedCredentialRef ? { credentialRef: selectedCredentialRef } : {}),
      },
    }, {
      onError: (error) => onActionError(mutationErrorMessage(error, "Couldn't append the storage profile revision.")),
      onSuccess: () => onActionError(null),
    });
  };

  const transition = (state: Exclude<StorageProfileState, "Draft">) => {
    if (pending || retired || (state === "Active" && activationBlocked)) return;
    clearProbe();
    onActionError(null);
    setState.mutate({
      profileId: detail.id,
      input: { expectedXmin: detail.xmin, expectedCurrentRevision: detail.currentRevision, state },
    }, {
      onError: (error) => onActionError(mutationErrorMessage(error, `Couldn't set the storage profile ${state.toLowerCase()}.`)),
      onSuccess: () => onActionError(null),
    });
  };

  return (
    <>
      <div className="cn-banner">
        <div className="cn-banner-h">
          <span className={detail.state === "Active" ? "cn-status cn-status-active" : detail.state === "Retired" ? "cn-status cn-status-revoked" : "cn-status"}>{detail.state}</span>
          <span style={{ marginLeft: 8 }}>Current revision {detail.currentRevision}</span>
        </div>
        <div className="cn-banner-p">State changes and revisions use optimistic concurrency. Retired is terminal.</div>
      </div>

      <div style={{ borderBottom: "1px solid var(--line)", marginBottom: 18, paddingBottom: 16 }}>
        <div className="wf-form-label" style={{ marginBottom: 8 }}>Runtime probe</div>
        <div className="wf-form" style={{ display: "grid", gap: 10 }}>
          <div className="wf-form-row">
            <label className="wf-form-label" htmlFor="storage-profile-probe-revision">Probe revision</label>
            <select id="storage-profile-probe-revision" className="wf-form-input" value={probeRevision} disabled={pending} onChange={(event) => selectProbeRevision(event.target.value)}>
              <option value="current">Current revision ({detail.currentRevision})</option>
              {[...detail.revisions].sort((left, right) => right.revision - left.revision).map((revision) => <option key={revision.id} value={revision.revision}>Exact revision {revision.revision}</option>)}
            </select>
          </div>
          <div className="wf-form-row">
            <label className="wf-form-label" htmlFor="storage-profile-probe-access">Probe access</label>
            <select id="storage-profile-probe-access" className="wf-form-input" value={probeAccess} disabled={pending} onChange={(event) => selectProbeAccess(event.target.value === "read" ? "read" : "write")}>
              <option value="write">Read and write</option>
              <option value="read">Read only</option>
            </select>
          </div>
          <button type="button" className="btn" disabled={pending} onClick={runProbe}>{probe.isPending ? "Probing…" : `Run ${probeAccess} probe`}</button>
        </div>
        {probeError && <div className="cn-banner cn-banner-err" role="alert" style={{ marginTop: 10 }}><div className="cn-banner-p">{probeError}</div></div>}
        {probeResult && probeResult.profileId === detail.id && probeResult.profileRevision === (probeRevision === "current" ? detail.currentRevision : Number(probeRevision)) && <StorageProfileProbeResultView binding={probeResult} />}
      </div>

      {currentProvider && providerRequiresCredential(currentProvider) && currentCredentialRef == null && (
        <CredentialNotice>
          This provider requires a Storage Credential before this profile can be activated.
        </CredentialNotice>
      )}

      {currentProvider && providerRequiresCredential(currentProvider) && currentCredentialRef != null && (
        <CredentialNotice>A Storage Credential is linked. Its opaque reference is intentionally hidden and preserved only while the provider stays unchanged.</CredentialNotice>
      )}

      {!currentProvider && <CredentialNotice>The current provider is not installed in this deployment, so activation is unavailable.</CredentialNotice>}

      <div className="wf-form" style={{ marginTop: 16 }}>
        <div className="wf-form-row">
          <label className="wf-form-label" htmlFor="storage-profile-revision-provider">Revision provider</label>
          <select id="storage-profile-revision-provider" className="wf-form-input" value={providerTypeKey} disabled={retired || pending} onChange={(event) => chooseProvider(event.target.value)}>
            {!selectedProvider && <option value={providerTypeKey}>{providerTypeKey} (not installed)</option>}
            {providers.map((provider) => <option key={provider.typeKey} value={provider.typeKey}>{provider.displayName}</option>)}
          </select>
        </div>

        {selectedProvider && (
          <div role="group" aria-label="Revision non-secret configuration">
            <SchemaForm schema={selectedProvider.configSchema} value={config} onChange={setConfig} />
          </div>
        )}

        {selectedProvider && providerHasSecretInputs(selectedProvider) && <CredentialSelector providerTypeKey={providerTypeKey} credentials={credentials} value={credentialRef} onChange={setCredentialRef} />}

        {selectedNeedsCredential && selectedCredentialRef == null && (
          <CredentialNotice>This revision will not contain credentials and cannot be activated.</CredentialNotice>
        )}

        {activeRevisionWouldLoseCredential && <div className="wf-form-help wf-form-help-err">Disable the profile before appending a credentialless revision.</div>}

        <button type="button" className="btn" disabled={!configValid || retired || activeRevisionWouldLoseCredential || pending} onClick={append}>
          {appendRevision.isPending ? "Appending…" : "Append revision"}
        </button>
      </div>

      <div style={{ borderTop: "1px solid var(--line)", marginTop: 18, paddingTop: 16 }}>
        <div className="wf-form-label" style={{ marginBottom: 8 }}>Profile state</div>
        <div style={{ display: "flex", flexWrap: "wrap", gap: 8 }}>
          <button type="button" className="btn" disabled={retired || detail.state === "Active" || activationBlocked || pending} title={activationBlocked ? "Link a required Storage Credential before activation" : undefined} onClick={() => transition("Active")}>Set Active</button>
          <button type="button" className="btn" disabled={retired || detail.state === "Disabled" || pending} onClick={() => transition("Disabled")}>Set Disabled</button>
          <button type="button" className="btn btn-danger" disabled={retired || pending} onClick={() => setConfirmRetire(true)}>Retire profile</button>
        </div>
      </div>

      <StoragePlacementDrain profileId={detail.id} disabled={retired || pending} />

      {confirmRetire && <RetireConfirmation stableName={detail.stableName} onCancel={() => setConfirmRetire(false)} onConfirm={() => { setConfirmRetire(false); transition("Retired"); }} />}
    </>
  );
}

interface BoundStorageProfileProbeResult {
  profileId: string;
  profileRevision: number;
  result: StorageProfileProbeResult;
}

function StorageProfileProbeResultView({ binding }: { binding: BoundStorageProfileProbeResult }) {
  const { result } = binding;
  const statusClass = result.status === "Available" ? "cn-status cn-status-active" : result.status === "Unavailable" || result.status === "Cancelled" ? "cn-status cn-status-revoked" : "cn-status cn-status-warn";
  return (
    <div className="cn-banner" role="status" aria-label="Storage probe result" style={{ marginTop: 10 }}>
      <div className="cn-banner-h"><span className={statusClass}>{result.status}</span></div>
      <div className="cn-banner-p">Revision {binding.profileRevision} · {result.writeAccessRequested ? "Read and write" : "Read only"} · {result.latencyMilliseconds} ms</div>
      {result.failure && <>
        <div className="cn-banner-p">Stage {result.failure.stage} · Code {result.failure.code} · {result.failure.retryable ? "Retryable" : "Not retryable"}</div>
        {probeFailureGuidance(result.failure.code) && <div className="cn-banner-p">{probeFailureGuidance(result.failure.code)}</div>}
      </>}
    </div>
  );
}


function RetireConfirmation({ stableName, onCancel, onConfirm }: { stableName: string; onCancel: () => void; onConfirm: () => void }) {
  const confirmRef = useRef<HTMLButtonElement>(null);
  useEffect(() => { confirmRef.current?.focus(); }, []);

  return createPortal(
    <>
      <div className="mdl-mask" aria-hidden="true" style={{ zIndex: 90 }} />
      <div className="mdl mdl-dialog" role="alertdialog" aria-modal="true" aria-label={`Retire ${stableName}?`} style={{ zIndex: 91 }}>
        <div className="mdl-dialog-head"><div className="mdl-dialog-title">Retire {stableName}?</div></div>
        <div className="mdl-dialog-body">Retirement is terminal. This profile cannot receive revisions or change state afterward.</div>
        <div className="mdl-dialog-foot">
          <button type="button" className="btn" onClick={onCancel}>Cancel</button>
          <button ref={confirmRef} type="button" className="btn btn-danger" onClick={onConfirm}>Retire permanently</button>
        </div>
      </div>
    </>,
    document.body,
  );
}

function ModalFrame({ label, title, subtitle, onClose, children }: { label: string; title: string; subtitle: string; onClose: () => void; children: ReactNode }) {
  return createPortal(
    <>
      <div className="mdl-mask" aria-hidden="true" />
      <div className="mdl" role="dialog" aria-modal="true" aria-label={label} style={{ width: 640, maxWidth: "94vw" }}>
        <div className="mdl-head">
          <div className="mdl-title-wrap">
            <div className="mdl-title">{title}</div>
            <div className="mdl-sub">{subtitle}</div>
          </div>
          <button type="button" className="mdl-x" aria-label="Close" onClick={onClose}>×</button>
        </div>
        {children}
      </div>
    </>,
    document.body,
  );
}

function CredentialNotice({ children }: { children: ReactNode }) {
  return <div className="cn-banner" style={{ marginTop: 12 }}><div className="cn-banner-h">Credential boundary</div><div className="cn-banner-p">{children}</div></div>;
}

function CredentialSelector({ providerTypeKey, credentials, value, onChange }: { providerTypeKey: string; credentials: StorageCredentialMetadata[]; value: string; onChange: (credentialRef: string) => void }) {
  const eligible = credentials.filter((credential) => credential.state === "Active" && credential.providerTypeKey === providerTypeKey);
  const current = eligible.find((credential) => credential.credentialRef === value);
  const selected = current?.id ?? (value ? "__pinned__" : "");
  return (
    <div className="wf-form-row">
      <label className="wf-form-label" htmlFor="storage-profile-credential">Storage credential</label>
      <select
        id="storage-profile-credential"
        className="wf-form-input"
        value={selected}
        onChange={(event) => {
          if (event.target.value === "") { onChange(""); return; }
          if (event.target.value === "__pinned__") return;
          onChange(eligible.find((credential) => credential.id === event.target.value)?.credentialRef ?? "");
        }}
      >
        <option value="">— none —</option>
        {value && !current && <option value="__pinned__">Current linked credential (pinned revision)</option>}
        {eligible.map((credential) => <option key={credential.id} value={credential.id}>{credential.stableName} · revision {credential.currentRevision}{credential.safeHint ? ` · ${credential.safeHint}` : ""}</option>)}
      </select>
      {eligible.length === 0 && <span className="wf-form-help">No active credential matches this provider.</span>}
    </div>
  );
}

function ErrorBanner({ title, message }: { title: string; message: string }) {
  return <div className="cn-banner cn-banner-err" role="alert"><div className="cn-banner-h">{title}</div><div className="cn-banner-p">{message}</div></div>;
}

function LoadingMessage({ children }: { children: ReactNode }) {
  return <div className="ct-empty" role="status"><div className="ct-empty-h">{children}</div></div>;
}

function defaultsFromSchema(schema: unknown): Record<string, unknown> {
  if (!isRecord(schema) || !isRecord(schema.properties)) return {};
  const defaults: Record<string, unknown> = {};
  for (const [key, value] of Object.entries(schema.properties)) {
    if (!isRecord(value)) continue;
    if (value.default !== undefined) {
      defaults[key] = value.default;
      continue;
    }
    if (value.type === "object") {
      const nested = defaultsFromSchema(value);
      if (Object.keys(nested).length > 0) defaults[key] = nested;
    }
  }
  return defaults;
}

function requiredValuesPresent(schema: unknown, value: unknown): boolean {
  if (!isRecord(schema)) return true;
  if (schema.type === "object") {
    if (!isRecord(value)) return false;
    const required = Array.isArray(schema.required) ? schema.required.filter((key): key is string => typeof key === "string") : [];
    const properties = isRecord(schema.properties) ? schema.properties : {};
    return required.every((key) => Object.prototype.hasOwnProperty.call(value, key) && value[key] !== undefined && requiredValuesPresent(properties[key], value[key]));
  }
  if (schema.type === "array" && typeof schema.minItems === "number") return Array.isArray(value) && value.length >= schema.minItems;
  if (schema.type === "string" && typeof schema.minLength === "number") return typeof value === "string" && [...value].length >= schema.minLength;
  return value !== undefined;
}

function cleanConfig(value: Record<string, unknown>): Record<string, unknown> {
  return pruneUndefined(value) as Record<string, unknown>;
}

function pruneUndefined(value: unknown): unknown {
  if (Array.isArray(value)) return value.filter((item) => item !== undefined).map(pruneUndefined);
  if (!isRecord(value)) return value;
  return Object.fromEntries(Object.entries(value).filter(([, item]) => item !== undefined).map(([key, item]) => [key, pruneUndefined(item)]));
}

function providerRequiresCredential(provider: StorageProviderModuleSummary): boolean {
  const required = provider.secretSchema.required;
  return Array.isArray(required) && required.some((value) => typeof value === "string");
}

function providerHasSecretInputs(provider: StorageProviderModuleSummary): boolean {
  return isRecord(provider.secretSchema.properties) && Object.keys(provider.secretSchema.properties).length > 0;
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

function errorMessage(error: unknown): string | null {
  if (error instanceof ApiError || error instanceof Error) return error.message;
  return error == null ? null : "An unexpected storage error occurred.";
}

/**
 * A 409 is not always a concurrent edit. `StorageProfileService` also refuses retirement while active
 * routes or stored locations still live under the profile, and names both the count and the fix in the
 * message. Collapsing every 409 into "this changed elsewhere" replaced a true, actionable sentence with
 * a false one, so the server's own words are shown and only the reload note is added.
 */
function mutationErrorMessage(error: unknown, fallback: string): string {
  if (!(error instanceof ApiError) || error.status !== 409) return errorMessage(error) ?? fallback;
  const reason = error.message.trim();
  return reason.length === 0 ? fallback : `${reason} The latest data was reloaded — review it before trying again.`;
}

function providerInitials(displayName: string): string {
  const words = displayName.match(/[A-Za-z0-9]+/g) ?? [];
  return words.slice(0, 2).map((word) => word[0]).join("").toUpperCase() || "ST";
}

function capabilityLabel(capability: string): string {
  const words = capability.replace(/([a-z0-9])([A-Z])/g, "$1 $2").toLowerCase();
  return words.replace(/^./, (value) => value.toUpperCase());
}
