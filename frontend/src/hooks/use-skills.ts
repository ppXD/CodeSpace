import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";

import { skillsApi } from "@/api/skills";

/** The team's skills — backs the editor's skill-binding picker. Not keyed by team id (switching team clears the cache). */
export function useSkills() {
  return useQuery({
    queryKey: ["skills"],
    queryFn: () => skillsApi.list(),
  });
}

/** One skill's detail (with the SKILL.md body) — the Library detail modal. Keyed by id; only enabled when opened. */
export function useSkill(skillId: string | null) {
  return useQuery({
    queryKey: ["skill", skillId],
    queryFn: () => skillsApi.get(skillId!),
    enabled: !!skillId,
  });
}

/**
 * Soft-delete a skill. Refreshes everything the deletion touches: the skill list + picker, the Library packs/
 * detail (the skill drops from its pack), and the agents (a deleted skill drops from any persona's bound set).
 */
export function useDeleteSkill() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (id: string) => skillsApi.remove(id),
    onSuccess: () => Promise.all([
      queryClient.invalidateQueries({ queryKey: ["skills"] }),
      queryClient.invalidateQueries({ queryKey: ["packs"] }),
      queryClient.invalidateQueries({ queryKey: ["pack"] }),
      queryClient.invalidateQueries({ queryKey: ["agents"] }),
      queryClient.invalidateQueries({ queryKey: ["agent"] }),
    ]),
  });
}
