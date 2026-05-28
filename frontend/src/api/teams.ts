import { fetchJson } from "./request";

/** Mirrors backend `TeamMemberSummary`. The minimal identity to put a name + avatar on a
 *  user id — drives chat author rendering and the `@`-mention picker. */
export interface TeamMemberSummary {
  userId: string;
  name: string;
  email: string;
  avatarUrl: string | null;
}

export const teamsApi = {
  /** Current team's members (from the X-Team-Id header). */
  listMembers: () => fetchJson<TeamMemberSummary[]>("/api/teams/members"),
};
