import type { PackPreview } from "@/api/packs";

/** The selectability/state of a discovered pack item: importable (new), a handle conflict, or otherwise blocked. */
export type ImportFlag = "new" | "exists" | "blocked";

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
