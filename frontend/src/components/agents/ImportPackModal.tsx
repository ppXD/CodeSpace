import { useEffect, useState } from "react";
import { createPortal } from "react-dom";

import { Ic } from "@/_imported/ai-code-space/icons";
import { ApiError } from "@/api/request";
import { useImportPack, usePreviewPack } from "@/hooks/use-packs";

import { defaultSelectedPaths, toRows } from "./packPreview";
import { PreviewGroup } from "./packPreviewRows";

/**
 * Import-from-URL modal — paste a GitHub/GitLab URL, clone + discover its agents AND skills (host-allowlist
 * guarded, persists nothing), then select the importable ones and commit. Each item shows its derived @handle and
 * a flag: new (importable), already-exists (a team handle collides — not selectable), or can't-import (nameless /
 * unparseable). A successful import closes the modal; the agents library refetches and the new personas appear.
 */
export function ImportPackModal({ onClose }: { onClose: () => void }) {
  const [url, setUrl] = useState("");
  const [reference, setReference] = useState("");
  const [selected, setSelected] = useState<Set<string>>(new Set());

  const preview = usePreviewPack();
  const importPack = useImportPack();

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => { if (e.key === "Escape") onClose(); };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [onClose]);

  const data = preview.data;
  const rows = data ? toRows(data) : [];
  const agentRows = rows.filter((r) => r.kind === "agent");
  const skillRows = rows.filter((r) => r.kind === "skill");

  function fetchPreview() {
    if (!url.trim()) return;
    preview.mutate({ url: url.trim(), reference }, { onSuccess: (p) => setSelected(new Set(defaultSelectedPaths(p))) });
  }

  function toggle(sourcePath: string) {
    setSelected((s) => {
      const next = new Set(s);
      if (next.has(sourcePath)) next.delete(sourcePath); else next.add(sourcePath);
      return next;
    });
  }

  function commit() {
    importPack.mutate({ url: url.trim(), reference, sourcePaths: [...selected] }, { onSuccess: onClose });
  }

  const previewErr = preview.error instanceof ApiError ? preview.error.message : preview.error ? "Couldn't fetch the pack." : null;
  const importErr = importPack.error instanceof ApiError ? importPack.error.message : importPack.error ? "Couldn't import." : null;

  return createPortal(
    <>
      <div className="mdl-mask" onClick={onClose} />
      <div className="mdl" role="dialog" aria-modal="true" style={{ width: 620, maxWidth: "94vw" }}>
        <div className="mdl-head">
          <div className="mdl-title-wrap">
            <div className="mdl-title">Import from a pack</div>
            <div className="mdl-sub">Clone a GitHub / GitLab URL and pick the agents + skills to add.</div>
          </div>
          <button type="button" className="mdl-x" onClick={onClose} title="Close" aria-label="Close"><Ic.X size={14} /></button>
        </div>

        <div className="mdl-body">
          <div className="wf-form">
            <div className="wf-form-row">
              <label className="wf-form-label" htmlFor="imp-url">Pack URL</label>
              <div style={{ display: "flex", gap: 8, alignItems: "stretch" }}>
                <input id="imp-url" className="wf-form-input" style={{ flex: 2 }} value={url} onChange={(e) => setUrl(e.target.value)} placeholder="github.com/obra/superpowers" onKeyDown={(e) => { if (e.key === "Enter") fetchPreview(); }} autoFocus />
                <input className="wf-form-input" style={{ flex: 1 }} value={reference} onChange={(e) => setReference(e.target.value)} placeholder="branch / tag" onKeyDown={(e) => { if (e.key === "Enter") fetchPreview(); }} />
                <button type="button" className="btn btn-primary" onClick={fetchPreview} disabled={!url.trim() || preview.isPending}>
                  <Ic.Search size={14} /> {preview.isPending ? "Fetching…" : "Fetch"}
                </button>
              </div>
              <span className="wf-form-help">Only github.com and gitlab.com are allowed (operator-configurable via CODESPACE_PACK_ALLOWED_HOSTS).</span>
            </div>
          </div>

          {previewErr && (
            <div className="cn-banner cn-banner-err" style={{ marginTop: 14 }}>
              <div className="cn-banner-h">Couldn't fetch the pack</div>
              <div className="cn-banner-p">{previewErr}</div>
            </div>
          )}

          {data && rows.length === 0 && (
            <div className="wf-form-empty" style={{ marginTop: 16 }}>No agents or skills found in this pack{data.reference ? ` at ${data.reference}` : ""}.</div>
          )}

          {agentRows.length > 0 && <PreviewGroup title="Agents" rows={agentRows} selected={selected} onToggle={toggle} />}
          {skillRows.length > 0 && <PreviewGroup title="Skills" rows={skillRows} selected={selected} onToggle={toggle} />}

          {importErr && (
            <div className="cn-banner cn-banner-err" style={{ marginTop: 14 }}>
              <div className="cn-banner-h">Couldn't import</div>
              <div className="cn-banner-p">{importErr}</div>
            </div>
          )}
        </div>

        {data && rows.length > 0 && (
          <div className="mdl-foot">
            <div className="mdl-foot-info">{selected.size} selected · {rows.filter((r) => r.importable).length} importable</div>
            <div style={{ display: "flex", gap: 8 }}>
              <button type="button" className="btn" onClick={onClose}>Cancel</button>
              <button type="button" className="btn btn-primary" onClick={commit} disabled={selected.size === 0 || importPack.isPending}>
                <Ic.Plus size={14} /> {importPack.isPending ? "Importing…" : `Import ${selected.size}`}
              </button>
            </div>
          </div>
        )}
      </div>
    </>,
    document.body,
  );
}
