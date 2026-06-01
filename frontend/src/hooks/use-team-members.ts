import { useQuery } from "@tanstack/react-query";
import { useMemo } from "react";

import { teamsApi, type TeamMemberSummary } from "@/api/teams";

/**
 * Current team's PICKABLE members (bot-excluded). For the `@`-mention picker, invite list, and
 * member roster — places that offer a user to choose. 60s staleTime — membership changes rarely
 * relative to how often chat reads it.
 */
export function useTeamMembers() {
  return useQuery({
    queryKey: ["team-members"],
    queryFn: () => teamsApi.listMembers(),
    staleTime: 60_000,
  });
}

/**
 * Current team's DISPLAY identities (bot-INCLUSIVE). For resolving a message author id (and
 * `@user` reference ids) to a name/avatar — a bot authors messages (e.g. review cards) but isn't
 * pickable, so name resolution needs this superset, not {@link useTeamMembers}.
 */
export function useTeamMemberIdentities() {
  return useQuery({
    queryKey: ["team-member-identities"],
    queryFn: () => teamsApi.listMemberIdentities(),
    staleTime: 60_000,
  });
}

/** A userId → identity lookup map (bot-inclusive), memoised so message rows don't each rebuild it. */
export function useTeamMemberIdentityMap(): Map<string, TeamMemberSummary> {
  const { data } = useTeamMemberIdentities();
  return useMemo(() => indexById(data), [data]);
}

function indexById(members: TeamMemberSummary[] | undefined): Map<string, TeamMemberSummary> {
  return new Map((members ?? []).map(m => [m.userId, m]));
}
