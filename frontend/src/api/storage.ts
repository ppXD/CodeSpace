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

/** This slice intentionally has no credentialRef input; newly-created profiles are always credentialless Drafts. */
export interface CreateStorageProfileInput {
  stableName: string;
  providerTypeKey: string;
  nonSecretConfig: Record<string, unknown>;
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
  listProfiles: () => fetchJson<StorageProfileSummary[]>("/api/storage/profiles"),
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
