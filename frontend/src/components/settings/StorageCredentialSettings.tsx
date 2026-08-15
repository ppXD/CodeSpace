import { useEffect, useRef, useState, type ReactNode } from "react";
import { createPortal } from "react-dom";

import { ApiError } from "@/api/request";
import type { StorageCredentialMetadata, StorageProviderModuleSummary } from "@/api/storage";
import { useAppendStorageCredentialRevision, useCreateStorageCredential, useRevokeStorageCredential, useStorageCredentials } from "@/hooks/use-storage";
import { SchemaForm } from "@/components/workflows/SchemaForm";

export function StorageCredentialSettings({ providers }: { providers: StorageProviderModuleSummary[] }) {
  const credentials = useStorageCredentials();
  const [createOpen, setCreateOpen] = useState(false);
  const [managedId, setManagedId] = useState<string | null>(null);
  const rows = credentials.data ?? [];
  const error = errorMessage(credentials.error);
  const managed = rows.find((value) => value.id === managedId);
  const secretProviders = providers.filter(hasSecretInputs);

  return (
    <section aria-labelledby="storage-credentials-title" style={{ margin: 16 }}>
      <div className="cn-listhead">
        <h3 className="cn-listhead-l" id="storage-credentials-title">Storage credentials</h3>
        <button type="button" className="btn btn-primary" disabled={secretProviders.length === 0} onClick={() => setCreateOpen(true)}>Create storage credential</button>
      </div>

      {credentials.isLoading && <LoadingMessage>Loading storage credentials…</LoadingMessage>}
      {error && <ErrorBanner title="Couldn't load storage credentials" message={error} />}
      {!credentials.isLoading && !error && rows.length === 0 && (
        <div className="ct-empty">
          <div className="ct-empty-h">No storage credentials configured</div>
          <div className="ct-empty-p">Credentials are encrypted, team-scoped, revisioned, and never shown again after submission.</div>
        </div>
      )}
      {!credentials.isLoading && !error && rows.length > 0 && (
        <div className="cn-list" role="list" aria-label="Storage credentials">
          {rows.map((credential) => <StorageCredentialRow key={credential.id} credential={credential} provider={providers.find((provider) => provider.typeKey === credential.providerTypeKey)} onManage={() => setManagedId(credential.id)} />)}
        </div>
      )}

      {createOpen && <CreateStorageCredentialDialog providers={secretProviders} onClose={() => setCreateOpen(false)} />}
      {managedId && managed && <ManageStorageCredentialDialog key={`${managed.id}:${managed.xmin}:${managed.currentRevision}`} credential={managed} provider={providers.find((value) => value.typeKey === managed.providerTypeKey)} onClose={() => setManagedId(null)} />}
    </section>
  );
}

function StorageCredentialRow({ credential, provider, onManage }: { credential: StorageCredentialMetadata; provider?: StorageProviderModuleSummary; onManage: () => void }) {
  return (
    <div className="cn-row" role="listitem">
      <div className="cn-row-head">
        <div className="cn-mark">{providerInitials(provider?.displayName ?? credential.stableName)}</div>
        <div className="cn-meta">
          <div className="cn-name">
            {credential.stableName}
            <span className={credential.state === "Active" ? "cn-status cn-status-active" : "cn-status cn-status-revoked"}>{credential.state}</span>
            <span className="cn-status">Revision {credential.currentRevision}</span>
          </div>
          <div className="cn-sub"><span>{provider?.displayName ?? credential.providerTypeKey}</span>{credential.safeHint && <span>{credential.safeHint}</span>}</div>
        </div>
        <button type="button" className="btn" aria-label={`Manage credential ${credential.stableName}`} onClick={onManage}>Manage</button>
      </div>
    </div>
  );
}

