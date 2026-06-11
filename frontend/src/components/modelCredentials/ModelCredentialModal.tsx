import { useEffect, useState } from "react";
import { createPortal } from "react-dom";

import { Ic } from "@/_imported/ai-code-space/icons";
import type { ModelCredentialSummary } from "@/api/modelCredentials";
import { ApiError } from "@/api/request";
import { useAddModelCredential, useUpdateModelCredential } from "@/hooks/use-model-credentials";
import { PROVIDER_FORMS, providerForm } from "@/lib/providerForms";

interface ModelCredentialModalProps {
  /** Present = edit (provider locked, secret write-only); absent = add (provider is chosen in the form). */
  editing?: ModelCredentialSummary;
  onClose: () => void;
}

/**
 * Add / edit a model credential. Mirrors the warm-theme `.mdl` + `wf-form` shell (see IdentityLinkModal),
 * and renders its fields from the per-provider form variant (lib/providerForms): a masked secret (write-only
 * on edit — blank keeps the current key), a base URL (required for gateway/keyless providers, optional
 * otherwise), and a provider picker when adding.
 */
export function ModelCredentialModal({ editing, onClose }: ModelCredentialModalProps) {
  const isEdit = editing != null;

  const [provider, setProvider] = useState(editing?.provider ?? PROVIDER_FORMS[0].provider);
  const [displayName, setDisplayName] = useState(editing?.displayName ?? "");
  const [apiKey, setApiKey] = useState("");                      // never prefilled — the key is write-only
  const [baseUrl, setBaseUrl] = useState(editing?.baseUrl ?? "");
  const [error, setError] = useState<string | null>(null);

  const add = useAddModelCredential();
  const update = useUpdateModelCredential();
  const pending = add.isPending || update.isPending;

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => { if (e.key === "Escape") onClose(); };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [onClose]);

  const form = providerForm(provider);
  const keyless = form?.keyless ?? false;
  const keyField = form?.fields.find(f => f.key === "apiKey");
  const baseUrlField = form?.fields.find(f => f.key === "baseUrl");
  const baseUrlRequired = baseUrlField?.required ?? false;
  // Key is mandatory only when ADDING a key-based provider; on edit a blank key keeps the existing one.
  const keyRequired = !isEdit && !keyless && (keyField?.required ?? false);

  const requiredOk =
    displayName.trim() !== "" &&
    (!keyRequired || apiKey.trim() !== "") &&
    (!baseUrlRequired || baseUrl.trim() !== "");

  const submit = () => {
    if (!requiredOk || pending) return;
    setError(null);

    const onError = (e: unknown) => setError(e instanceof ApiError ? e.message : "Could not save the credential.");
    const trimmedKey = apiKey.trim() === "" ? null : apiKey.trim();
    const trimmedBaseUrl = baseUrl.trim() === "" ? null : baseUrl.trim();

    if (isEdit) {
      update.mutate(
        { id: editing.id, input: { displayName: displayName.trim(), apiKey: trimmedKey, baseUrl: trimmedBaseUrl } },
        { onSuccess: onClose, onError },
      );
    } else {
      add.mutate(
        { provider, displayName: displayName.trim(), apiKey: keyless ? null : trimmedKey, baseUrl: trimmedBaseUrl },
        { onSuccess: onClose, onError },
      );
    }
  };

  return createPortal(
    <>
      <div className="mdl-mask" onClick={onClose} />
      <div className="mdl" role="dialog" aria-modal="true" aria-label={isEdit ? "Edit model credential" : "Add model credential"}>
        <div className="mdl-head">
          <div className="mdl-title-wrap">
            <div className="mdl-title">{isEdit ? `Edit ${editing.displayName}` : "Add model credential"}</div>
            <div className="mdl-sub">Stored encrypted. The key is injected into the agent sandbox at run time and never shown again.</div>
          </div>
          <button className="mdl-x" onClick={onClose} title="Close"><Ic.X size={14} /></button>
        </div>

        <div className="mdl-body">
          <div className="wf-form">
            {!isEdit && (
              <label className="wf-form-row">
                <span className="wf-form-label">Provider</span>
                <select className="wf-form-input" value={provider} onChange={(e) => setProvider(e.target.value)}>
                  {PROVIDER_FORMS.map(f => <option key={f.provider} value={f.provider}>{f.label}</option>)}
                </select>
              </label>
            )}

            <label className="wf-form-row">
              <span className="wf-form-label">Display name</span>
              <input className="wf-form-input" type="text" value={displayName} onChange={(e) => setDisplayName(e.target.value)} placeholder={`${form?.label ?? provider} key`} autoFocus={!isEdit} />
            </label>

            {!keyless && keyField && (
              <label className="wf-form-row">
                <span className="wf-form-label">{keyField.label}</span>
                <input
                  className="wf-form-input"
                  type="password"
                  value={apiKey}
                  onChange={(e) => setApiKey(e.target.value)}
                  onKeyDown={(e) => { if (e.key === "Enter") submit(); }}
                  placeholder={isEdit ? `${editing.keyHint ?? "····"} — leave blank to keep` : keyField.placeholder}
                  autoFocus={isEdit}
                />
                <span className="wf-form-help">{isEdit ? "Leave blank to keep the current key, or paste a new one to rotate." : "Stored encrypted; we never show it again."}</span>
              </label>
            )}

            {baseUrlField && (
              <label className="wf-form-row">
                <span className="wf-form-label">{baseUrlField.label}{baseUrlRequired ? "" : " · optional"}</span>
                <input className="wf-form-input" type="text" value={baseUrl} onChange={(e) => setBaseUrl(e.target.value)} placeholder={baseUrlField.placeholder} />
                {!baseUrlRequired && <span className="wf-form-help">Override the default endpoint — for a self-hosted or compatible gateway.</span>}
              </label>
            )}

            {error && <div className="wf-form-row"><span className="wf-form-help wf-form-help-err">{error}</span></div>}
          </div>
        </div>

        <div className="mdl-foot">
          <button className="btn btn-ghost" onClick={onClose}>Cancel</button>
          <button className="btn btn-primary" onClick={submit} disabled={!requiredOk || pending}>
            {pending ? "Saving…" : isEdit ? "Save" : "Add credential"}
          </button>
        </div>
      </div>
    </>,
    document.body,
  );
}
