import { useEffect, useRef, useState, type ReactNode } from "react";
import { createPortal } from "react-dom";

import { ApiError } from "@/api/request";
import type { StorageCredentialMetadata, StorageProfileDetail, StorageProfileState, StorageProfileSummary, StorageProviderModuleSummary } from "@/api/storage";
import { useAppendStorageProfileRevision, useCreateStorageProfile, useSetStorageProfileState, useStorageCredentials, useStorageProfile, useStorageProfiles, useStorageProviderModules } from "@/hooks/use-storage";
import { SchemaForm } from "@/components/workflows/SchemaForm";
import { StorageCredentialSettings } from "./StorageCredentialSettings";

/** Settings → Storage profile control plane. Runtime ArtifactStore selection remains deployment-managed. */
export function StorageSettings() {
  const providers = useStorageProviderModules();
  const credentials = useStorageCredentials();
  const profiles = useStorageProfiles();
  const [createOpen, setCreateOpen] = useState(false);
  const [managedProfileId, setManagedProfileId] = useState<string | null>(null);
  const providerRows = providers.data ?? [];
  const profileRows = profiles.data ?? [];
  const credentialRows = credentials.data ?? [];
  const providerError = errorMessage(providers.error);
  const profileError = errorMessage(profiles.error);

  return (
    <div aria-labelledby="storage-settings-title">
      <div className="cn-banner" style={{ margin: 16 }}>
        <h2 className="cn-banner-h" id="storage-settings-title">Artifact storage</h2>
        <div className="cn-banner-p">
          Active profiles are control-plane configuration only. The current ArtifactStore and runtime remain
          deployment-managed until qualification and cutover are complete.
        </div>
      </div>

      <StorageCredentialSettings providers={providerRows} />

      <section aria-labelledby="storage-profiles-title" style={{ margin: 16 }}>
        <div className="cn-listhead">
          <h3 className="cn-listhead-l" id="storage-profiles-title">Storage profiles</h3>
          <button type="button" className="btn btn-primary" disabled={providers.isLoading || providerError != null || providerRows.length === 0} onClick={() => setCreateOpen(true)}>
            Create storage profile
          </button>
        </div>

        {profiles.isLoading && <LoadingMessage>Loading storage profiles…</LoadingMessage>}

        {profileError && (
          <ErrorBanner title="Couldn't load storage profiles" message={profileError} />
        )}

        {!profiles.isLoading && !profileError && profileRows.length === 0 && (
          <div className="ct-empty">
            <div className="ct-empty-h">No storage profiles configured</div>
            <div className="ct-empty-p">Create a Draft from an installed provider. Drafts do not change runtime storage.</div>
          </div>
        )}

        {!profiles.isLoading && !profileError && profileRows.length > 0 && (
          <>
            <div className="cn-list" role="list" aria-label="Storage profiles">
              {profileRows.map((profile) => (
                <StorageProfileRow key={profile.id} profile={profile} provider={providerRows.find((provider) => provider.typeKey === profile.providerTypeKey)} onManage={() => setManagedProfileId(profile.id)} />
              ))}
            </div>
            {profiles.hasNextPage && (
              <button type="button" className="btn" disabled={profiles.isFetchingNextPage} onClick={() => profiles.fetchNextPage()}>
                {profiles.isFetchingNextPage ? "Loading more profiles…" : "Load more profiles"}
              </button>
            )}
          </>
        )}
      </section>

      <section aria-labelledby="storage-providers-title" style={{ margin: 16 }}>
        <div className="cn-listhead">
          <h3 className="cn-listhead-l" id="storage-providers-title">Installed providers</h3>
          <span className="cn-listhead-c">Deployment catalog</span>
        </div>

        {providers.isLoading && <LoadingMessage>Loading storage providers…</LoadingMessage>}

        {providerError && <ErrorBanner title="Couldn't load storage providers" message={providerError} />}

        {!providers.isLoading && !providerError && providerRows.length === 0 && (
          <div className="ct-empty">
            <div className="ct-empty-h">No storage provider modules installed</div>
            <div className="ct-empty-p">
              Provider packages will appear here when installed. This does not change where current run artifacts,
              model calls, or logs are written.
            </div>
          </div>
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

function StorageProfileRow({ profile, provider, onManage }: { profile: StorageProfileSummary; provider?: StorageProviderModuleSummary; onManage: () => void }) {
  return (
    <div className="cn-row" role="listitem">
      <div className="cn-row-head">
        <div className="cn-mark">{providerInitials(provider?.displayName ?? profile.stableName)}</div>
        <div className="cn-meta">
          <div className="cn-name">
            {profile.stableName}
            <span className={profile.state === "Active" ? "cn-status cn-status-active" : profile.state === "Retired" ? "cn-status cn-status-revoked" : "cn-status"}>{profile.state}</span>
            <span className="cn-status">Revision {profile.currentRevision}</span>
          </div>
          <div className="cn-sub">
            <span>{provider?.displayName ?? profile.providerTypeKey}</span>
            {provider && <span>{profile.providerTypeKey}</span>}
          </div>
        </div>
        <button type="button" className="btn" aria-label={`Manage ${profile.stableName}`} onClick={onManage}>Manage</button>
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
    <ModalFrame label={`Manage storage profile ${label}`} title={profile.data?.stableName ?? "Storage profile"} subtitle="Append-only revision and lifecycle controls. Runtime storage is not changed here." onClose={onClose}>
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
        <span className="mdl-foot-info">Control plane only</span>
        <button type="button" className="btn" onClick={onClose}>Close</button>
      </div>
    </ModalFrame>
  );
}

function StorageProfileEditor({ detail, providers, credentials, onActionError }: { detail: StorageProfileDetail; providers: StorageProviderModuleSummary[]; credentials: StorageCredentialMetadata[]; onActionError: (message: string | null) => void }) {
  const appendRevision = useAppendStorageProfileRevision();
  const setState = useSetStorageProfileState();
  const currentRevision = detail.revisions.find((revision) => revision.revision === detail.currentRevision);
  const [providerTypeKey, setProviderTypeKey] = useState(currentRevision?.providerTypeKey ?? "");
  const [config, setConfig] = useState<Record<string, unknown>>(() => currentRevision?.nonSecretConfig ?? {});
  const [credentialRef, setCredentialRef] = useState(() => typeof currentRevision?.credentialRef === "string" ? currentRevision.credentialRef : "");
  const [confirmRetire, setConfirmRetire] = useState(false);
  const selectedProvider = providers.find((provider) => provider.typeKey === providerTypeKey);
  const currentProvider = providers.find((provider) => provider.typeKey === currentRevision?.providerTypeKey);
  const currentCredentialRef = typeof currentRevision?.credentialRef === "string" && currentRevision.credentialRef !== "" ? currentRevision.credentialRef : undefined;
  const selectedCredentialRef = credentialRef || undefined;
  const currentNeedsCredential = currentProvider == null || providerRequiresCredential(currentProvider);
  const selectedNeedsCredential = selectedProvider != null && providerRequiresCredential(selectedProvider);
  const activationBlocked = currentProvider == null || (currentNeedsCredential && currentCredentialRef == null);
  const activeRevisionWouldLoseCredential = detail.state === "Active" && selectedNeedsCredential && selectedCredentialRef == null;
  const configValid = selectedProvider != null && requiredValuesPresent(selectedProvider.configSchema, config);
  const pending = appendRevision.isPending || setState.isPending;
  const retired = detail.state === "Retired";

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

  const append = () => {
    if (!selectedProvider || !configValid || retired || activeRevisionWouldLoseCredential || pending) return;
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

      {confirmRetire && <RetireConfirmation stableName={detail.stableName} onCancel={() => setConfirmRetire(false)} onConfirm={() => { setConfirmRetire(false); transition("Retired"); }} />}
    </>
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

function mutationErrorMessage(error: unknown, fallback: string): string {
  if (error instanceof ApiError && error.status === 409) return "This profile changed elsewhere. The latest data was reloaded; review the latest revision and try again.";
  return errorMessage(error) ?? fallback;
}

function providerInitials(displayName: string): string {
  const words = displayName.match(/[A-Za-z0-9]+/g) ?? [];
  return words.slice(0, 2).map((word) => word[0]).join("").toUpperCase() || "ST";
}

function capabilityLabel(capability: string): string {
  const words = capability.replace(/([a-z0-9])([A-Z])/g, "$1 $2").toLowerCase();
  return words.replace(/^./, (value) => value.toUpperCase());
}
