import { fetchJson } from "./request";

/** Mirrors backend `TeamMemberSummary`. The minimal identity to put a name + avatar on a
 *  user id — drives chat author rendering and the `@`-mention picker. */
export interface TeamMemberSummary {
  userId: string;
  name: string;
  email: string;
  avatarUrl: string | null;
  /** True for a bot user (e.g. the per-team CodeSpace bot). Present on the member-identities list; drives the robot avatar. */
  isBot: boolean;
}

export const teamsApi = {
  /** Pickable members (bot-excluded) — for the `@`-mention picker, invite list, member roster. */
  listMembers: () => fetchJson<TeamMemberSummary[]>("/api/teams/members"),

  /** Identities for display/resolution, including bots — for turning an author/`@user` id into a
   *  name + avatar. A bot (e.g. the per-team CodeSpace bot that posts review cards) authors messages
   *  but isn't pickable, so name resolution must use this superset list, not `listMembers`. */
  listMemberIdentities: () => fetchJson<TeamMemberSummary[]>("/api/teams/member-identities"),
};
