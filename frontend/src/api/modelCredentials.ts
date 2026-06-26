import { fetchJson } from "./request";
import type { CredentialStatus } from "./types";

// ─── Types (mirror backend ModelCredential DTOs) ────────────────────────────────

/**
 * Mirrors backend `ModelCredentialSummary` — a team's model credential as shown in settings. NEVER carries
 * the secret: `keyHint` is the masked tail (e.g. `····a1b2`), null for a keyless provider OR a dead key.
 * `keyUnreadable` disambiguates the two. `baseUrl` is non-secret config and is shown verbatim.
 */
export interface ModelCredentialSummary {
  id: string;
  teamId: string;
  provider: string;
  displayName: string;
  keyHint: string | null;
  /** A stored key that can no longer be decrypted (key-ring rotated/lost/migrated) — needs re-entry. */
  keyUnreadable: boolean;
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

  /** The models a single credential exposes (mirror of backend CredentialedModelSummary). */
  listModels: (credentialId: string) =>
    fetchJson<CredentialedModelSummary[]>(`/api/model-credentials/${encodeURIComponent(credentialId)}/models`),

  /** Manually add a model id to a credential's maintained list (the "type a model id" half). */
  addModel: (credentialId: string, input: AddCredentialedModelInput) =>
    fetchJson<{ id: string }>(`/api/model-credentials/${encodeURIComponent(credentialId)}/models`, { method: "POST", body: JSON.stringify(input) }),

  /** Remove one model row from a credential by its row id. */
  removeModel: (credentialId: string, modelRowId: string) =>
    fetchJson<{ id: string }>(`/api/model-credentials/${encodeURIComponent(credentialId)}/models/${encodeURIComponent(modelRowId)}`, { method: "DELETE" }),

  /** Reflect the credential's endpoint and refresh its model list (the "auto-suggest" half). Returns the count. */
  refreshModels: (credentialId: string) =>
    fetchJson<{ refreshed: number }>(`/api/model-credentials/${encodeURIComponent(credentialId)}/models/refresh`, { method: "POST" }),
};

/** Body for adding a model to a credential (mirror of backend AddCredentialedModelCommand). */
export interface AddCredentialedModelInput {
  modelId: string;
  displayName?: string | null;
}

/** One model a credential can authenticate (mirror of backend CredentialedModelSummary). */
export interface CredentialedModelSummary {
  /** The model-row id (not the model id) — used to pin model+credential together. */
  id: string;
  modelId: string;
  displayName?: string | null;
  enabled: boolean;
}
