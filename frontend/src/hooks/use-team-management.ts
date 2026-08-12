import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";

import { teamsApi } from "@/api/teams";
import type { TeamRole } from "@/api/types";
import { useActiveTeam } from "./use-me";

/**
 * Team permissions, straight from the server's own expansion of its matrix.
 *
 * <p>Deliberately no local table mapping roles to abilities. One existed in the design and was cut:
 * a second copy of the rules drifts the first time a permission moves tier, and the symptom is a
 * button that stays visible and answers 403 — the worst of both, since the user is told they may do
 * something and then refused.</p>
 */
export function useTeamPermissions() {
  const { active } = useActiveTeam();
  const held = new Set(active?.permissions ?? []);

  // `isPersonal` rather than another permission: a Personal team's owner genuinely HOLDS
  // members.manage — the matrix expands it from their role like anywhere else — and the server still
  // refuses to invite into one, because a solo space having a second person in it is a contradiction
  // rather than a permission question. So the shape of the team is what the UI has to branch on.
  return { can: (permission: string) => held.has(permission), role: active?.role ?? null, isPersonal: active?.kind === "Personal" };
}

export const TeamPermissions = {
  MembersManage: "members.manage",
  TeamManage: "team.manage",
} as const;

/** Pending invitations. Only fetched when the caller may manage members — the endpoint refuses otherwise. */
export function useTeamInvitations(enabled: boolean) {
  return useQuery({
    queryKey: ["team-invitations"],
    queryFn: () => teamsApi.listInvitations(),
    enabled,
  });
}

/**
 * Every write on this page. They all invalidate the roster and the invitation list together, because
 * the two are one picture: accepting an invitation removes a row from one and adds it to the other,
 * and refreshing only half of that shows a person twice.
 */
export function useTeamManagement() {
  const queryClient = useQueryClient();

  const refresh = async () => {
    await Promise.all([
      queryClient.invalidateQueries({ queryKey: ["team-members"] }),
      queryClient.invalidateQueries({ queryKey: ["team-invitations"] }),
      // The caller's own role can change here — transferring ownership demotes them — and /me is
      // what every permission check on the page reads.
      queryClient.invalidateQueries({ queryKey: ["me"] }),
    ]);
  };

  return {
    changeRole: useMutation({ mutationFn: (input: { userId: string; role: TeamRole }) => teamsApi.changeRole(input.userId, input.role), onSuccess: refresh }),
    removeMember: useMutation({ mutationFn: (userId: string) => teamsApi.remove(userId), onSuccess: refresh }),
    transferOwnership: useMutation({ mutationFn: (toUserId: string) => teamsApi.transferOwnership(toUserId), onSuccess: refresh }),
    invite: useMutation({ mutationFn: (input: { email: string; role: TeamRole }) => teamsApi.invite(input.email, input.role), onSuccess: refresh }),
    revokeInvitation: useMutation({ mutationFn: (id: string) => teamsApi.revokeInvitation(id), onSuccess: refresh }),
    regenerateInvitation: useMutation({ mutationFn: (id: string) => teamsApi.regenerateInvitation(id), onSuccess: refresh }),
  };
}
