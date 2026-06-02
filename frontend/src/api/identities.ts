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

/** `/api/me/identities` — link the caller's own provider identity by PAT (Model B). The list /
 *  unlink endpoints still exist server-side, but the only frontend caller is the reactive link modal. */
export const identitiesApi = {
  linkByPat: (input: { providerInstanceId: string; accessToken: string }) =>
    fetchJson<UserProviderIdentitySummary>("/api/me/identities/pat", {
      method: "POST",
      body: JSON.stringify(input),
    }),
};
