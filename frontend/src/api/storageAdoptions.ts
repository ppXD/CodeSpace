import { fetchJson } from "@/api/request";

/**
 * Where one team stands on the deployment's default for one routed data class.
 *
 * `canAdopt` is computed by the server, not here. The rule it encodes — a default exists, is enabled,
 * and nothing already claims the class — also lives in the materializer, and a screen that re-derived
 * it would eventually offer a button that refuses, or hide one that would have worked.
 */
export interface StorageAdoptionStatus {
  dataClassTypeKey: string;
  displayName: string;
  defaultAvailable: boolean;
  adopted: boolean;
  teamOwnsRoute: boolean;
  canAdopt: boolean;
  /** Adopting takes this class off a durable home it has now, permanently. Say so before asking. */
  adoptionIsIrreversible: boolean;
  sourceRevision: number | null;
  templateRevision: number | null;
}

/**
 * Every outcome is named. The server answers 200 for all of them because a screen renders "the
 * deployment authored nothing", "you already adopted this" and "the destination refused a write"
 * three different ways, and a status code would collapse them into one apology.
 */
export type StorageAdoptionOutcome =
  | "Adopted"
  | "AlreadyAdopted"
  | "TeamOwnsRoute"
  | "NoTemplate"
  | "TemplateDisabled"
  | "DestinationUnusable"
  | "RaceLost";

export interface StorageAdoptionResult {
  outcome: StorageAdoptionOutcome;
  storageProfileId: string | null;
  storageRouteId: string | null;
  sourceRevision: number | null;
  /** What the provider answered, for an outcome that has one. Today: why a destination was refused. */
  detail: string | null;
}

export const storageAdoptionApi = {
  list: (signal?: AbortSignal) => fetchJson<StorageAdoptionStatus[]>("/api/storage/adoptions", { signal }),
  adopt: (dataClassTypeKey: string) => fetchJson<StorageAdoptionResult>("/api/storage/adoptions", {
    method: "POST",
    body: JSON.stringify({ dataClassTypeKey }),
  }),
};
