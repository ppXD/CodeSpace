import type { RoomFilePreview } from "@/api/sessions";
import { ApiError } from "@/api/request";

/**
 * The lanes the artifact plane itself can fail in — the backend's ArtifactContentUnavailableKind, a subset of the
 * Room's file reasons. A runtime list because it is also the guard that narrows an untrusted error body.
 */
export const STORAGE_UNAVAILABLE_REASONS = ["MetadataMissing", "PhysicalObjectMissing", "IntegrityFailure", "BackendUnavailable", "AccessDenied"] as const;

export type StorageUnavailableReason = (typeof STORAGE_UNAVAILABLE_REASONS)[number];

/**
 * The reason a failed artifact fetch carried, or null when it carried none.
 *
 * <p>Read off the error BODY rather than guessed from the status: the storage plane distinguishes five lanes and a
 * client that invents one sends the operator to fix the wrong thing. Anything unrecognised is null, because a
 * sentence the server did not authorise is worse than no sentence.</p>
 */
export function artifactUnavailableReason(error: unknown): StorageUnavailableReason | null {
  const body = error instanceof ApiError ? error.body : null;
  const reason = body !== null && typeof body === "object" ? (body as Record<string, unknown>).reason : null;

  return STORAGE_UNAVAILABLE_REASONS.includes(reason as StorageUnavailableReason) ? reason as StorageUnavailableReason : null;
}

/**
 * Why a file could not be shown, in words that say what to do about it.
 *
 * <p>The server distinguishes eight reasons and the client read none of them, so a purged artifact, a revoked storage
 * credential and a file that simply is not in the change set all rendered as one sentence. Those call for three
 * different actions — restore or accept the loss, fix the credential, and nothing at all — and a reader who cannot
 * tell them apart will go looking in the wrong place.</p>
 *
 * <p>Exhaustive by CASE so a reason added later fails to compile here rather than silently collapsing into the
 * fallback, which is exactly how this gap appeared in the first place.</p>
 */
export function roomFileUnavailableNote(reason: NonNullable<RoomFilePreview["unavailableReason"]>): string {
  switch (reason) {
    case "NotInChangeSet":
      return "This file isn't part of this turn's changes.";
    case "AmbiguousRepository":
      return "More than one repository in this run changed a file at this path, so there's no single version to show.";
    case "MetadataMissing":
      return "This run recorded no saved copy of the change, so there's nothing to reconstruct the file from.";
    case "PhysicalObjectMissing":
      return "The stored copy is gone from the storage destination. It may have been cleaned up, or removed at the provider.";
    case "IntegrityFailure":
      return "The stored copy came back but did not match what was recorded, so it was refused rather than shown.";
    case "BackendUnavailable":
      return "The storage destination did not respond. Check its health in Settings → Storage; recent runs may be affected too.";
    case "AccessDenied":
      return "The storage destination refused access. The credential it uses is likely expired or revoked — check Settings → Storage.";
    case "ReconstructionUnavailable":
      return "The change was saved, but this file's contents could not be rebuilt from it.";
  }
}

/**
 * The note to show for an unavailable preview. The server's own prose wins when it sent any: it can say things the
 * client cannot know. This fills the far more common case where it sent only a reason.
 */
export function roomFileNote(file: RoomFilePreview): string {
  if (file.note) return file.note;
  if (file.unavailableReason) return roomFileUnavailableNote(file.unavailableReason);

  return "Preview isn't available for this file.";
}
