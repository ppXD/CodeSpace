import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useEffect, useMemo } from "react";

import { meApi } from "@/api/me";
import type { MeTeam } from "@/api/types";

const ACTIVE_TEAM_STORAGE_KEY = "codespace.activeTeamId";

/**
 * URL alias for the user's Personal team. Backend slugs for Personal teams have
 * a `-{8-hex-of-userId}` suffix to keep them globally unique (e.g.
 * `personal-a3f8c1d2`), which looks like noise in the URL bar. The frontend
 * substitutes this alias when writing URLs, and resolves it back via
 * <see cref="resolveTeamByUrlSlug"/> when reading URLs. Always lowercase.
 */
export const PERSONAL_TEAM_URL_ALIAS = "personal";

/**
 * Resolve a URL slug to the matching team in the user's membership list.
 * Accepts either the team's real slug or the special <c>personal</c> alias —
 * the alias points at whichever team has <c>kind === "Personal"</c> (there's
 * exactly one per user).
 */
export function resolveTeamByUrlSlug(teams: ReadonlyArray<MeTeam>, urlSlug: string): MeTeam | undefined {
  if (urlSlug === PERSONAL_TEAM_URL_ALIAS) {
    return teams.find(t => t.kind === "Personal");
  }
  return teams.find(t => t.slug === urlSlug);
}

/**
 * Inverse of <see cref="resolveTeamByUrlSlug"/> — produces the URL-facing slug
 * for a team. Personal teams collapse to the <c>personal</c> alias; Workspace
 * teams use their canonical backend slug.
 */
export function teamToUrlSlug(team: MeTeam): string {
  return team.kind === "Personal" ? PERSONAL_TEAM_URL_ALIAS : team.slug;
}

export function useMe() {
  return useQuery({ queryKey: ["me"], queryFn: () => meApi.me(), staleTime: 60_000 });
}

/**
 * Returns the currently-active team and a setter that persists across reloads. The setter
 * writes to localStorage and invalidates all team-scoped queries so they re-fetch with the
 * new X-Team-Id header.
 *
 * Auto-picks the first team on initial load if nothing is stored — first-run users see a
 * sensible default instead of an empty workspace.
 */
export function useActiveTeam() {
  const me = useMe();
  const queryClient = useQueryClient();

  const stored = readStored();
  const teams = me.data?.teams ?? [];

  const active: MeTeam | undefined = useMemo(() => {
    if (stored && teams.some(t => t.id === stored)) return teams.find(t => t.id === stored);
    return teams[0];
  }, [stored, teams]);

  // Persist the chosen team so the api/client.ts middleware reads the same value on the
  // next request, even before the user explicitly picks one.
  useEffect(() => {
    if (active && active.id !== stored) writeStored(active.id);
  }, [active, stored]);

  const setActive = useMutation({
    mutationFn: async (teamId: string) => {
      writeStored(teamId);
      return teamId;
    },
    onSuccess: () => {
      // Anything keyed by the team header needs a refetch.
      queryClient.invalidateQueries({ queryKey: ["repositories"] });
      queryClient.invalidateQueries({ queryKey: ["credentials"] });
      queryClient.invalidateQueries({ queryKey: ["provider-instances"] });
    },
  });

  return { me, teams, active, setActive: setActive.mutate };
}

function readStored(): string | null {
  if (typeof window === "undefined") return null;
  return localStorage.getItem(ACTIVE_TEAM_STORAGE_KEY);
}

function writeStored(teamId: string) {
  if (typeof window !== "undefined") localStorage.setItem(ACTIVE_TEAM_STORAGE_KEY, teamId);
}
