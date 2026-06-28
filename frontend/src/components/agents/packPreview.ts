import type { PackPreview } from "@/api/packs";

/** The selectability/state of a discovered pack item: importable (new), a handle conflict, or otherwise blocked. */
export type ImportFlag = "new" | "exists" | "blocked";

/** One discovered item, flattened across agents + skills for uniform rendering in the preview lists. */
export interface Row {
  sourcePath: string;
  name: string;
  derivedSlug: string;
  description: string | null;
  diagnostics: string[];
  slugConflict: boolean;
  importable: boolean;
  kind: "agent" | "skill";
}

/** Flatten a PackPreview into agent-then-skill rows. */
export function toRows(p: PackPreview): Row[] {
  return [
    ...p.agents.map((a): Row => ({ sourcePath: a.sourcePath, name: a.name, derivedSlug: a.derivedSlug, description: a.description, diagnostics: a.diagnostics, slugConflict: a.slugConflict, importable: a.importable, kind: "agent" })),
    ...p.skills.map((s): Row => ({ sourcePath: s.sourcePath, name: s.name, derivedSlug: s.derivedSlug, description: s.description, diagnostics: s.diagnostics, slugConflict: s.slugConflict, importable: s.importable, kind: "skill" })),
  ];
}

/** Classify a discovered item for its chip + selectability. Importable → new; else a slug conflict → exists; else blocked (e.g. nameless / unparseable). */
export function flagFor(item: { importable: boolean; slugConflict: boolean }): ImportFlag {
  if (item.importable) return "new";
  if (item.slugConflict) return "exists";
  return "blocked";
}

/** The source-paths pre-selected when a preview loads — every importable agent + skill (the operator can deselect). */
export function defaultSelectedPaths(preview: PackPreview): string[] {
  return [...preview.agents, ...preview.skills].filter((i) => i.importable).map((i) => i.sourcePath);
}

/** Case-insensitive substring filter over name / @handle / source path — the import modal's per-tab search. Blank → all. */
export function filterRows(rows: Row[], query: string): Row[] {
  const q = query.trim().toLowerCase();
  if (!q) return rows;

  return rows.filter((r) =>
    r.name.toLowerCase().includes(q)
    || r.derivedSlug.toLowerCase().includes(q)
    || (r.sourcePath?.toLowerCase().includes(q) ?? false));
}
