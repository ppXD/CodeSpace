import { fetchJson } from "./request";
import type { TeamRole } from "./types";

/** Mirrors backend `TeamMemberSummary`. */
export interface TeamMemberSummary {
  userId: string;
  name: string;
  email: string;
  avatarUrl: string | null;
  /** True for a bot user (e.g. the per-team CodeSpace bot). Drives the robot avatar; a bot holds no role. */
  isBot: boolean;
  /** Null for the bot — it is not a person and has nothing to be promoted to. */
  role: TeamRole | null;
  /** Null for an owner seeded without a membership row. */
  joinedAt: string | null;
}

/** A pending invitation as the members screen lists it. Never carries a token. */
export interface TeamInvitationSummary {
  id: string;
  email: string;
  role: TeamRole;
  invitedByName: string;
  expiresAt: string;
  isExpired: boolean;
}

/**
 * The one and only time the link is readable. The server keeps a hash, so it cannot be fetched
 * again — losing it means regenerating, which invalidates the one that went missing.
 */
export interface CreateInvitationResult {
  invitationId: string;
  inviteUrl: string;
  expiresAt: string;
}

export const teamsApi = {
  /** Pickable members (bot-excluded) — the `@`-mention picker, invite list, member roster. */
  listMembers: () => fetchJson<TeamMemberSummary[]>("/api/teams/members"),

  /** Identities for display/resolution, including bots — for turning an author id into a name + avatar. */
  listMemberIdentities: () => fetchJson<TeamMemberSummary[]>("/api/teams/member-identities"),

  changeRole: (userId: string, role: TeamRole) =>
    fetchJson<void>(`/api/teams/members/${userId}`, { method: "PATCH", body: JSON.stringify({ role }) }),

  remove: (userId: string) => fetchJson<void>(`/api/teams/members/${userId}`, { method: "DELETE" }),

  leave: () => fetchJson<void>("/api/teams/members/leave", { method: "POST" }),

  transferOwnership: (toUserId: string) =>
    fetchJson<void>("/api/teams/transfer-ownership", { method: "POST", body: JSON.stringify({ toUserId }) }),

  listInvitations: () => fetchJson<TeamInvitationSummary[]>("/api/teams/invitations"),

  invite: (email: string, role: TeamRole) =>
    fetchJson<CreateInvitationResult>("/api/teams/invitations", { method: "POST", body: JSON.stringify({ email, role }) }),

  revokeInvitation: (invitationId: string) =>
    fetchJson<void>(`/api/teams/invitations/${invitationId}`, { method: "DELETE" }),

  regenerateInvitation: (invitationId: string) =>
    fetchJson<CreateInvitationResult>(`/api/teams/invitations/${invitationId}/regenerate`, { method: "POST" }),
};
