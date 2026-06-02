import { fetchJson } from "./request";
import type { ProviderKind } from "./types";

/** The current user's OWN linked provider identity (Model B). Mirrors the backend
 *  `UserProviderIdentitySummary` — never carries token material. */
export interface UserProviderIdentitySummary {
  id: string;
  providerInstanceId: string;
  provider: ProviderKind;
  providerUsername: string;
  providerUserId: string;
  avatarUrl?: string | null;
  credentialStatus: string;
  createdDate: string;
}

/** `/api/me/identities` — the caller's own provider identities (link via PAT / list / unlink). */
export const identitiesApi = {
  listMine: () => fetchJson<UserProviderIdentitySummary[]>("/api/me/identities"),

  linkByPat: (input: { providerInstanceId: string; accessToken: string }) =>
    fetchJson<UserProviderIdentitySummary>("/api/me/identities/pat", {
      method: "POST",
      body: JSON.stringify(input),
    }),

  unlink: (identityId: string) =>
    fetchJson<void>(`/api/me/identities/${encodeURIComponent(identityId)}`, { method: "DELETE" }),
};
