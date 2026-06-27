import { fetchJson } from "./request";

// ─── Types (mirror backend Pack preview / import DTOs) ──────────────────────────

/** One discovered AGENT in a URL pack preview (mirrors backend AgentPackPreviewItem). */
export interface AgentPackPreviewItem {
  sourcePath: string;
  name: string;
  derivedSlug: string;
  description: string | null;
  systemPrompt: string;
  model: string | null;
  tools: string[] | null;
  rawFrontmatterJson: string;
  diagnostics: string[];
  /** An active persona with this handle already exists in the team — import will SKIP it. */
  slugConflict: boolean;
  /** Parseable + named + no conflict. The UI defaults its selection to importable items. */
  importable: boolean;
}

/** One discovered SKILL in a URL pack preview (mirrors backend SkillPackPreviewItem). */
export interface SkillPackPreviewItem {
  sourcePath: string;
  name: string;
  derivedSlug: string;
  description: string | null;
  body: string;
  category: string | null;
  rawFrontmatterJson: string;
  diagnostics: string[];
  slugConflict: boolean;
  importable: boolean;
}

/** Dry-run view of a pack cloned from a URL — agents + skills with conflict/importable flags (mirrors backend PackPreview). */
export interface PackPreview {
  reference: string | null;
  agents: AgentPackPreviewItem[];
  skills: SkillPackPreviewItem[];
}

export type PackImportOutcome = "Imported" | "Updated" | "Skipped" | "Failed";
export type PackArtifactKind = "Agent" | "Skill";

/** Per-selected-path outcome of a commit (mirrors backend PackArtifactImportResult). */
export interface PackArtifactImportResult {
  sourcePath: string;
  kind: PackArtifactKind | null;
  outcome: PackImportOutcome;
  definitionId: string | null;
  reason: string | null;
}

/** Result of committing a previewed URL pack (mirrors backend PackImportResult). */
export interface PackImportResult {
  packId: string;
  items: PackArtifactImportResult[];
}

// ─── API client ─────────────────────────────────────────────────────────────────

export const packsApi = {
  /** Clone + discover a pack at a URL (host-allowlist-guarded, persists nothing). */
  previewFromUrl: (url: string, reference: string) =>
    fetchJson<PackPreview>("/api/agents/import-preview-url", { method: "POST", body: JSON.stringify({ url, reference: reference.trim() || null }) }),

  /** Commit the selected source-paths from a previewed URL pack. */
  importFromUrl: (url: string, reference: string, sourcePaths: string[]) =>
    fetchJson<PackImportResult>("/api/agents/import-url", { method: "POST", body: JSON.stringify({ url, reference: reference.trim() || null, sourcePaths }) }),
};
