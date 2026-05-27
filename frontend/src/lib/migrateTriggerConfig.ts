/**
 * Trigger-config normalisation.
 *
 * <h3>Why</h3>
 * The PR-trigger config schema upgraded in #23 from
 *   <c>{ repositoryId, labels? }</c>  (single repo)
 * to
 *   <c>{ repositories: [{ repositoryId, labels? }, ...] }</c>  (per-repo label sets).
 * The matcher accepts both shapes via a backward-compat shim, but the inspector UI
 * needs ONE shape to render against. This helper normalises whatever shape is on disk
 * into the array form so the picker component always works with a homogeneous payload.
 *
 * <h3>Auto-migration</h3>
 * On first edit, the picker emits a save back in the array form. So a workflow stored
 * with the legacy shape transparently migrates the next time an operator touches the
 * trigger — no offline data migration job needed.
 *
 * <h3>Defensive</h3>
 * Any malformed / unrecognised input returns <c>{ repositories: [] }</c>. The picker
 * renders an empty list; the operator can add entries from there. Never throws.
 */

export interface TriggerRepoEntry {
  repositoryId: string;
  labels?: string[];
}

export interface TriggerConfigArrayShape {
  repositories: TriggerRepoEntry[];
}

export function migrateLegacyTriggerConfig(raw: unknown): TriggerConfigArrayShape {
  if (raw == null || typeof raw !== "object") return { repositories: [] };

  const obj = raw as Record<string, unknown>;

  // New shape wins when both keys are present — matches the matcher's precedence in
  // PrTriggerMatcherFilter (the new `repositories` array takes precedence over any
  // legacy top-level `repositoryId`).
  if (Array.isArray(obj.repositories)) {
    return { repositories: obj.repositories.flatMap(normaliseEntry) };
  }

  // Legacy single-repo shape — promote to a one-entry array. Skips the promotion when
  // the legacy id is missing OR empty: an empty-string legacy id is indistinguishable
  // from "user opened the inspector but never picked a repo", and the new shape's
  // empty list models that more honestly.
  if (typeof obj.repositoryId === "string" && obj.repositoryId.length > 0) {
    const labels = normaliseLabels(obj.labels);
    return {
      repositories: [labels.length > 0
        ? { repositoryId: obj.repositoryId, labels }
        : { repositoryId: obj.repositoryId }],
    };
  }

  // Neither shape recognised — return an empty list rather than throw. The picker can
  // recover from this; throwing here would break the inspector for any workflow whose
  // config got corrupted upstream.
  return { repositories: [] };
}

/**
 * Reverse direction — used by the picker when emitting onChange. Strips empty labels
 * arrays so the wire format stays minimal (`{ repositoryId: "..." }` instead of
 * `{ repositoryId: "...", labels: [] }`) — matters because the matcher treats absent
 * `labels` and empty `labels` identically; we prefer absent for clean diffs.
 */
export function normaliseTriggerConfigForSave(shape: TriggerConfigArrayShape): TriggerConfigArrayShape {
  return {
    repositories: shape.repositories
      .filter((e) => e.repositoryId.length > 0)
      .map((e) => (e.labels && e.labels.length > 0 ? { repositoryId: e.repositoryId, labels: e.labels } : { repositoryId: e.repositoryId })),
  };
}

/**
 * Permissive: keeps entries whose <c>repositoryId</c> key exists and is a string,
 * INCLUDING empty strings (the in-progress "user just clicked Add but hasn't picked"
 * state). Save-time normalisation via <see cref="normaliseTriggerConfigForSave"/>
 * strips the empty ones when the operator hits save. Drops entries that are clearly
 * wrong: nulls, non-object array items, objects without a repositoryId key, objects
 * whose repositoryId isn't a string.
 */
function normaliseEntry(entry: unknown): TriggerRepoEntry[] {
  if (entry == null || typeof entry !== "object") return [];

  const obj = entry as Record<string, unknown>;
  if (typeof obj.repositoryId !== "string") return [];

  const labels = normaliseLabels(obj.labels);
  return [labels.length > 0
    ? { repositoryId: obj.repositoryId, labels }
    : { repositoryId: obj.repositoryId }];
}

function normaliseLabels(raw: unknown): string[] {
  if (!Array.isArray(raw)) return [];
  const out: string[] = [];
  for (const item of raw) {
    if (typeof item === "string" && item.length > 0) out.push(item);
  }
  return out;
}
