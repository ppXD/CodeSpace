import type { AgentBoundSkill } from "@/api/agents";
import type { SkillSummary } from "@/api/skills";

/**
 * Pure derivation for the editor's skill-binding picker — the bound-chip label lookup, kept out of the component
 * so it's unit-testable without rendering. No React. (Add-list filtering moved to the Library picker modal.)
 */

/**
 * Label (slug) for each bound skill. The live team list wins; the persona's already-bound skills fill the gap on
 * first render before that list resolves. Both carry only ACTIVE skills, so a selected id always resolves here.
 */
export function skillLabels(skills: SkillSummary[], boundSkills: AgentBoundSkill[]): Map<string, string> {
  const labels = new Map<string, string>();
  boundSkills.forEach((s) => labels.set(s.skillDefinitionId, s.slug));
  skills.forEach((s) => labels.set(s.id, s.slug));

  return labels;
}
