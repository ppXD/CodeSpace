import { fetchJson } from "@/api/request";

/** A template's adoption policy, as the wire carries it. */
export type StorageDefaultAdoptionPolicy = "Automatic" | "Explicit";

export interface StorageDefaultSummary {
  id: string;
  dataClassTypeKey: string;
  revision: number;
  providerTypeKey: string;
  adoptionPolicy: StorageDefaultAdoptionPolicy;
  isEnabled: boolean;
  hasCredential: boolean;
  credentialSafeHint: string | null;
  /** Optimistic-concurrency token. Sent back on every edit so two operators cannot silently overwrite each other. */
  xmin: number;
  createdDate: string;
  lastModifiedDate: string;
}

export interface StorageDefaultDetail extends StorageDefaultSummary {
  nonSecretConfig: Record<string, unknown>;
  /** A ROOT, never a finished namespace: the server appends a per-team segment before it reaches any team. */
  namespaceRoot: string;
}

export interface CreateStorageDefaultInput {
  dataClassTypeKey: string;
  providerTypeKey: string;
  nonSecretConfig: Record<string, unknown>;
  namespaceRoot: string;
  adoptionPolicy: StorageDefaultAdoptionPolicy;
  isEnabled: boolean;
  secret?: Record<string, unknown>;
}

export interface UpdateStorageDefaultInput {
  expectedXmin: number;
  expectedRevision: number;
  providerTypeKey: string;
  nonSecretConfig: Record<string, unknown>;
  namespaceRoot: string;
  adoptionPolicy: StorageDefaultAdoptionPolicy;
  secret?: Record<string, unknown>;
  safeHint?: string;
  clearCredential?: boolean;
}

export interface SetStorageDefaultEnabledInput {
  expectedXmin: number;
  expectedRevision: number;
  isEnabled: boolean;
}

/**
 * Deliberately NOT under `/api/storage`. The request client injects `X-Team-Id` from local storage into
 * every call, and nothing clears it for a non-team route — so an admin surface calling a team-scoped
 * controller would write into whichever team the operator last visited. The separate path keeps that
 * header inert: nothing behind these endpoints reads a team.
 */
export const storageDefaultsApi = {
  list: (signal?: AbortSignal) => fetchJson<StorageDefaultSummary[]>("/api/admin/storage-defaults", { signal }),
  get: (defaultId: string, signal?: AbortSignal) => fetchJson<StorageDefaultDetail>(`/api/admin/storage-defaults/${encodeURIComponent(defaultId)}`, { signal }),
  create: (input: CreateStorageDefaultInput) => fetchJson<StorageDefaultDetail>("/api/admin/storage-defaults", {
    method: "POST",
    body: JSON.stringify(input),
  }),
  update: (defaultId: string, input: UpdateStorageDefaultInput) => fetchJson<StorageDefaultDetail>(`/api/admin/storage-defaults/${encodeURIComponent(defaultId)}`, {
    method: "PUT",
    body: JSON.stringify(input),
  }),
  setEnabled: (defaultId: string, input: SetStorageDefaultEnabledInput) => fetchJson<StorageDefaultDetail>(`/api/admin/storage-defaults/${encodeURIComponent(defaultId)}/enabled`, {
    method: "PUT",
    body: JSON.stringify(input),
  }),
};
