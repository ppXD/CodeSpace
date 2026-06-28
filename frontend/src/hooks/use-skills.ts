import { useQuery } from "@tanstack/react-query";

import { skillsApi } from "@/api/skills";

/** The team's skills — backs the editor's skill-binding picker. Not keyed by team id (switching team clears the cache). */
export function useSkills() {
  return useQuery({
    queryKey: ["skills"],
    queryFn: () => skillsApi.list(),
  });
}
