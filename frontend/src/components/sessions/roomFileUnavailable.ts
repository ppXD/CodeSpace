import type { RoomFilePreview } from "@/api/sessions";

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
