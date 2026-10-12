import { fetchJson } from "./request";
import type { MeResponse } from "./types";

export interface SignInRequest {
  /** Either email or display name — backend accepts both, case-insensitive. */
  name: string;
  password: string;
}

export interface SignInResponse {
  /**
   * For API clients. The browser's session is the HttpOnly cookie the same response sets — see `adoptSessionFrom`,
   * which settles this and hands the rest on without it. Absent when the cookie carried the request: the API never
   * puts a session in a body a page script could read on the strength of the cookie alone.
   */
  token?: string;
  expiresAt: string;
  user: MeResponse;
}

export interface ChangePasswordRequest {
  currentPassword: string;
  newPassword: string;
}

export interface ChangePasswordResponse {
  user: MeResponse;
  /**
   * A token minted under the account's NEW security stamp. Changing a password revokes every token
   * issued before it, including the one that made this call — the response also replaces the session
   * cookie, and `adoptSessionFrom` must settle this token or the caller is signed out by their own change.
   * Absent when the cookie carried the request: the replacement cookie is then the whole answer.
   */
  token?: string;
  expiresAt: string;
}

export const authApi = {
  signIn: (input: SignInRequest) => fetchJson<SignInResponse>("/api/auth/sign-in", {
    method: "POST",
    body: JSON.stringify(input),
  }),

  changePassword: (input: ChangePasswordRequest) => fetchJson<ChangePasswordResponse>("/api/auth/change-password", {
    method: "POST",
    body: JSON.stringify(input),
  }),

  /** Anonymous — the token in the path is the credential. Answers 204 and signs nobody in: spending
   *  the link ends every session the account had, and handing one back here would undo that. */
  resetPassword: (token: string, newPassword: string) =>
    fetchJson<void>(`/api/auth/reset-password/${encodeURIComponent(token)}`, {
      method: "POST",
      body: JSON.stringify({ newPassword }),
    }),
};
