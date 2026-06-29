import type { AgentBoundSkill } from "@/api/agents";
import type { SkillSummary } from "@/api/skills";
import type { Option } from "@/components/common/Combo";

/**
 * Pure derivations for the editor's skill-binding picker — kept out of the component so the label lookup and
 * the add-list filtering are unit-testable without rendering. No React.
 */

/**
 * Label (slug) for each bound/available skill. The live team list wins; the persona's already-bound skills
 * fill the gap on first render before that list resolves. Both carry only ACTIVE skills, so a selected id
 * always resolves here.
 */
export function skillLabels(skills: SkillSummary[], boundSkills: AgentBoundSkill[]): Map<string, string> {
  const labels = new Map<string, string>();
  boundSkills.forEach((s) => labels.set(s.skillDefinitionId, s.slug));
  skills.forEach((s) => labels.set(s.id, s.slug));

  return labels;
}

/** The team's skills not already selected, as "Add a skill" combo options (name + @slug hint). */
export function availableSkillOptions(skills: SkillSummary[], selected: string[]): Option[] {
  const chosen = new Set(selected);

  return skills.filter((s) => !chosen.has(s.id)).map((s) => ({ value: s.id, label: s.name, desc: `@${s.slug}` }));
}
