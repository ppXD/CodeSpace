import { useEffect, useState } from "react";
import { createPortal } from "react-dom";

import { Ic } from "@/_imported/ai-code-space/icons";
import type { PackSummary, PackSyncResult } from "@/api/packs";
import { ApiError } from "@/api/request";
import { defaultSelectedPaths, toRows } from "@/components/agents/packPreview";
import { PreviewGroup } from "@/components/agents/packPreviewRows";
import { useImportPack } from "@/hooks/use-packs";

import { newArtifactCount, syncSummaryLabel } from "./syncView";

/**
 * Sync result modal — shown after re-pulling a pack. The header reports what changed (up to date / updated /
 * new), and any discovered-but-not-imported artifacts are listed for the operator to select and add (committed
 * via the same import path as a first import, so the pack's saved URL + ref drive the commit). A pure-refresh
 * sync with nothing new is a one-line "everything's in sync" with a Done button.
 */
export function SyncResultModal({ pack, result, onClose }: { pack: PackSummary; result: PackSyncResult; onClose: () => void }) {
  const rows = toRows(result.newArtifacts);
  const agentRows = rows.filter((r) => r.kind === "agent");
  const skillRows = rows.filter((r) => r.kind === "skill");
  const hasNew = newArtifactCount(result) > 0;

  const [selected, setSelected] = useState<Set<string>>(() => new Set(defaultSelectedPaths(result.newArtifacts)));
  const importPack = useImportPack();

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => { if (e.key === "Escape") onClose(); };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [onClose]);

  function toggle(sourcePath: string) {
    setSelected((s) => {
      const next = new Set(s);
      if (next.has(sourcePath)) next.delete(sourcePath); else next.add(sourcePath);
      return next;
    });
  }

  function add() {
    importPack.mutate({ url: pack.url ?? "", reference: result.reference ?? "", sourcePaths: [...selected] }, { onSuccess: onClose });
  }

  const importErr = importPack.error instanceof ApiError ? importPack.error.message : importPack.error ? "Couldn't add the selected artifacts." : null;

  return createPortal(
    <>
      <div className="mdl-mask" onClick={onClose} />
      <div className="mdl" role="dialog" aria-modal="true" style={{ width: 560, maxWidth: "94vw" }}>
        <div className="mdl-head">
          <div className="mdl-title-wrap">
            <div className="mdl-title">Synced {pack.name}</div>
            <div className="mdl-sub">{syncSummaryLabel(result)}{result.reference ? ` · ${result.reference}` : ""}</div>
          </div>
          <button type="button" className="mdl-x" onClick={onClose} title="Close" aria-label="Close"><Ic.X size={14} /></button>
        </div>

        <div className="mdl-body">
          {hasNew ? (
            <>
              <div className="wf-form-help" style={{ marginBottom: 4 }}>New artifacts discovered in the source — select the ones to add. A sync never imports automatically.</div>
              {agentRows.length > 0 && <PreviewGroup title="Agents" rows={agentRows} selected={selected} onToggle={toggle} />}
              {skillRows.length > 0 && <PreviewGroup title="Skills" rows={skillRows} selected={selected} onToggle={toggle} />}
            </>
          ) : (
            <div className="wf-form-empty" style={{ marginTop: 4 }}>Everything's in sync — no new artifacts to add.</div>
          )}

          {importErr && (
            <div className="cn-banner cn-banner-err" style={{ marginTop: 14 }}>
              <div className="cn-banner-h">Couldn't add the selected artifacts</div>
              <div className="cn-banner-p">{importErr}</div>
            </div>
          )}
        </div>

        <div className="mdl-foot">
          {hasNew ? (
            <>
              <div className="mdl-foot-info">{selected.size} selected · {rows.filter((r) => r.importable).length} importable</div>
              <div style={{ display: "flex", gap: 8 }}>
                <button type="button" className="btn" onClick={onClose}>Cancel</button>
                <button type="button" className="btn btn-primary" onClick={add} disabled={selected.size === 0 || importPack.isPending}>
                  <Ic.Plus size={14} /> {importPack.isPending ? "Adding…" : `Add ${selected.size}`}
                </button>
              </div>
            </>
          ) : (
            <div style={{ display: "flex", gap: 8, marginLeft: "auto" }}>
              <button type="button" className="btn btn-primary" onClick={onClose}>Done</button>
            </div>
          )}
        </div>
      </div>
    </>,
    document.body,
  );
}
