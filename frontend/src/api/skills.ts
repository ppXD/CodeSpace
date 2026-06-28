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

export const skillsApi = {
  /** The team's active skills — the editor's skill-binding picker. */
  list: () => fetchJson<SkillSummary[]>("/api/skills"),
};