function CreateStorageCredentialDialog({ providers, onClose }: { providers: StorageProviderModuleSummary[]; onClose: () => void }) {
  const [providerTypeKey, setProviderTypeKey] = useState(providers[0]?.typeKey ?? "");
  const [stableName, setStableName] = useState("");
  const [safeHint, setSafeHint] = useState("");
  const selected = providers.find((provider) => provider.typeKey === providerTypeKey);
  const [secret, setSecret] = useState<Record<string, unknown>>(() => defaultsFromSchema(selected?.secretSchema));
  const [formError, setFormError] = useState<string | null>(null);
  const create = useCreateStorageCredential();
  const normalizedName = stableName.trim().toLowerCase();
  const canSubmit = selected != null && /^[a-z0-9][a-z0-9-]{0,127}$/.test(normalizedName) && requiredValuesPresent(selected.secretSchema, secret) && !create.isPending;

  const chooseProvider = (typeKey: string) => {
    const provider = providers.find((value) => value.typeKey === typeKey);
    setProviderTypeKey(typeKey);
    setSecret(defaultsFromSchema(provider?.secretSchema));
    setFormError(null);
  };

  const submit = () => {
    if (!selected || !canSubmit) return;
    const payload = { stableName: normalizedName, providerTypeKey, secret: cleanObject(secret), ...(safeHint.trim() ? { safeHint: safeHint.trim() } : {}) };
    setFormError(null);
    create.mutate(payload, {
      onSuccess: () => { setSecret({}); create.reset(); onClose(); },
      onError: (error) => { setSecret({}); create.reset(); setFormError(errorMessage(error) ?? "Couldn't create the storage credential."); },
    });
  };

  return (
    <ModalFrame label="Create storage credential" title="Create storage credential" subtitle="Secret values are write-only. They are encrypted before persistence and are never returned by the API." onClose={() => { setSecret({}); create.reset(); onClose(); }}>
      <div className="mdl-body"><div className="wf-form">
        <LabeledInput id="storage-credential-name" label="Stable name" value={stableName} onChange={setStableName} autoFocus />
        <div className="wf-form-row">
          <label className="wf-form-label" htmlFor="storage-credential-provider">Provider</label>
          <select id="storage-credential-provider" className="wf-form-input" value={providerTypeKey} onChange={(event) => chooseProvider(event.target.value)}>
            {providers.map((provider) => <option key={provider.typeKey} value={provider.typeKey}>{provider.displayName}</option>)}
          </select>
        </div>
        {selected && <div role="group" aria-label="Write-only secret"><SchemaForm schema={selected.secretSchema} value={secret} onChange={setSecret} sensitive /></div>}
        <LabeledInput id="storage-credential-hint" label="Safe hint" value={safeHint} onChange={setSafeHint} placeholder="Optional non-secret identifier" />
        {formError && <ErrorBanner title="Couldn't create storage credential" message={formError} />}
      </div></div>
      <div className="mdl-foot"><button type="button" className="btn btn-ghost" onClick={() => { setSecret({}); create.reset(); onClose(); }}>Cancel</button><button type="button" className="btn btn-primary" disabled={!canSubmit} onClick={submit}>{create.isPending ? "Creating…" : "Create credential"}</button></div>
    </ModalFrame>
  );
}

function ManageStorageCredentialDialog({ credential, provider, onClose }: { credential: StorageCredentialMetadata; provider?: StorageProviderModuleSummary; onClose: () => void }) {
  const [secret, setSecret] = useState<Record<string, unknown>>(() => defaultsFromSchema(provider?.secretSchema));
  const [safeHint, setSafeHint] = useState(credential.safeHint ?? "");
  const [error, setError] = useState<string | null>(null);
  const [confirmRevoke, setConfirmRevoke] = useState(false);
  const rotate = useAppendStorageCredentialRevision();
  const revoke = useRevokeStorageCredential();
  const active = credential.state === "Active";
  const valid = provider != null && requiredValuesPresent(provider.secretSchema, secret);

  const rotateCredential = () => {
    if (!active || !provider || !valid || rotate.isPending) return;
    setError(null);
    rotate.mutate({ credentialId: credential.id, input: { expectedXmin: credential.xmin, expectedCurrentRevision: credential.currentRevision, providerTypeKey: credential.providerTypeKey, secret: cleanObject(secret), ...(safeHint.trim() ? { safeHint: safeHint.trim() } : {}) } }, {
      onSuccess: () => { setSecret({}); rotate.reset(); },
      onError: (reason) => { setSecret({}); rotate.reset(); setError(mutationErrorMessage(reason, "Couldn't rotate the storage credential.")); },
    });
  };

  const revokeCredential = () => {
    if (!active || revoke.isPending) return;
    setError(null);
    revoke.mutate({ credentialId: credential.id, input: { expectedXmin: credential.xmin, expectedCurrentRevision: credential.currentRevision } }, {
      onError: (reason) => setError(mutationErrorMessage(reason, "Couldn't revoke the storage credential.")),
    });
  };

  return (
    <ModalFrame label={`Manage storage credential ${credential.stableName}`} title={credential.stableName} subtitle="Append-only secret rotation and terminal revocation. Existing secret values are never read back." onClose={() => { setSecret({}); rotate.reset(); onClose(); }}>
      <div className="mdl-body">
        <div className="cn-banner"><div className="cn-banner-h"><span className={active ? "cn-status cn-status-active" : "cn-status cn-status-revoked"}>{credential.state}</span><span style={{ marginLeft: 8 }}>Current revision {credential.currentRevision}</span></div><div className="cn-banner-p">{provider?.displayName ?? credential.providerTypeKey}{credential.safeHint ? ` · ${credential.safeHint}` : ""}</div></div>
        {error && <ErrorBanner title="Storage credential action failed" message={error} />}
        {active && provider && <div className="wf-form" style={{ marginTop: 16 }}><div role="group" aria-label="Rotated write-only secret"><SchemaForm schema={provider.secretSchema} value={secret} onChange={setSecret} sensitive /></div><LabeledInput id="storage-credential-rotate-hint" label="Safe hint" value={safeHint} onChange={setSafeHint} placeholder="Optional non-secret identifier" /><button type="button" className="btn" disabled={!valid || rotate.isPending || revoke.isPending} onClick={rotateCredential}>{rotate.isPending ? "Rotating…" : "Rotate credential"}</button></div>}
        {active && !provider && <ErrorBanner title="Provider unavailable" message="This credential's provider module is not installed, so it cannot be rotated in this deployment." />}
        {active && <div style={{ borderTop: "1px solid var(--line)", marginTop: 18, paddingTop: 16 }}><button type="button" className="btn btn-danger" disabled={rotate.isPending || revoke.isPending} onClick={() => setConfirmRevoke(true)}>Revoke credential</button></div>}
      </div>
      <div className="mdl-foot"><span className="mdl-foot-info">Secrets are write-only</span><button type="button" className="btn" onClick={() => { setSecret({}); rotate.reset(); onClose(); }}>Close</button></div>
      {confirmRevoke && <RevokeConfirmation stableName={credential.stableName} onCancel={() => setConfirmRevoke(false)} onConfirm={() => { setConfirmRevoke(false); revokeCredential(); }} />}
    </ModalFrame>
  );
}

