import { fetchJson } from "./request";
import type { SignInResponse } from "./auth";
import type { TeamRole } from "./types";

/**
 * Team invitations — the only way an account is created, since there is no public sign-up.
 *
 * Both endpoints are ANONYMOUS: the invitee holds a token, not a session. The token in the URL is
 * the whole credential, so it is never echoed into a query string here and never logged.
 *
 * The backend for these has not landed yet; this module is the consumer-side contract the server
 * implementation targets. Until it exists both calls 404, which the invite page renders as an
 * expired-or-revoked link — the same terminal state a real dead token produces.
 */

export interface InvitationPreview {
  teamName: string;
  /** Display name of the member who sent it, for "X invited you to Y". */
  invitedByName: string;
  /** Role the invitee lands on — shown before they accept, never chosen by them. */
  role: TeamRole;
  /** The address the invitation is bound to; acceptance must match it. */
  email: string;
  expiresAt: string;
  /** True when the address already has a CodeSpace account, so the page asks them to sign in instead of setting a password. */
  accountExists: boolean;
}

export interface AcceptInvitationRequest {
  /** Omitted when the invitee already has an account — the server takes the name from it. */
  name?: string;
  password?: string;
}

export const invitationsApi = {
  preview: (token: string) => fetchJson<InvitationPreview>(`/api/invitations/${encodeURIComponent(token)}`),

  accept: (token: string, input: AcceptInvitationRequest) =>
    fetchJson<SignInResponse>(`/api/invitations/${encodeURIComponent(token)}/accept`, {
      method: "POST",
      body: JSON.stringify(input),
    }),
};
