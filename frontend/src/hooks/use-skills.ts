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

/** Author a new skill INTO the Library (a Custom-pack store entry); invalidates the Library packs/detail. Returns the new id. */
export function useAuthorStoreSkill() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (input: { name: string; description?: string | null; body?: string | null; category?: string | null }) => skillsApi.authorStore(input),
    onSuccess: () => Promise.all([
      queryClient.invalidateQueries({ queryKey: ["packs"] }),
      queryClient.invalidateQueries({ queryKey: ["pack"] }),
      queryClient.invalidateQueries({ queryKey: ["pack-artifacts"] }),
    ]),
  });
}

/** Instantiate a working (bindable) skill by copying a Library store skill; invalidates the skill list (so the new copy's label resolves) + the Library. Returns the new id. */
export function useInstantiateSkillFromStore() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (sourceDefinitionId: string) => skillsApi.instantiateFromStore(sourceDefinitionId),
    // Instantiate creates a WORKING bindable copy (PackId null) — it doesn't touch any pack's Store artifacts, so
    // the [pack-artifacts] detail lists stay valid; only the skill list + the Library's surfacing state can shift.
    onSuccess: () => Promise.all([
      queryClient.invalidateQueries({ queryKey: ["skills"] }),
      queryClient.invalidateQueries({ queryKey: ["packs"] }),
      queryClient.invalidateQueries({ queryKey: ["pack"] }),
    ]),
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
      queryClient.invalidateQueries({ queryKey: ["pack-artifacts"] }),
      queryClient.invalidateQueries({ queryKey: ["agents"] }),
      queryClient.invalidateQueries({ queryKey: ["agent"] }),
    ]),
  });
}
