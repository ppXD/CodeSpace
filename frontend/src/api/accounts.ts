import { fetchJson } from "./request";

/** Mirrors backend `AccountSummary`. Carries no secret and no token. */
export interface AccountSummary {
  id: string;
  name: string;
  email: string;
  isDeactivated: boolean;
  passwordMustChange: boolean;
  lastLoginDate: string | null;
}

/** The one and only time a reset link is readable — only its digest is stored. */
export interface PasswordResetLink {
  resetUrl: string;
  expiresAt: string;
}

/** Instance administration. Every call is global-admin only, enforced server-side. */
export const accountsApi = {
  list: () => fetchJson<AccountSummary[]>("/api/admin/accounts"),
  deactivate: (userId: string) => fetchJson<void>(`/api/admin/accounts/${userId}/deactivate`, { method: "POST" }),
  reactivate: (userId: string) => fetchJson<void>(`/api/admin/accounts/${userId}/reactivate`, { method: "POST" }),
  issueResetLink: (userId: string) => fetchJson<PasswordResetLink>(`/api/admin/accounts/${userId}/reset-link`, { method: "POST" }),
};
