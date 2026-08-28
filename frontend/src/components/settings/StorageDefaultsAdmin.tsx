import { useMemo, useState } from "react";

import { ApiError } from "@/api/request";
import type { StorageProviderModuleSummary } from "@/api/storage";
import type { StorageDefaultAdoptionPolicy, StorageDefaultSummary } from "@/api/storageDefaults";
import { SchemaForm } from "@/components/workflows/SchemaForm";

import { useCreateStorageDefault, useSetStorageDefaultEnabled, useStorageDefault, useStorageDefaultDataClasses, useStorageDefaultProviders, useStorageDefaults, useUpdateStorageDefault } from "@/hooks/use-storage-defaults";

/**
 * Deployment administration → Storage. One default destination per class of data, offered to every team
 * on this instance.
 *
 * <p>Authoring one moves nothing. A team is put on a default only when its own admin adopts it, or — for
 * a class whose default says so — on that team's first write. Editing one changes what a LATER adoption
 * produces and never touches a team already on it, because a read resolves through the configuration
 * recorded when the bytes were written.</p>
 */
export function StorageDefaultsAdmin() {
  const defaults = useStorageDefaults();
  const providers = useStorageDefaultProviders();
  const dataClasses = useStorageDefaultDataClasses();
  const [creating, setCreating] = useState(false);

  if (defaults.error instanceof ApiError && defaults.error.status === 403) {
    return (
      <div className="ct-empty">
        <div className="ct-empty-h">Not yours to see</div>
        <div className="ct-empty-p">Storage defaults are set by instance administrators.</div>
      </div>
    );
  }

  const rows = defaults.data ?? [];
  const providerRows = providers.data ?? [];
  const classRows = dataClasses.data ?? [];
  const unauthored = classRows.filter((dataClass) => !rows.some((row) => row.dataClassTypeKey === dataClass.typeKey));

  return (
    <>
      <div className="cn-banner" style={{ margin: 16 }}>
        <div className="cn-banner-h">Storage defaults for this deployment</div>
        <div className="cn-banner-p">
          One destination per class of data, prepared once and offered to every team. Each team that takes one gets
          its own namespace inside it, so no two teams ever share a place to put bytes.
        </div>
        <div className="cn-banner-p">
          Authoring a default moves nothing on its own, and editing one never touches a team already on it — what a
          team has already stored keeps resolving through the configuration it was written with.
        </div>
      </div>

      {defaults.isLoading && <div className="ct-empty"><div className="ct-empty-h">Loading…</div></div>}

      <div className="cn-list" style={{ margin: 16 }} role="list" aria-label="Storage defaults">
        {rows.map((row) => <DefaultRow key={row.id} row={row} providers={providerRows} />)}
        {!defaults.isLoading && rows.length === 0 && (
          <div className="stg-hint">No default is authored yet, so every team configures its own storage.</div>
        )}
      </div>

      <div style={{ margin: 16 }}>
        {creating
          ? <DefaultEditor providers={providerRows} unauthored={unauthored} onDone={() => setCreating(false)} />
          : unauthored.length > 0 && <button type="button" className="btn btn-primary" onClick={() => setCreating(true)}>New default</button>}
        {unauthored.length === 0 && rows.length > 0 && (
          <div className="stg-hint">Every class of data already has a default. Edit one below to change where it points.</div>
        )}
      </div>
    </>
  );
}

