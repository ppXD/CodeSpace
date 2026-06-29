import type { AgentBoundSkill } from "@/api/agents";
import type { SkillSummary } from "@/api/skills";

/**
 * Pure derivation for the editor's skill-binding picker — the bound-chip label lookup, kept out of the component
 * so it's unit-testable without rendering. No React. (Add-list filtering moved to the Library picker modal.)
 */

/**
 * Display NAME for each bound skill (the human title, e.g. "TDD") — NOT the slug. A bound skill is a private
 * working copy whose slug is auto-disambiguated to a unique team handle (tdd, tdd-2, tdd-3…), so showing the slug
 * leaked those -2/-3 suffixes into the chip; the name is the same friendly title across copies. The live team list
 * wins; the persona's already-bound skills fill the gap on first render before that list resolves. Both carry only
 * ACTIVE skills, so a selected id always resolves here.
 */
export function skillLabels(skills: SkillSummary[], boundSkills: AgentBoundSkill[]): Map<string, string> {
  const labels = new Map<string, string>();
  boundSkills.forEach((s) => labels.set(s.skillDefinitionId, s.name));
  skills.forEach((s) => labels.set(s.id, s.name));

  return labels;
}
