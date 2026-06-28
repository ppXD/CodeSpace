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

  /** Author a new skill directly INTO the Library (a store entry under the team's Custom pack). */
  authorStore: (input: { name: string; description?: string | null; body?: string | null; category?: string | null }) =>
    fetchJson<{ id: string }>("/api/skills/library", { method: "POST", body: JSON.stringify(input) }),

  /** Copy a Library store skill into a new working (bindable) skill — how binding a Library skill to an agent works. */
  instantiateFromStore: (sourceDefinitionId: string) =>
    fetchJson<{ id: string }>("/api/skills/from-store", { method: "POST", body: JSON.stringify({ sourceDefinitionId }) }),

  /** Soft-delete a skill. */
  remove: (id: string) => fetchJson<void>(`/api/skills/${id}`, { method: "DELETE" }),
};