function RevokeConfirmation({ stableName, onCancel, onConfirm }: { stableName: string; onCancel: () => void; onConfirm: () => void }) {
  const confirmRef = useRef<HTMLButtonElement>(null);
  useEffect(() => { confirmRef.current?.focus(); }, []);
  return createPortal(<><div className="mdl-mask" aria-hidden="true" style={{ zIndex: 90 }} /><div className="mdl mdl-dialog" role="alertdialog" aria-modal="true" aria-label={`Revoke ${stableName}?`} style={{ zIndex: 91 }}><div className="mdl-dialog-head"><div className="mdl-dialog-title">Revoke {stableName}?</div></div><div className="mdl-dialog-body">Revocation is terminal. Profiles pinned to this credential will fail readiness until a new credential revision is selected.</div><div className="mdl-dialog-foot"><button type="button" className="btn" onClick={onCancel}>Cancel</button><button ref={confirmRef} type="button" className="btn btn-danger" onClick={onConfirm}>Revoke permanently</button></div></div></>, document.body);
}

function LabeledInput({ id, label, value, onChange, placeholder, autoFocus = false }: { id: string; label: string; value: string; onChange: (value: string) => void; placeholder?: string; autoFocus?: boolean }) {
  return <div className="wf-form-row"><label className="wf-form-label" htmlFor={id}>{label}</label><input id={id} className="wf-form-input" value={value} placeholder={placeholder} autoFocus={autoFocus} onChange={(event) => onChange(event.target.value)} /></div>;
}

function ModalFrame({ label, title, subtitle, onClose, children }: { label: string; title: string; subtitle: string; onClose: () => void; children: ReactNode }) {
  return createPortal(<><div className="mdl-mask" aria-hidden="true" /><div className="mdl" role="dialog" aria-modal="true" aria-label={label} style={{ width: 640, maxWidth: "94vw" }}><div className="mdl-head"><div className="mdl-title-wrap"><div className="mdl-title">{title}</div><div className="mdl-sub">{subtitle}</div></div><button type="button" className="mdl-x" aria-label="Close" onClick={onClose}>×</button></div>{children}</div></>, document.body);
}

function ErrorBanner({ title, message }: { title: string; message: string }) {
  return <div className="cn-banner cn-banner-err" role="alert"><div className="cn-banner-h">{title}</div><div className="cn-banner-p">{message}</div></div>;
}

function LoadingMessage({ children }: { children: ReactNode }) {
  return <div className="ct-empty" role="status"><div className="ct-empty-h">{children}</div></div>;
}

function defaultsFromSchema(schema: unknown): Record<string, unknown> {
  if (!isRecord(schema) || !isRecord(schema.properties)) return {};
  return Object.fromEntries(Object.entries(schema.properties).flatMap(([key, value]) => {
    if (!isRecord(value)) return [];
    if (value.default !== undefined) return [[key, value.default]];
    if (value.type === "object") { const nested = defaultsFromSchema(value); return Object.keys(nested).length > 0 ? [[key, nested]] : []; }
    return [];
  }));
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

function cleanObject(value: Record<string, unknown>): Record<string, unknown> {
  return pruneUndefined(value) as Record<string, unknown>;
}

function pruneUndefined(value: unknown): unknown {
  if (Array.isArray(value)) return value.filter((item) => item !== undefined).map(pruneUndefined);
  if (!isRecord(value)) return value;
  return Object.fromEntries(Object.entries(value).filter(([, item]) => item !== undefined).map(([key, item]) => [key, pruneUndefined(item)]));
}

function hasSecretInputs(provider: StorageProviderModuleSummary): boolean {
  return isRecord(provider.secretSchema.properties) && Object.keys(provider.secretSchema.properties).length > 0;
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

function errorMessage(error: unknown): string | null {
  if (error instanceof ApiError || error instanceof Error) return error.message;
  return error == null ? null : "An unexpected storage credential error occurred.";
}

function mutationErrorMessage(error: unknown, fallback: string): string {
  if (error instanceof ApiError && error.status === 409) return "This credential changed elsewhere. The latest metadata was reloaded; review it and try again.";
  return errorMessage(error) ?? fallback;
}

function providerInitials(displayName: string): string {
  const words = displayName.match(/[A-Za-z0-9]+/g) ?? [];
  return words.slice(0, 2).map((word) => word[0]).join("").toUpperCase() || "SC";
}