function DefaultRow({ row, providers }: { row: StorageDefaultSummary; providers: StorageProviderModuleSummary[] }) {
  const [editing, setEditing] = useState(false);
  const setEnabled = useSetStorageDefaultEnabled(row.id);
  const provider = providers.find((item) => item.typeKey === row.providerTypeKey);

  return (
    <div className="cn-row" role="listitem">
      <div className="cn-row-head">
        <div className="cn-meta">
          <div className="cn-name">
            {row.dataClassTypeKey}
            <span className="cn-status">{row.isEnabled ? "Offered" : "Switched off"}</span>
          </div>
          <div className="cn-sub">
            {provider?.displayName ?? row.providerTypeKey} · revision {row.revision} ·{" "}
            {row.adoptionPolicy === "Automatic" ? "taken on a team's first write" : "taken only when a team admin chooses it"}
            {row.hasCredential && row.credentialSafeHint != null && ` · ${row.credentialSafeHint}`}
          </div>
        </div>
        <button type="button" className="btn" onClick={() => setEditing((open) => !open)}>{editing ? "Close" : "Edit"}</button>
        <button
          type="button"
          className="btn"
          disabled={setEnabled.isPending}
          onClick={() => setEnabled.mutate({ expectedXmin: row.xmin, expectedRevision: row.revision, isEnabled: !row.isEnabled })}
        >
          {row.isEnabled ? "Switch off" : "Offer again"}
        </button>
      </div>

      {/* Switching off leaves every team already on it exactly where it is: their routes are their own rows. */}
      {!row.isEnabled && <div className="stg-hint">No team can take this one while it is off. Teams already on it are unaffected.</div>}
      {setEnabled.error != null && <div className="stg-hint" role="alert">{errorMessage(setEnabled.error)}</div>}

      {editing && <DefaultEditor providers={providers} editing={row} onDone={() => setEditing(false)} />}
    </div>
  );
}

interface EditorProps {
  providers: StorageProviderModuleSummary[];
  unauthored?: { typeKey: string; displayName: string }[];
  editing?: StorageDefaultSummary;
  onDone: () => void;
}

/**
 * Create and edit are one form because the server takes the same fields for both: an update replaces the
 * template wholesale rather than patching it, so a partial edit form would have to reconstruct the parts
 * it did not show.
 */
