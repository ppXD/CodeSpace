import { fetchJson } from "./request";

/** One installed storage provider type. Schemas describe future profile forms and never contain secret values. */
export interface StorageProviderModuleSummary {
  typeKey: string;
  displayName: string;
  configSchema: Record<string, unknown>;
  secretSchema: Record<string, unknown>;
  capabilities: string[];
  /** The config property that carries this provider's namespace, or null when it cannot subdivide one — and so cannot be a deployment default. */
  teamNamespaceProperty: string | null;
}

export type StorageProfileState = "Draft" | "Active" | "Disabled" | "Retired";
export type StorageCredentialState = "Active" | "Revoked";
export type StorageProfileProbeStatus = "Available" | "ReadOnly" | "Degraded" | "Unavailable" | "Cancelled";
export type StorageProfileProbeFailureStage = "Profile" | "Credential" | "Provider" | "Configuration" | "DriverInitialization" | "Probe" | "Cancellation" | "DriverCleanup";
export type StorageProfileProbeFailureCode =
  | "ProfileMissing"
  | "ProfileNotActive"
  | "ProfileRevisionMissing"
  | "ProfileRevisionInvalid"
  | "ProfileResolutionFailed"
  | "CredentialMissing"
  | "CredentialNotActive"
  | "CredentialRevisionMissing"
  | "CredentialProviderMismatch"
  | "CredentialProviderUnavailable"
  | "CredentialEnvelopeInvalid"
  | "CredentialReferenceInvalid"
  | "CredentialSecretInvalid"
  | "CredentialResolutionFailed"
  | "ProviderModuleMissing"
  | "ProviderFactoryMissing"
  | "ProviderFactoryMismatch"
  | "ProviderCatalogFailure"
  | "ConfigurationInvalid"
  | "ConfigurationSchemaUnsupported"
  | "SnapshotIdentityMismatch"
  | "ProviderTypeKeyInvalid"
  | "FactoryRejectedConfiguration"
  | "DriverNull"
  | "DriverProviderCancelled"
  | "DriverProviderFailure"
  | "DriverCleanupFailure"
  | "CancelledProfileResolution"
  | "CancelledCredentialResolution"
  | "CancelledDriverInitialization"
  | "CancelledProbe"
  | "ProbeInvalidRequest"
  | "ProbeMissing"
  | "ProbeAlreadyExists"
  | "ProbeConditionNotMet"
  | "ProbeIntegrityMismatch"
  | "ProbeCorrupt"
  | "ProbeUnauthorized"
  | "ProbeForbidden"
  | "ProbeCredentialInvalid"
  | "ProbeSignatureMismatch"
  | "ProbeSecurityTokenInvalid"
  | "ProbeSecurityTokenExpired"
  | "ProbeSecurityTokenMissing"
  | "ProbeClockSkew"
  | "ProbeDestinationMissing"
  | "ProbePermissionDenied"
  | "ProbeNetworkUnavailable"
  | "ProbeThrottled"
  | "ProbeUnavailable"
  | "ProbeUnsupported"
  | "ProbeProviderFailure";

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

/**
 * What a probe last saw at a profile's destination.
 *
 * `null` on the profile is a real answer and must be rendered as one: nobody has checked is not working.
 * The distinction matters most right after a profile is created, when nothing has probed it yet.
 */
