import { ApiError, fetchJson } from "./request";
import type { CredentialCapabilitiesResponse, CredentialSummary, CredentialUsage, DeleteProviderInstanceResponse, InitOAuthResponse, ProviderDefaults, ProviderInstanceSummary, ProviderInstanceUsage, ProviderKind, RevokeCredentialResponse } from "./types";

/**
 * Thin typed wrappers around the new OAuth + credential endpoints. They handle:
 *   • Bearer JWT + X-Team-Id headers (shared via fetchJson)
 *   • JSON serialization
 *   • Surfacing the backend's { code, message } error envelope as an ApiError
 *
 * Once schema.ts is regenerated these should migrate to the openapi-fetch client; the
 * function signatures match the eventual typed API to minimise churn.
 */

export interface InitOAuthRequest {
  providerInstanceId: string;
  displayName: string;
  intendedOwnerUserId?: string | null;
  returnUrl?: string | null;
  scopes?: string[] | null;
}

export interface AddProviderInstanceRequest {
  provider: ProviderKind;
  displayName: string;
  baseUrl: string;
  apiUrl?: string | null;
  webUrl?: string | null;
  oauthClientId?: string | null;
  oauthClientSecret?: string | null;
  oauthRedirectPath?: string | null;
  oauthDefaultScopes?: string[] | null;
}

/**
 * PATCH-style: only set the fields you want to change. Empty-string on oauthClientSecret
 * means "leave the existing secret alone" — matches the form's "leave blank to keep"
 * password input pattern. Provider kind is not editable post-creation (delete + re-add).
 */
export interface UpdateProviderInstanceRequest {
  displayName?: string | null;
  baseUrl?: string | null;
  apiUrl?: string | null;
  webUrl?: string | null;
  oauthClientId?: string | null;
  oauthClientSecret?: string | null;
  oauthRedirectPath?: string | null;
  oauthDefaultScopes?: string[] | null;
}

export const oauthApi = {
  listProviderInstances: () => fetchJson<ProviderInstanceSummary[]>("/api/provider-instances"),

  addProviderInstance: (input: AddProviderInstanceRequest) => fetchJson<{ id: string }>("/api/provider-instances", {
    method: "POST",
    body: JSON.stringify(input),
  }),

  updateProviderInstance: (providerInstanceId: string, input: UpdateProviderInstanceRequest) =>
    fetchJson<void>(`/api/provider-instances/${encodeURIComponent(providerInstanceId)}`, {
      method: "PATCH",
      body: JSON.stringify(input),
    }),

  deleteProviderInstance: (providerInstanceId: string, options: { force?: boolean } = {}) =>
    fetchJson<DeleteProviderInstanceResponse>(
      `/api/provider-instances/${encodeURIComponent(providerInstanceId)}${options.force ? "?force=true" : ""}`,
      { method: "DELETE" },
    ),

  // Pre-delete preview — how many repos / credentials would be touched. UI uses this to
  // word the confirm dialog and decide whether to offer the cascade option.
  getProviderInstanceUsage: (providerInstanceId: string) =>
    fetchJson<ProviderInstanceUsage>(`/api/provider-instances/${encodeURIComponent(providerInstanceId)}/usage`),

  listCredentials: (providerInstanceId?: string) => {
    const qs = providerInstanceId ? `?providerInstanceId=${encodeURIComponent(providerInstanceId)}` : "";
    return fetchJson<CredentialSummary[]>(`/api/credentials${qs}`);
  },

  initOAuth: (input: InitOAuthRequest) => fetchJson<InitOAuthResponse>("/api/credentials/oauth/init", {
    method: "POST",
    body: JSON.stringify(input),
  }),

  // Add a GitLab Group Access Token as a TEAM-SERVICE credential (owned by the team, not a person).
  addGroupAccessToken: (input: { providerInstanceId: string; displayName: string; token: string }) =>
    fetchJson<{ id: string }>("/api/credentials/group-access-token", {
      method: "POST",
      body: JSON.stringify(input),
    }),

  revokeCredential: (credentialId: string) => fetchJson<RevokeCredentialResponse>(`/api/credentials/${credentialId}/revoke`, {
    method: "POST",
  }),

  // Pre-revoke preview — drives the confirm dialog's impact wording.
  getCredentialUsage: (credentialId: string) =>
    fetchJson<CredentialUsage>(`/api/credentials/${encodeURIComponent(credentialId)}/usage`),

  // Provider defaults — recommended baseUrl, default OAuth scopes, callback URL. Driven by
  // the backend's IProviderModule so frontend never duplicates scope strings.
  getProviderDefaults: (provider: ProviderKind) =>
    fetchJson<ProviderDefaults>(`/api/provider-instances/defaults/${encodeURIComponent(provider)}`),

  // Per-credential capability availability — what the granted scopes can/can't do.
  // Returns "Read ✓ · Webhooks ⚠ needs api" style data the UI renders as badges.
  getCredentialCapabilities: (credentialId: string) =>
    fetchJson<CredentialCapabilitiesResponse>(`/api/credentials/${encodeURIComponent(credentialId)}/capabilities`),
};

export { ApiError };
