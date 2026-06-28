import { flagFor, type ImportFlag, type Row } from "./packPreview";

/**
 * Shared preview-row UI for a pack's discovered agents + skills — used by the import-from-URL modal and the
 * Sync result modal so both render selection identically. Each row shows the derived @handle and a flag:
 * new (importable), already-exists (a team handle collides — not selectable), or can't-import (nameless /
 * unparseable). Presentational only; the flatten helper (toRows) + Row type live in packPreview.
 */

export function PreviewGroup({ title, rows, selected, onToggle }: { title: string; rows: Row[]; selected: Set<string>; onToggle: (p: string) => void }) {
  return (
    <div style={{ marginTop: 16 }}>
      <div className="wf-form-label" style={{ textTransform: "uppercase", letterSpacing: ".06em", fontSize: 10.5, color: "var(--muted)", marginBottom: 4 }}>{title} · {rows.length}</div>
      {rows.map((r) => <PreviewRow key={r.sourcePath} row={r} checked={selected.has(r.sourcePath)} onToggle={() => onToggle(r.sourcePath)} />)}
    </div>
  );
}

export function PreviewRow({ row, checked, onToggle }: { row: Row; checked: boolean; onToggle: () => void }) {
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
