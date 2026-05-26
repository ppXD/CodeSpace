import { fetchJson } from "./request";
import type { MeResponse } from "./types";

export interface SignInRequest {
  /** Either email or display name — backend accepts both, case-insensitive. */
  name: string;
  password: string;
}

export interface SignInResponse {
  token: string;
  expiresAt: string;
  user: MeResponse;
}

export interface ChangePasswordRequest {
  currentPassword: string;
  newPassword: string;
}

export interface ChangePasswordResponse {
  user: MeResponse;
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
};

const JWT_STORAGE_KEY = "codespace.jwt";
const ACTIVE_TEAM_STORAGE_KEY = "codespace.activeTeamId";

export function storeJwt(token: string) {
  localStorage.setItem(JWT_STORAGE_KEY, token);
}

export function readJwt(): string | null {
  return localStorage.getItem(JWT_STORAGE_KEY);
}

export function clearAuthState() {
  localStorage.removeItem(JWT_STORAGE_KEY);
  localStorage.removeItem(ACTIVE_TEAM_STORAGE_KEY);
}

export function isAuthenticated(): boolean {
  return readJwt() != null;
}
