import { useState } from "react";

import { Ic } from "@/_imported/ai-code-space/icons";
import type { WorkflowVariable } from "@/api/workflows";
import { useRenameExternalTrigger, useRevealExternalTriggerKey, useRotateExternalTriggerKey } from "@/hooks/use-workflows";
import { deriveExternalTriggerSlug, isUsableExternalTriggerSlug } from "@/lib/external-trigger-slug";
import { externalTriggerUrl } from "@/lib/external-trigger-url";

import { StartNodeInputsEditor } from "./StartNodeInputsEditor";

interface IExternalTriggerInspectorProps {
  config: Record<string, unknown>;
  nodeId: string;
  currentSlug: string | null;
  onConfigChange: (next: Record<string, unknown>) => void;
}

const UNUSABLE_SLUG_GUIDANCE = "Cannot derive a usable slug. Use an English name with ASCII letters, digits or underscores.";

function asObject(value: unknown): Record<string, unknown> {
  return value !== null && typeof value === "object" && !Array.isArray(value) ? value as Record<string, unknown> : {};
}

export function ExternalTriggerInspector({ config, nodeId, currentSlug, onConfigChange }: IExternalTriggerInspectorProps) {
  const reveal = useRevealExternalTriggerKey();
  const rotate = useRotateExternalTriggerKey();
  const rename = useRenameExternalTrigger();
  const [confirming, setConfirming] = useState<"rotate" | "rename" | null>(null);
  const [key, setKey] = useState<string | null>(null);
  const [copyStatus, setCopyStatus] = useState<string | null>(null);
  const [copyError, setCopyError] = useState<string | null>(null);
  const [schemaWarning, setSchemaWarning] = useState<string | null>(null);
  const draft = typeof config.slug === "string" ? config.slug : "";
  const derived = deriveExternalTriggerSlug(draft);
  const usable = isUsableExternalTriggerSlug(derived);
  const schema = asObject(config.inputSchema);
  const properties = asObject(schema.properties);
  const required = Array.isArray(schema.required) ? schema.required : [];
  const fields: WorkflowVariable[] = Object.entries(properties).map(([name, value]) => {
    const property = asObject(value);
    return { name, schema: value, label: typeof property.title === "string" ? property.title : undefined, default: property.default, required: required.includes(name) };
  });
  const pending = reveal.isPending || rotate.isPending || rename.isPending;
  const error = reveal.error ?? rotate.error ?? rename.error;
  const endpoint = currentSlug ? `/api/ext/${encodeURIComponent(currentSlug)}/run` : null;
  const fullUrl = endpoint ? externalTriggerUrl(endpoint) : "";
  const curl = key ? `curl -X POST '${fullUrl}' -H 'Authorization: Bearer ${key}' -H 'Content-Type: application/json' -d '{}'` : null;

  const changeFields = (next: WorkflowVariable[]) => {
    const unappliedMetadata: string[] = [];
    const nextProperties = Object.fromEntries(next.map((field) => {
      const hasMetadata = field.label != null || field.default !== undefined;
      if (field.schema === null || typeof field.schema !== "object" || Array.isArray(field.schema)) {
        if (hasMetadata) unappliedMetadata.push(field.name);
        return [field.name, field.schema];
      }
      if (!hasMetadata && !("title" in field.schema) && !("default" in field.schema)) return [field.name, field.schema];
      const property = { ...asObject(field.schema) };
      if (field.label != null) property.title = field.label;
      else delete property.title;
      if (field.default !== undefined) property.default = field.default;
      else delete property.default;
      return [field.name, property];
    }));
    setSchemaWarning(unappliedMetadata.length > 0
      ? `${unappliedMetadata.join(", ")}: label/default could not be applied to a non-object schema. The original constraint was preserved.`
      : null);
    onConfigChange({ ...config, inputSchema: { ...schema, type: "object", properties: nextProperties, required: next.filter((field) => field.required).map((field) => field.name) } });
  };

  const copy = async (text: string) => {
    setCopyStatus(null);
    setCopyError(null);
    try { await navigator.clipboard.writeText(text); setCopyStatus("Copied"); }
    catch { setCopyError("Could not copy to clipboard."); }
  };

  const requestKey = () => {
    if (!currentSlug || pending) return;
    reveal.reset();
    rotate.reset();
    rename.reset();
    reveal.mutate(currentSlug, { onSuccess: (result) => setKey(result.key) });
  };

  const confirm = () => {
    if (!currentSlug || pending) return;
    reveal.reset();
    rotate.reset();
    rename.reset();
    if (confirming === "rotate") {
      setKey(null);
      rotate.mutate(currentSlug, { onSuccess: (result) => { setKey(result.key); setConfirming(null); } });
    } else if (confirming === "rename" && usable && derived !== currentSlug) {
      rename.mutate({ slug: currentSlug, desiredSlug: derived }, { onSuccess: () => { setKey(null); setConfirming(null); } });
    }
  };

  return (
    <>
      <section className="wf-inspector-section">
        <label className="wf-form-row">
          <span className="wf-form-label">Draft slug</span>
          <input className="wf-form-input" value={draft} aria-invalid={!usable} aria-describedby={`${nodeId}-slug-status`} onChange={(event) => onConfigChange({ ...config, slug: event.target.value })} />
        </label>
        <p id={`${nodeId}-slug-status`} className="wf-retry-hint" role={usable ? undefined : "alert"}>
          {usable ? `Derived slug: ${derived}` : UNUSABLE_SLUG_GUIDANCE}
        </p>
      </section>

      <StartNodeInputsEditor inputs={fields} onChange={changeFields} />
      {schemaWarning && <p role="alert" className="wf-retry-hint">{schemaWarning}</p>}

      <section className="wf-inspector-section">
        <h3 className="wf-inspector-section-h">Endpoint</h3>
        {!endpoint ? <p role="status" className="wf-retry-hint">Save the workflow to create this endpoint.</p> : (
          <>
            <div className="wf-form-row"><span className="wf-form-label">Live slug</span><code style={{ overflowWrap: "anywhere" }}>{currentSlug}</code></div>
            <code style={{ display: "block", overflowWrap: "anywhere" }}>{endpoint}</code>
            <button type="button" className="btn btn-ghost" title="Copy endpoint" aria-label="Copy endpoint" onClick={() => void copy(fullUrl)}><Ic.Copy size={13} /></button>
            {draft !== currentSlug && <p className="wf-retry-hint">The draft differs from the live slug. Saving the workflow does not rename this endpoint.</p>}
            <div style={{ display: "flex", flexWrap: "wrap", gap: 8 }}>
              <button type="button" className="btn" disabled={pending} onClick={requestKey}>Show key</button>
              <button type="button" className="btn" disabled={pending} onClick={() => setConfirming("rotate")}>Regenerate</button>
              <button type="button" className="btn" disabled={pending || !usable || derived === currentSlug} onClick={() => setConfirming("rename")}>Rename</button>
            </div>
            {confirming && (
              <div role="group" aria-label={`Confirm ${confirming === "rotate" ? "regenerate" : "rename"}`} className="wf-retry-hint">
                <p>{confirming === "rotate"
                  ? "The old key stops working immediately. Existing callers must use the new key."
                  : usable ? `The existing URL will stop working. Rename the live endpoint to ${derived}?` : UNUSABLE_SLUG_GUIDANCE}</p>
                <div style={{ display: "flex", gap: 8 }}>
                  <button type="button" className="btn btn-primary" disabled={pending || (confirming === "rename" && (!usable || derived === currentSlug))} onClick={confirm}>Confirm {confirming === "rotate" ? "regenerate" : "rename"}</button>
                  <button type="button" className="btn" disabled={pending} onClick={() => setConfirming(null)}>Cancel</button>
                </div>
              </div>
            )}
            {key && (
              <div>
                <label className="wf-form-row"><span className="wf-form-label">Key</span><input className="wf-form-input" readOnly value={key} /></label>
                <button type="button" className="btn btn-ghost" title="Copy key" aria-label="Copy key" onClick={() => void copy(key)}><Ic.Copy size={13} /></button>
              </div>
            )}
            {curl && (
              <div>
                <pre style={{ whiteSpace: "pre-wrap", overflowWrap: "anywhere" }}>{curl}</pre>
                <button type="button" className="btn btn-ghost" title="Copy curl" aria-label="Copy curl" onClick={() => void copy(curl)}><Ic.Copy size={13} /></button>
              </div>
            )}
          </>
        )}
        {pending && <p role="status">Working...</p>}
        {error && <p role="alert">{error.message}</p>}
        {copyStatus && <p role="status">{copyStatus}</p>}
        {copyError && <p role="alert">{copyError}</p>}
      </section>
    </>
  );
}
