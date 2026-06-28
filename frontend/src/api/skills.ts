import { fetchJson } from "./request";

/** Where a skill was sourced from (mirrors backend SkillDefinitionOrigin). */
export type SkillDefinitionOrigin = "Authored" | "Imported";

/** Level-1 skill row (mirrors backend SkillDefinitionSummary) — name + grouping, no SKILL.md body. */
export interface SkillSummary {
  id: string;
  teamId: string;
  slug: string;
  name: string;
  description: string | null;
  category: string | null;
  origin: SkillDefinitionOrigin;
  packId: string | null;
  createdDate: string;
}

/** Level-2 skill detail (mirrors backend SkillDefinitionDetail) — the summary fields plus the SKILL.md body. */
export interface SkillDetail extends SkillSummary {
  body: string;
  rawFrontmatterJson: string;
  sourcePath: string | null;
}

export const skillsApi = {
  /** The team's active skills — the editor's skill-binding picker. */
  list: () => fetchJson<SkillSummary[]>("/api/skills"),

  /** One skill with its SKILL.md body — the Library detail modal. */
  get: (id: string) => fetchJson<SkillDetail>(`/api/skills/${id}`),

  /** Soft-delete a skill. */
  remove: (id: string) => fetchJson<void>(`/api/skills/${id}`, { method: "DELETE" }),
};
