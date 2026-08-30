import { useState } from "react";

import { roomFileUnavailableNote } from "../sessions/roomFileUnavailable";

/**
 * Collapsible JSON tree used by RunDetailView for the normalized payload, run outputs and
 * each node's inputs/outputs. Objects and arrays carry a click-to-toggle caret so a reader can
 * fold away noisy subtrees (e.g. a large PR-diff blob) while keeping the surrounding shape in
 * view. Everything starts expanded, so the default render shows exactly what the old `<pre>`
 * dump did — folding is opt-in, per node.
 *
 * Pure presentational + theme-only: styled with the `.acs-root`-scoped `.wf-jsonv-*` classes,
 * so any host must live inside `.acs-root` (same constraint as the rest of RunDetailView).
 */
export function JsonView({ data }: { data: unknown }) {
  return (
    <div className="wf-jsonv">
      <JsonNode value={data} isLast />
    </div>
  );
}

/** The storage lanes a shed offload pointer can name — the artifact plane's own kinds, a subset of the Room's file reasons. */
const SHED_REASONS = ["MetadataMissing", "PhysicalObjectMissing", "IntegrityFailure", "BackendUnavailable", "AccessDenied"] as const;

/**
 * The note for a value the server could not read back. It keeps the `$artifact_ref` pointer and adds the lane that
 * failed; rendered as a bare object that reads as machine noise, so the reason is shown as the sentence the Room
 * already uses for it. A healthy pointer carries no reason and renders as the object it is.
 */
function shedNote(value: unknown): string | null {
  const marker = value !== null && typeof value === "object" && !Array.isArray(value)
    ? (value as Record<string, unknown>)["$artifact_ref"]
    : null;
  const reason = marker !== null && typeof marker === "object" ? (marker as Record<string, unknown>).reason : null;

  return SHED_REASONS.includes(reason as (typeof SHED_REASONS)[number])
    ? roomFileUnavailableNote(reason as (typeof SHED_REASONS)[number])
    : null;
}

/** One JSON value — a leaf (primitive), a shed pointer, or a branch (object/array). */
function JsonNode({ name, value, isLast }: { name?: string; value: unknown; isLast: boolean }) {
  const note = shedNote(value);

  if (note !== null)
    return (
      <div className="wf-jsonv-row">
        <span className="wf-jsonv-caret" aria-hidden />
        <JsonKey name={name} />
        <span className="wf-jsonv-unavailable" role="status">{note}</span>
        {!isLast && <span className="wf-jsonv-punc">,</span>}
      </div>
    );

  if (value !== null && typeof value === "object")
    return <JsonBranch name={name} value={value} isLast={isLast} />;

  return <JsonLeaf name={name} value={value} isLast={isLast} />;
}

/** Primitive row: optional `"key":` + a type-coloured scalar + trailing comma. */
function JsonLeaf({ name, value, isLast }: { name?: string; value: unknown; isLast: boolean }) {
  return (
    <div className="wf-jsonv-row">
      <span className="wf-jsonv-caret" aria-hidden />
      <JsonKey name={name} />
      <JsonScalar value={value} />
      {!isLast && <span className="wf-jsonv-punc">,</span>}
    </div>
  );
}

/** Object/array row(s) — collapsible when non-empty, inline `{}`/`[]` when empty. */
function JsonBranch({ name, value, isLast }: { name?: string; value: object; isLast: boolean }) {
  const isArray = Array.isArray(value);
  const entries: Array<[string | undefined, unknown]> = isArray
    ? (value as unknown[]).map((v) => [undefined, v])
    : Object.entries(value as Record<string, unknown>);

  const [open, setOpen] = useState(true);

  const openCh = isArray ? "[" : "{";
  const closeCh = isArray ? "]" : "}";

  if (entries.length === 0) {
    return (
      <div className="wf-jsonv-row">
        <span className="wf-jsonv-caret" aria-hidden />
        <JsonKey name={name} />
        <span className="wf-jsonv-punc">{openCh}{closeCh}</span>
        {!isLast && <span className="wf-jsonv-punc">,</span>}
      </div>
    );
  }

  const toggle = () => setOpen((o) => !o);
  const unit = isArray ? "item" : "key";

  return (
    <div className="wf-jsonv-branch">
      <div
        className="wf-jsonv-row wf-jsonv-toggle"
        role="button"
        tabIndex={0}
        aria-expanded={open}
        onClick={toggle}
        onKeyDown={(e) => {
          if (e.key === "Enter" || e.key === " ") {
            e.preventDefault();
            toggle();
          }
        }}
      >
        <span className="wf-jsonv-caret" aria-hidden>{open ? "▾" : "▸"}</span>
        <JsonKey name={name} />
        <span className="wf-jsonv-punc">{openCh}</span>
        {!open && (
          <>
            <span className="wf-jsonv-ellipsis">…</span>
            <span className="wf-jsonv-punc">{closeCh}</span>
            {!isLast && <span className="wf-jsonv-punc">,</span>}
            <span className="wf-jsonv-count">{entries.length} {entries.length === 1 ? unit : `${unit}s`}</span>
          </>
        )}
      </div>

      {open && (
        <>
          <div className="wf-jsonv-children">
            {entries.map(([k, v], i) => (
              <JsonNode key={k ?? i} name={k} value={v} isLast={i === entries.length - 1} />
            ))}
          </div>
          <div className="wf-jsonv-row">
            <span className="wf-jsonv-caret" aria-hidden />
            <span className="wf-jsonv-punc">{closeCh}</span>
            {!isLast && <span className="wf-jsonv-punc">,</span>}
          </div>
        </>
      )}
    </div>
  );
}

/** `"key":` label — omitted for array items and the root value. */
function JsonKey({ name }: { name?: string }) {
  if (name === undefined) return null;

  return (
    <>
      <span className="wf-jsonv-key">"{name}"</span>
      <span className="wf-jsonv-punc">: </span>
    </>
  );
}

/** A primitive value, coloured by type. */
function JsonScalar({ value }: { value: unknown }) {
  if (typeof value === "string") return <span className="wf-jsonv-str">"{value}"</span>;
  if (typeof value === "number") return <span className="wf-jsonv-num">{String(value)}</span>;
  if (typeof value === "boolean") return <span className="wf-jsonv-bool">{String(value)}</span>;

  return <span className="wf-jsonv-null">null</span>;
}
