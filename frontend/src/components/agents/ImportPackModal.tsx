import { useEffect, useState } from "react";
import { createPortal } from "react-dom";

import { Ic } from "@/_imported/ai-code-space/icons";
import type { PackPreview } from "@/api/packs";
import { ApiError } from "@/api/request";
import { useImportPack, usePreviewPack } from "@/hooks/use-packs";

import { defaultSelectedPaths, flagFor, type ImportFlag } from "./packPreview";

/** One discovered item, flattened across agents + skills for uniform rendering. */
interface Row {
  sourcePath: string;
  name: string;
  derivedSlug: string;
  description: string | null;
  diagnostics: string[];
  slugConflict: boolean;
  importable: boolean;
  kind: "agent" | "skill";
}

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

function PreviewGroup({ title, rows, selected, onToggle }: { title: string; rows: Row[]; selected: Set<string>; onToggle: (p: string) => void }) {
  return (
    <div style={{ marginTop: 16 }}>
      <div className="wf-form-label" style={{ textTransform: "uppercase", letterSpacing: ".06em", fontSize: 10.5, color: "var(--muted)", marginBottom: 4 }}>{title} · {rows.length}</div>
      {rows.map((r) => <PreviewRow key={r.sourcePath} row={r} checked={selected.has(r.sourcePath)} onToggle={() => onToggle(r.sourcePath)} />)}
    </div>
  );
}

function PreviewRow({ row, checked, onToggle }: { row: Row; checked: boolean; onToggle: () => void }) {
  const flag = flagFor(row);
  return (
    <label style={{ display: "flex", gap: 10, padding: "9px 0", borderBottom: "1px solid var(--line)", alignItems: "flex-start", cursor: row.importable ? "pointer" : "default", opacity: row.importable ? 1 : 0.65 }}>
      <input type="checkbox" checked={checked} disabled={!row.importable} onChange={onToggle} style={{ marginTop: 2 }} />
      <div style={{ minWidth: 0, flex: 1 }}>
        <div style={{ display: "flex", alignItems: "center", gap: 8, flexWrap: "wrap" }}>
          <span style={{ fontWeight: 600, fontSize: 13, color: "var(--ink)" }}>{row.name || row.sourcePath.split("/").pop()}</span>
          {row.derivedSlug && <span style={{ fontSize: 11, color: "var(--muted-2)" }}>@{row.derivedSlug}</span>}
          <FlagTag flag={flag} />
        </div>
        {flag === "new" && row.description && <div style={{ fontSize: 11, color: "var(--muted)", marginTop: 2, overflow: "hidden", textOverflow: "ellipsis", whiteSpace: "nowrap" }}>{row.description}</div>}
        {flag === "exists" && <div style={{ fontSize: 11, color: "var(--muted)", marginTop: 2 }}>A {row.kind} with this handle already exists in your team.</div>}
        {flag === "blocked" && <div style={{ fontSize: 10.5, color: "var(--warn)", marginTop: 3 }}>{row.diagnostics[0] ?? "Missing a name — nothing to import."}</div>}
      </div>
    </label>
  );
}

function FlagTag({ flag }: { flag: ImportFlag }) {
  const style: React.CSSProperties = { fontSize: 10, letterSpacing: ".03em", padding: "1px 6px", borderRadius: 4, flexShrink: 0 };
  if (flag === "new") return <span style={{ ...style, background: "#EAF4EE", color: "#2D6A48" }}>new</span>;
  if (flag === "exists") return <span style={{ ...style, background: "var(--panel-2)", color: "var(--muted)", border: "1px solid var(--line)" }}>already exists</span>;
  return <span style={{ ...style, background: "#FCF1E2", color: "#8A5A1A" }}>can't import</span>;
}

function toRows(p: PackPreview): Row[] {
  return [
    ...p.agents.map((a): Row => ({ sourcePath: a.sourcePath, name: a.name, derivedSlug: a.derivedSlug, description: a.description, diagnostics: a.diagnostics, slugConflict: a.slugConflict, importable: a.importable, kind: "agent" })),
    ...p.skills.map((s): Row => ({ sourcePath: s.sourcePath, name: s.name, derivedSlug: s.derivedSlug, description: s.description, diagnostics: s.diagnostics, slugConflict: s.slugConflict, importable: s.importable, kind: "skill" })),
  ];
}
