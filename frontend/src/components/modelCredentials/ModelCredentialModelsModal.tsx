import { useEffect, useState } from "react";
import { createPortal } from "react-dom";

import { Ic } from "@/_imported/ai-code-space/icons";
import type { ModelCredentialSummary } from "@/api/modelCredentials";
import { ApiError } from "@/api/request";
import { useCredentialedModelList, useRefreshCredentialedModels, useSaveCredentialedModels, useSetDefaultCredentialedModel } from "@/hooks/use-model-credentials";
import { providerForm } from "@/lib/providerForms";

import { ModelRowsEditor, type ModelRow } from "./ModelRowsEditor";

interface ModelCredentialModelsModalProps {
  credential: ModelCredentialSummary;
  onClose: () => void;
}

/**
 * Manage the models on one credential with the same multi-row editor the Add-credential form uses: edit
 * the list by hand (add / remove / rename rows) or refresh it from the provider endpoint, then Save —
 * which reconciles the edits against the credential's current models. Secret-free; warm `.mdl` shell.
 */
export function ModelCredentialModelsModal({ credential, onClose }: ModelCredentialModelsModalProps) {
  const list = useCredentialedModelList(credential.id);
  const refresh = useRefreshCredentialedModels(credential.id);
  const save = useSaveCredentialedModels(credential.id);
  const setDefault = useSetDefaultCredentialedModel(credential.id);

  const [rows, setRows] = useState<ModelRow[] | null>(null);
  const [error, setError] = useState<string | null>(null);

  // Sync editable rows from the server list whenever its reference changes — first load, and the re-fetch
  // after a save/refresh. Edits in between survive because the query data reference is stable until refetch.
  const [syncedData, setSyncedData] = useState<unknown>(undefined);
  if (list.data !== syncedData) {
    setSyncedData(list.data);
    setRows(list.data ? list.data.map(m => ({ id: m.id, modelId: m.modelId, displayName: m.displayName ?? "", isDefault: m.isDefault })) : null);
  }

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => { if (e.key === "Escape") onClose(); };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [onClose]);

  const providerLabel = providerForm(credential.provider)?.label ?? credential.provider;
  const onError = (e: unknown) => setError(e instanceof ApiError ? e.message : "Something went wrong.");
  const count = (rows ?? []).filter(r => r.modelId.trim()).length;

  const doRefresh = () => { setError(null); refresh.mutate(undefined, { onError }); };
  const doSave = () => { setError(null); save.mutate({ original: list.data ?? [], rows: rows ?? [] }, { onSuccess: onClose, onError }); };
  const doSetDefault = (rowId: string) => {
    setError(null);
    setRows(rs => rs?.map(r => ({ ...r, isDefault: r.id === rowId })) ?? rs);   // optimistic: flip the star without a refetch that would wipe unsaved edits
    setDefault.mutate(rowId, { onError });
  };

  return createPortal(
    <>
      <div className="mdl-mask" onClick={onClose} />
      <div className="mdl mc-models" role="dialog" aria-modal="true" aria-label={`Models for ${credential.displayName}`}>
        <div className="mdl-head">
          <div className="mdl-title-wrap">
            <div className="mdl-title">{credential.displayName} · Models</div>
            <div className="mdl-sub">The models this credential offers to pickers. Refresh to pull them from {providerLabel}, or edit the list by hand.</div>
          </div>
          <button className="mdl-x" onClick={onClose} title="Close"><Ic.X size={14} /></button>
        </div>

        <div className="mdl-body">
          <div className="mc-models-bar">
            <span className="mc-models-count">{count} {count === 1 ? "model" : "models"}</span>
            <button className="btn btn-ghost mc-models-refresh" onClick={doRefresh} disabled={refresh.isPending}>
              <Ic.Sparkles size={13} /> {refresh.isPending ? "Refreshing…" : "Refresh from provider"}
            </button>
          </div>

          {list.isLoading && rows === null && <div className="mc-models-empty">Loading…</div>}
          {list.error instanceof ApiError && <div className="mc-models-empty">Couldn't load models — {list.error.message}</div>}
          {rows !== null && <ModelRowsEditor rows={rows} onChange={setRows} onSetDefault={doSetDefault} />}

          {error && <div className="mc-models-err">{error}</div>}
        </div>

        <div className="mdl-foot">
          <button className="btn btn-ghost" onClick={onClose}>Cancel</button>
          <button className="btn btn-primary" onClick={doSave} disabled={save.isPending || rows === null}>
            {save.isPending ? "Saving…" : "Save"}
          </button>
        </div>
      </div>
    </>,
    document.body,
  );
}