export interface StorageProfileHealthSummary {
  status: StorageProfileProbeStatus;
  /** True only when the probe PUT and discarded a real object. A passing read-only probe qualifies reachability, not that a run's bytes will land. */
  writeVerified: boolean;
  /** The revision that was exercised. Behind the profile's current revision means this describes a destination the profile has since left. */
  profileRevision: number;
  failureStage?: StorageProfileProbeFailureStage | null;
  failureCode?: StorageProfileProbeFailureCode | null;
  latencyMilliseconds: number;
  observedAt: string;
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
  /** Null when nothing has ever probed this destination. */
  health?: StorageProfileHealthSummary | null;
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

export interface ProbeStorageProfileInput {
  profileRevision: number | null;
  verifyWriteAccess: boolean;
}

export interface StorageProfileProbeResult {
  profileId: string;
  profileRevision: number | null;
  writeAccessRequested: boolean;
  status: StorageProfileProbeStatus;
  latencyMilliseconds: number;
  failure?: {
    stage: StorageProfileProbeFailureStage;
    code: StorageProfileProbeFailureCode;
    retryable: boolean;
  } | null;
}

/**
 * Whether the artifact bytes this team already wrote are still at their destinations.
 *
 * <p>A different question from profile health, which describes whether a destination answers right now. A
 * destination can answer perfectly while objects written to it last year are gone.</p>
 */
export interface PlacementIntegritySummary {
  /** Placements the destination no longer holds — work a reader will not get back. */
  missing: number;
  /** Placements whose destination now holds something that is not the recorded object. */
  corrupt: number;
  /** Placements believed good; the population the two counts above are read against. */
  available: number;
  /** When the least recently confirmed placement was last known good, or null when nothing is stored. */
  oldestVerifiedAt: string | null;
}

/** What a placement's record says about the bytes it names. */
export type ArtifactLocationState = "Pending" | "Available" | "Missing" | "Corrupt" | "Deleting" | "Deleted" | "Failed" | "Purged";

/** One placement still recorded under a storage profile. */
export interface ProfilePlacementSummary {
  locationId: string;
  artifactObjectId: string;
  state: ArtifactLocationState;
  objectKey: string;
  /** Which revision of the profile placed it. A profile that has been re-pointed holds rows under several. */
  profileRevision: number;
  sizeBytes?: number | null;
  verifiedAt?: string | null;
  lastErrorCode?: string | null;
}

/** How many placements a profile holds in one state, and how many bytes they account for. */
export interface ProfilePlacementTotal {
  state: ArtifactLocationState;
  count: number;
  sizeBytes: number;
}

/** What one abandonment pass established about one placement. */
export type ProfilePlacementAbandonOutcome = "Abandoned" | "StillServed" | "Unanswered";

export interface ProfilePlacementOutcome {
  locationId: string;
  objectKey: string;
  outcome: ProfilePlacementAbandonOutcome;
  /** What the destination answered, or null when nothing was asked because the claim was held elsewhere. */
  detail?: string | null;
}

/** What one bounded pass of abandoning a profile's placements did. */
export interface ProfileAbandonmentSummary {
  examined: number;
  abandoned: number;
  /** Placements the destination SERVED. Left exactly as they were — the refusal that makes the operation safe. */
  stillServed: number;
  /** Placements whose destination gave no usable answer. A revoked key or an unmounted volume lands here. */
  unanswered: number;
  /** Unreleased placements still under the profile after this pass. */
  remaining: number;
  /** The problem code that stopped the pass before its batch was done, or null when the whole batch was examined. */
  stoppedBy?: string | null;
  outcomes: ProfilePlacementOutcome[];
}

export interface AbandonProfilePlacementsInput {
  batchSize: number;
}

export const storageApi = {
  listProviderModules: () => fetchJson<StorageProviderModuleSummary[]>("/api/storage/provider-modules"),
  getPlacementIntegrity: (signal?: AbortSignal) => fetchJson<PlacementIntegritySummary>("/api/storage/placements/integrity", { signal }),
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
  probeProfile: (profileId: string, input: ProbeStorageProfileInput, signal?: AbortSignal) => fetchJson<StorageProfileProbeResult>(`/api/storage/profiles/${encodeURIComponent(profileId)}/probe`, {
    method: "POST",
    body: JSON.stringify(input),
    signal,
  }),
  getProfilePlacementTotals: (profileId: string, signal?: AbortSignal) => fetchJson<ProfilePlacementTotal[]>(`/api/storage/profiles/${encodeURIComponent(profileId)}/placements/totals`, { signal }),
  listProfilePlacementPage: (profileId: string, cursor: string | null, limit = 50, signal?: AbortSignal) => {
    const query = new URLSearchParams({ limit: String(limit) });
    if (cursor) query.set("cursor", cursor);
    return fetchJson<StoragePage<ProfilePlacementSummary>>(`/api/storage/profiles/${encodeURIComponent(profileId)}/placements?${query}`, { signal });
  },
  abandonProfilePlacements: (profileId: string, input: AbandonProfilePlacementsInput) => fetchJson<ProfileAbandonmentSummary>(`/api/storage/profiles/${encodeURIComponent(profileId)}/placements/abandon`, {
    method: "POST",
    body: JSON.stringify(input),
  }),
};
