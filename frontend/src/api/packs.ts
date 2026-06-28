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

/** Where a pack is sourced from (mirrors backend PackKind). `Custom` is the synthetic per-team locally-authored pack. */
export type PackKind = "Github" | "GitUrl" | "Custom";

/**
 * One row in the Library's source rail — an imported pack (a github/git-url library) as a category, with its
 * source + freshness + the active-artifact counts (mirrors backend PackSummary).
 */
export interface PackSummary {
  id: string;
  kind: PackKind;
  name: string;
  url: string | null;
  reference: string | null;
  lastSyncedSha: string | null;
  lastSyncedDate: string | null;
  agentCount: number;
  skillCount: number;
}

/** One artifact inside a pack — an agent persona or a skill — as the Library detail lists them uniformly (mirrors backend PackArtifactSummary). */
export interface PackArtifactSummary {
  kind: PackArtifactKind;
  id: string;
  slug: string;
  name: string;
  description: string | null;
  sourcePath: string | null;
}

/** A pack with every active agent + skill it contributed — the Library detail pane (mirrors backend PackDetail). */
export interface PackDetail {
  pack: PackSummary;
  artifacts: PackArtifactSummary[];
}

/**
 * One server-side page of a pack's artifacts of a single kind (mirrors backend PagedArtifacts). `total` is the
 * full count for the (kind + search) query — it drives the pager, not the length of `items`. `page` is the
 * 0-based index actually returned (clamped server-side).
 */
export interface PagedArtifacts {
  items: PackArtifactSummary[];
  total: number;
  page: number;
  pageCount: number;
}

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

/**
 * Result of re-pulling a pack from its saved source (mirrors backend PackSyncResult). Already-imported
 * artifacts are refreshed in place — `upToDate` were unchanged, `updated` had their content re-applied;
 * `newArtifacts` are the discovered-but-not-yet-imported artifacts, surfaced as a preview to select + add
 * (never auto-imported).
 */
export interface PackSyncResult {
  packId: string;
  reference: string | null;
  upToDate: number;
  updated: number;
  newArtifacts: PackPreview;
}

// ─── API client ─────────────────────────────────────────────────────────────────

export const packsApi = {
  /** The team's imported packs (the Library's source rail) with freshness + artifact counts. */
  list: () => fetchJson<PackSummary[]>("/api/packs"),

  /** One pack with its agents + skills (the Library detail pane). */
  get: (packId: string) => fetchJson<PackDetail>(`/api/packs/${packId}`),

  /** One server-side page of a pack's artifacts of a single kind, optionally name/handle-filtered (the paginated Library detail tab + the pickers). */
  listArtifacts: (packId: string, kind: PackArtifactKind, search: string, page: number, pageSize: number) =>
    fetchJson<PagedArtifacts>(`/api/packs/${packId}/artifacts?kind=${kind}&search=${encodeURIComponent(search)}&page=${page}&pageSize=${pageSize}`),

  /** Clone + discover a pack at a URL (host-allowlist-guarded, persists nothing). */
  previewFromUrl: (url: string, reference: string) =>
    fetchJson<PackPreview>("/api/agents/import-preview-url", { method: "POST", body: JSON.stringify({ url, reference: reference.trim() || null }) }),

  /** Commit the selected source-paths from a previewed URL pack. */
  importFromUrl: (url: string, reference: string, sourcePaths: string[]) =>
    fetchJson<PackImportResult>("/api/agents/import-url", { method: "POST", body: JSON.stringify({ url, reference: reference.trim() || null, sourcePaths }) }),

  /** Re-pull a pack from its saved source — refresh its imported artifacts, return what changed + the new ones. */
  sync: (packId: string) =>
    fetchJson<PackSyncResult>(`/api/packs/${packId}/sync`, { method: "POST" }),
};
