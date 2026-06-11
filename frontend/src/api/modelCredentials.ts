import { fetchJson } from "./request";
import type { CredentialStatus } from "./types";

// ─── Types (mirror backend ModelCredential DTOs) ────────────────────────────────

/**
 * Mirrors backend `ModelCredentialSummary` — a team's model credential as shown in settings. NEVER carries
 * the secret: `keyHint` is the masked tail (e.g. `····a1b2`), null for a keyless provider. `baseUrl` is
 * non-secret config and is shown verbatim.
 */
export interface ModelCredentialSummary {
  id: string;
  teamId: string;
  provider: string;
  displayName: string;
  keyHint: string | null;
  baseUrl: string | null;
  status: CredentialStatus;
  createdDate: string;
}

export interface AddModelCredentialInput {
  provider: string;
  displayName: string;
  /** Plaintext key sent over TLS; the backend encrypts it and never echoes it back. Omit/blank for a keyless provider. */
  apiKey?: string | null;
  baseUrl?: string | null;
}

/** Write-only secret: omit/blank `apiKey` to keep the existing key, a value rotates it. */
export interface UpdateModelCredentialInput {
  displayName: string;
  apiKey?: string | null;
  baseUrl?: string | null;
}

// ─── API client ────────────────────────────────────────────────────────────────

export const modelCredentialsApi = {
  list: (provider?: string) =>
    fetchJson<ModelCredentialSummary[]>(`/api/model-credentials${provider ? `?provider=${encodeURIComponent(provider)}` : ""}`),

  add: (input: AddModelCredentialInput) =>
    fetchJson<{ id: string }>("/api/model-credentials", { method: "POST", body: JSON.stringify(input) }),

  update: (id: string, input: UpdateModelCredentialInput) =>
    fetchJson<{ id: string }>(`/api/model-credentials/${id}`, { method: "PUT", body: JSON.stringify(input) }),

  revoke: (id: string) =>
    fetchJson<{ id: string }>(`/api/model-credentials/${id}/revoke`, { method: "POST" }),
};
