import { fetchJson } from "./request";

/** One installed storage provider type. Schemas describe future profile forms and never contain secret values. */
export interface StorageProviderModuleSummary {
  typeKey: string;
  displayName: string;
  configSchema: Record<string, unknown>;
  secretSchema: Record<string, unknown>;
  capabilities: string[];
}

export type StorageProfileState = "Draft" | "Active" | "Disabled" | "Retired";
export type StorageCredentialState = "Active" | "Revoked";

export interface StoragePage<T> {
  items: T[];
  nextCursor: string | null;
}

/** Safe metadata only. Secret JSON, ciphertext, and envelope fingerprints never cross this boundary. */
export interface StorageCredentialMetadata {
  id: string;
  stableName: string;
  state: StorageCredentialState;
  currentRevision: number;
  xmin: number;
  providerTypeKey: string;
  safeHint?: string | null;
  /** Opaque linkage used by profile commands; Settings must never render it. */
  credentialRef: string;
  createdDate: string;
  currentRevisionCreatedDate: string;
  revokedDate?: string | null;
}

export interface CreateStorageCredentialInput {
  stableName: string;
  providerTypeKey: string;
  secret: Record<string, unknown>;
  safeHint?: string;
}

export interface AppendStorageCredentialRevisionInput {
  expectedXmin: number;
  expectedCurrentRevision: number;
  providerTypeKey: string;
  secret: Record<string, unknown>;
  safeHint?: string;
}

export interface RevokeStorageCredentialInput {
  expectedXmin: number;
  expectedCurrentRevision: number;
}

export interface StorageProfileSummary {
  id: string;
  stableName: string;
  state: StorageProfileState;
  currentRevision: number;
  xmin: number;
  providerTypeKey: string;
  createdDate: string;
  lastModifiedDate: string;
}

export interface StorageProfileRevisionDetail {
  id: string;
  revision: number;
  providerTypeKey: string;
  nonSecretConfig: Record<string, unknown>;
  /** Opaque control-plane linkage. The Storage settings UI must never render this raw reference. */
  credentialRef?: string | null;
  namespaceFingerprint: string;
  createdDate: string;
  createdBy: string;
}

export interface StorageProfileDetail {
  id: string;
  stableName: string;
  state: StorageProfileState;
  currentRevision: number;
  xmin: number;
  createdDate: string;
  createdBy: string;
  lastModifiedDate: string;
  lastModifiedBy: string;
  revisions: StorageProfileRevisionDetail[];
}

export interface CreateStorageProfileInput {
  stableName: string;
  providerTypeKey: string;
  nonSecretConfig: Record<string, unknown>;
  credentialRef?: string;
}

export interface AppendStorageProfileRevisionInput {
  expectedXmin: number;
  expectedCurrentRevision: number;
  providerTypeKey: string;
  nonSecretConfig: Record<string, unknown>;
  /** Existing opaque linkage may be preserved without exposing or asking an operator for the raw reference. */
  credentialRef?: string;
}

export interface SetStorageProfileStateInput {
  expectedXmin: number;
  expectedCurrentRevision: number;
  state: Exclude<StorageProfileState, "Draft">;
}

export const storageApi = {
  listProviderModules: () => fetchJson<StorageProviderModuleSummary[]>("/api/storage/provider-modules"),
  listCredentials: () => fetchJson<StorageCredentialMetadata[]>("/api/storage/credentials"),
  listCredentialPage: (cursor: string | null, limit = 50, signal?: AbortSignal) => {
    const query = new URLSearchParams({ limit: String(limit) });
    if (cursor) query.set("cursor", cursor);
    return fetchJson<StoragePage<StorageCredentialMetadata>>(`/api/storage/credentials/page?${query}`, { signal });
  },
  createCredential: (input: CreateStorageCredentialInput) => fetchJson<StorageCredentialMetadata>("/api/storage/credentials", {
    method: "POST",
    body: JSON.stringify(input),
  }),
  appendCredentialRevision: (credentialId: string, input: AppendStorageCredentialRevisionInput) => fetchJson<StorageCredentialMetadata>(`/api/storage/credentials/${encodeURIComponent(credentialId)}/revisions`, {
    method: "POST",
    body: JSON.stringify(input),
  }),
  revokeCredential: (credentialId: string, input: RevokeStorageCredentialInput) => fetchJson<StorageCredentialMetadata>(`/api/storage/credentials/${encodeURIComponent(credentialId)}/revoke`, {
    method: "POST",
    body: JSON.stringify(input),
  }),
  listProfiles: () => fetchJson<StorageProfileSummary[]>("/api/storage/profiles"),
  listProfilePage: (cursor: string | null, limit = 50, signal?: AbortSignal) => {
    const query = new URLSearchParams({ limit: String(limit) });
    if (cursor) query.set("cursor", cursor);
    return fetchJson<StoragePage<StorageProfileSummary>>(`/api/storage/profiles/page?${query}`, { signal });
  },
  getProfile: (profileId: string) => fetchJson<StorageProfileDetail>(`/api/storage/profiles/${encodeURIComponent(profileId)}`),
  createProfile: (input: CreateStorageProfileInput) => fetchJson<StorageProfileDetail>("/api/storage/profiles", {
    method: "POST",
    body: JSON.stringify(input),
  }),
  appendProfileRevision: (profileId: string, input: AppendStorageProfileRevisionInput) => fetchJson<StorageProfileDetail>(`/api/storage/profiles/${encodeURIComponent(profileId)}/revisions`, {
    method: "POST",
    body: JSON.stringify(input),
  }),
  setProfileState: (profileId: string, input: SetStorageProfileStateInput) => fetchJson<StorageProfileDetail>(`/api/storage/profiles/${encodeURIComponent(profileId)}/state`, {
    method: "PUT",
    body: JSON.stringify(input),
  }),
};
