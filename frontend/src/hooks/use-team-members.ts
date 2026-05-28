import { useQuery } from "@tanstack/react-query";
import { useMemo } from "react";

import { teamsApi, type TeamMemberSummary } from "@/api/teams";

/**
 * Current team's members. Used to resolve message author ids (and `@user` reference ids) to
 * names/avatars. 60s staleTime — membership changes rarely relative to how often chat reads it.
 */
export function useTeamMembers() {
  return useQuery({
    queryKey: ["team-members"],
    queryFn: () => teamsApi.listMembers(),
    staleTime: 60_000,
  });
}

/** A userId → member lookup map, memoised so message rows don't each rebuild it. */
export function useTeamMemberMap(): Map<string, TeamMemberSummary> {
  const { data } = useTeamMembers();
  return useMemo(() => new Map((data ?? []).map(m => [m.userId, m])), [data]);
}
