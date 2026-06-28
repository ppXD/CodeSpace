import { useEffect, useState } from "react";
import { createPortal } from "react-dom";

import { Ic } from "@/_imported/ai-code-space/icons";
import { ApiError } from "@/api/request";
import { useImportPack, usePreviewPack } from "@/hooks/use-packs";

import { installSummary, repoLabel } from "./packInstall";
import { defaultSelectedPaths, filterRows, toRows } from "./packPreview";
import { PreviewRow } from "./packPreviewRows";

/**
 * Import a capability pack — paste a GitHub/GitLab URL, clone + discover its agents AND skills (host-allowlist
 * guarded, persists nothing), then install the chosen ones. Once fetched it reads like an install dialog: a left
 * rail summarises the source (repo / branch / discovered counts / conflicts) and the right pane lists the items
 * to select. Each item shows its derived @handle and a flag: new (importable), already-installed (a team handle
 * collides — not selectable), or can't-import (nameless / unparseable). A successful install closes the modal;
 * the agents library + packs rail refetch and the new artifacts appear.
 */
export function ImportPackModal({ onClose }: { onClose: () => void }) {
  const [url, setUrl] = useState("");
  const [reference, setReference] = useState("");
  const [selected, setSelected] = useState<Set<string>>(new Set());
  // The url/ref actually fetched — snapshotted on a successful preview so the rail label and the commit always
  // reflect the previewed pack, even if the operator edits the URL field afterwards without re-fetching.
  const [fetched, setFetched] = useState<{ url: string; reference: string } | null>(null);
  // Which kind the item list shows (a pack can carry hundreds; tabs keep agents + skills separate) + a search.
  const [pickedTab, setPickedTab] = useState<"agents" | "skills" | null>(null);
  const [query, setQuery] = useState("");

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

  const selectedAgents = agentRows.filter((r) => selected.has(r.sourcePath)).length;
  const selectedSkills = skillRows.filter((r) => selected.has(r.sourcePath)).length;
  const conflicts = rows.filter((r) => r.slugConflict).length;

  // Derived active tab (no effect): an explicit pick wins, else the first kind that has items.
  const activeTab = pickedTab ?? (agentRows.length > 0 ? "agents" : "skills");
  const activeRows = activeTab === "agents" ? agentRows : skillRows;
  const visible = filterRows(activeRows, query);
  const visibleSelected = visible.filter((r) => selected.has(r.sourcePath)).length;

  function fetchPreview() {
    if (!url.trim()) return;
    preview.mutate({ url: url.trim(), reference }, { onSuccess: (p) => { setSelected(new Set(defaultSelectedPaths(p))); setFetched({ url: url.trim(), reference }); setPickedTab(null); setQuery(""); } });
  }

  function toggle(sourcePath: string) {
    setSelected((s) => {
      const next = new Set(s);
      if (next.has(sourcePath)) next.delete(sourcePath); else next.add(sourcePath);
      return next;
    });
  }

  // Select-all / clear operate on the currently VISIBLE rows (the active tab, filtered by the search) — only
  // importable ones can be added; conflicts / unparseable rows are never selectable.
  function selectAllVisible() {
    setSelected((s) => { const next = new Set(s); visible.filter((r) => r.importable).forEach((r) => next.add(r.sourcePath)); return next; });
  }
  function clearVisible() {
    setSelected((s) => { const next = new Set(s); visible.forEach((r) => next.delete(r.sourcePath)); return next; });
  }

  function commit() {
    if (!fetched) return;
    importPack.mutate({ url: fetched.url, reference: fetched.reference, sourcePaths: [...selected] }, { onSuccess: onClose });
  }

  const previewErr = preview.error instanceof ApiError ? preview.error.message : preview.error ? "Couldn't fetch the pack." : null;
  const importErr = importPack.error instanceof ApiError ? importPack.error.message : importPack.error ? "Couldn't import." : null;

  return createPortal(
    <>
      <div className="mdl-mask" onClick={onClose} />
      <div className="mdl" role="dialog" aria-modal="true" aria-label="Import a capability pack" style={{ width: 640, maxWidth: "94vw" }}>
        <div className="mdl-head">
          <div className="mdl-title-wrap">
            <div className="mdl-title">Install a capability pack</div>
            <div className="mdl-sub">Clone a GitHub / GitLab URL and install the agents + skills it carries.</div>
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

          {data && fetched && rows.length > 0 && (
            <div className="imp-cols">
              <div className="imp-rail">
                <div className="imp-rk">Repository</div>
                <div className="imp-rv"><Ic.Box size={13} /> <span className="imp-rv-t" title={repoLabel(fetched.url)}>{repoLabel(fetched.url)}</span></div>
                <div className="imp-rk">Branch / tag</div>
                <div className="imp-rv"><Ic.Branch size={13} /> <span className="imp-rv-t" title={fetched.reference || data.reference || "default branch"}>{fetched.reference || data.reference || "default branch"}</span></div>
                <div className="imp-rk">Discovered</div>
                <div className="imp-rv"><Ic.Bot size={13} /> {agentRows.length} {agentRows.length === 1 ? "agent" : "agents"}</div>
                <div className="imp-rv"><Ic.Puzzle size={13} /> {skillRows.length} {skillRows.length === 1 ? "skill" : "skills"}</div>
                <div className="imp-rk">Conflicts</div>
                {conflicts > 0
                  ? <div className="imp-rv imp-rv-warn"><Ic.Triangle size={13} /> {conflicts} already installed</div>
                  : <div className="imp-rv imp-rv-muted">none</div>}
              </div>
              <div className="imp-list">
                <div className="imp-toolbar">
                  <div className="imp-tabs" role="tablist" aria-label="Artifact kind">
                    <button type="button" role="tab" aria-selected={activeTab === "agents"} className="imp-tab" data-on={activeTab === "agents"} onClick={() => setPickedTab("agents")}>Agents <span className="imp-tab-c">{agentRows.length}</span></button>
                    <button type="button" role="tab" aria-selected={activeTab === "skills"} className="imp-tab" data-on={activeTab === "skills"} onClick={() => setPickedTab("skills")}>Skills <span className="imp-tab-c">{skillRows.length}</span></button>
                  </div>
                  <div className="imp-search">
                    <Ic.Search size={13} />
                    <input value={query} onChange={(e) => setQuery(e.target.value)} placeholder="Search…" aria-label="Search artifacts" />
                  </div>
                </div>

                <div className="imp-selbar">
                  <span>{visibleSelected} of {visible.length} selected</span>
                  <div>
                    <button type="button" className="imp-link" onClick={selectAllVisible}>Select all</button>
                    <button type="button" className="imp-link" onClick={clearVisible}>Clear</button>
                  </div>
                </div>

                <div className="imp-rows">
                  {visible.length === 0
                    ? <div className="wf-form-empty">{query.trim() ? "No matches." : `No ${activeTab} in this pack.`}</div>
                    : visible.map((r) => <PreviewRow key={r.sourcePath} row={r} checked={selected.has(r.sourcePath)} onToggle={() => toggle(r.sourcePath)} />)}
                </div>
              </div>
            </div>
          )}

          {importErr && (
            <div className="cn-banner cn-banner-err" style={{ marginTop: 14 }}>
              <div className="cn-banner-h">Couldn't install</div>
              <div className="cn-banner-p">{importErr}</div>
            </div>
          )}
        </div>

        {data && fetched && rows.length > 0 && (
          <div className="mdl-foot">
            <div className="mdl-foot-info">{installSummary(selectedAgents, selectedSkills)}{conflicts > 0 ? ` · ${conflicts} already installed` : ""}</div>
            <div style={{ display: "flex", gap: 8 }}>
              <button type="button" className="btn" onClick={onClose}>Cancel</button>
              <button type="button" className="btn btn-primary" onClick={commit} disabled={selected.size === 0 || importPack.isPending}>
                <Ic.Download size={14} /> {importPack.isPending ? "Installing…" : `Install ${selected.size}`}
              </button>
            </div>
          </div>
        )}
      </div>
    </>,
    document.body,
  );
}