function DefaultEditor({ providers, unauthored = [], editing, onDone }: EditorProps) {
  const detail = useStorageDefault(editing?.id ?? null);
  const create = useCreateStorageDefault();
  const update = useUpdateStorageDefault(editing?.id ?? "");
  const [dataClass, setDataClass] = useState(unauthored[0]?.typeKey ?? "");
  const [providerKey, setProviderKey] = useState(editing?.providerTypeKey ?? "");
  const [config, setConfig] = useState<Record<string, unknown>>({});
  const [namespaceRoot, setNamespaceRoot] = useState("");
  const [policy, setPolicy] = useState<StorageDefaultAdoptionPolicy>(editing?.adoptionPolicy ?? "Explicit");
  const [secret, setSecret] = useState<Record<string, unknown>>({});
  const [loaded, setLoaded] = useState(false);

  if (editing && detail.data && !loaded) {
    // Prefilled once from the server's own copy, so an edit that changes one field cannot quietly rewrite
    // the others from an empty form.
    setLoaded(true);
    setProviderKey(detail.data.providerTypeKey);
    setConfig(detail.data.nonSecretConfig);
    setNamespaceRoot(detail.data.namespaceRoot);
    setPolicy(detail.data.adoptionPolicy);
  }

  const provider = providers.find((item) => item.typeKey === providerKey);
  const subdividable = providers.filter((item) => item.teamNamespaceProperty != null);
  const configSchema = useMemo(() => withoutNamespace(provider), [provider]);
  const pending = create.isPending || update.isPending;
  const error = create.error ?? update.error;

  const submit = () => {
    const body = {
      providerTypeKey: providerKey,
      nonSecretConfig: config,
      namespaceRoot,
      adoptionPolicy: policy,
      ...(Object.keys(secret).length > 0 ? { secret } : {}),
    };

    if (editing) {
      update.mutate({ ...body, expectedXmin: editing.xmin, expectedRevision: editing.revision }, { onSuccess: onDone });
      return;
    }

    create.mutate({ ...body, dataClassTypeKey: dataClass, isEnabled: true }, { onSuccess: onDone });
  };

  return (
    <div className="stg-hint" role="group" aria-label={editing ? `Edit ${editing.dataClassTypeKey}` : "New storage default"}>
      {!editing && (
        <div className="wf-form-row">
          <label className="wf-form-label" htmlFor="storage-default-data-class">Class of data</label>
          <select id="storage-default-data-class" className="wf-form-input" value={dataClass} onChange={(event) => setDataClass(event.target.value)}>
            {unauthored.map((item) => <option key={item.typeKey} value={item.typeKey}>{item.displayName}</option>)}
          </select>
        </div>
      )}

      <div className="wf-form-row">
        <label className="wf-form-label" htmlFor="storage-default-provider">Where it goes</label>
        <select id="storage-default-provider" className="wf-form-input" value={providerKey} onChange={(event) => { setProviderKey(event.target.value); setConfig({}); setSecret({}); }}>
          <option value="">Choose a provider…</option>
          {subdividable.map((item) => <option key={item.typeKey} value={item.typeKey}>{item.displayName}</option>)}
        </select>
        {providers.length > subdividable.length && (
          <span className="wf-form-help">Providers that cannot give each team a namespace of its own are not offered here.</span>
        )}
      </div>

      {provider && (
        <>
          <div className="wf-form">
            <SchemaForm schema={configSchema} value={config} onChange={setConfig} />
          </div>

          <div className="wf-form-row">
            <label className="wf-form-label" htmlFor="storage-default-namespace-root">Namespace root</label>
            <input id="storage-default-namespace-root" className="wf-form-input" value={namespaceRoot} onChange={(event) => setNamespaceRoot(event.target.value)} placeholder="codespace" />
            <span className="wf-form-help">
              A root, not a finished namespace. Each team gets its own segment appended under it, which is what keeps
              one team's data from landing on another's.
            </span>
          </div>

          {hasSecretInputs(provider) && (
            <div className="wf-form">
              <SchemaForm schema={provider.secretSchema} value={secret} onChange={setSecret} sensitive />
              {editing && <span className="wf-form-help">Leave blank to keep the secret already stored.</span>}
            </div>
          )}

          <div className="wf-form-row">
            <label className="wf-form-label" htmlFor="storage-default-policy">How teams take it</label>
            <select id="storage-default-policy" className="wf-form-input" value={policy} onChange={(event) => setPolicy(event.target.value as StorageDefaultAdoptionPolicy)}>
              <option value="Explicit">Only when a team admin chooses it</option>
              <option value="Automatic">On a team's first write</option>
            </select>
            <span className="wf-form-help">
              A class that keeps a durable home of its own may only be taken deliberately: moving it is permanent, and
              the server refuses to make that choice for anyone.
            </span>
          </div>
        </>
      )}

      {error != null && <div className="stg-hint" role="alert">{errorMessage(error)}</div>}

      <div style={{ display: "flex", gap: 8, marginTop: 8 }}>
        <button type="button" className="btn btn-primary" disabled={pending || !providerKey || namespaceRoot.trim() === ""} onClick={submit}>
          {editing ? "Save" : "Author this default"}
        </button>
        <button type="button" className="btn" onClick={onDone}>Cancel</button>
      </div>
    </div>
  );
}

/**
 * The provider's config schema WITHOUT the property that carries the namespace.
 *
 * <p>The server refuses a template that sets it — a template describes the whole deployment, and that
 * property names one team — so offering the field would be offering a rejection. The namespace root is
 * asked for separately, which is the shape the server actually accepts.</p>
 */
function withoutNamespace(provider: StorageProviderModuleSummary | undefined): Record<string, unknown> {
  if (!provider) return { type: "object", properties: {} };
  if (provider.teamNamespaceProperty == null) return provider.configSchema;

  const schema = provider.configSchema as { properties?: Record<string, unknown>; required?: string[] };
  const properties = { ...(schema.properties ?? {}) };
  delete properties[provider.teamNamespaceProperty];

  return {
    ...schema,
    properties,
    required: (schema.required ?? []).filter((name) => name !== provider.teamNamespaceProperty),
  };
}

function hasSecretInputs(provider: StorageProviderModuleSummary): boolean {
  const schema = provider.secretSchema as { properties?: Record<string, unknown> };
  return Object.keys(schema.properties ?? {}).length > 0;
}

function errorMessage(error: unknown): string {
  if (error instanceof ApiError) return error.message;
  return error instanceof Error ? error.message : "Something went wrong.";
}
