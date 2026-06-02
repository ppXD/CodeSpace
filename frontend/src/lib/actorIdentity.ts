import { ApiError } from "@/api/request";

/**
 * Parsed payload of the backend's 428 `actor_identity_required` response. The backend throws
 * `ActorIdentityRequiredException` whenever an operation must act AS the caller's own provider
 * identity (Model B) but they haven't linked one — `GlobalExceptionFilter` maps it to this shape so
 * a single frontend interceptor can open the link modal for the named provider instance and retry.
 */
export interface ActorIdentityRequired {
  /** The provider instance the caller must link an identity on. Drives the IdentityLinkModal. */
  providerInstanceId: string;
  /** Provider kind name (e.g. "GitHub", "GitLab") for the modal label. */
  provider: string;
  /** The backend's human-readable explanation. */
  message: string;
}

/**
 * Returns the actor-identity-required details when `error` is the backend's 428 signal, else null.
 * Pure + framework-free so the gate and call sites can branch on it without React.
 */
export function parseActorIdentityRequired(error: unknown): ActorIdentityRequired | null {
  if (!(error instanceof ApiError) || error.code !== "actor_identity_required") return null;

  const body = error.body as { providerInstanceId?: string; provider?: string; message?: string } | undefined;

  // The id is the one field the modal can't work without; bail if a forward-compat / malformed
  // body omits it rather than opening a modal that can't target an instance.
  if (!body?.providerInstanceId) return null;

  return {
    providerInstanceId: body.providerInstanceId,
    provider: body.provider ?? "your provider",
    message: body.message ?? error.message,
  };
}

/**
 * Parsed payload of the backend's 403 `actor_repo_permission_denied` response — the responder's
 * identity IS linked, but they can't act on the target repo (not a member / role too low / no access).
 * Unlike the 428 there's nothing to LINK; the fix is to get access, so the call site shows the reason
 * inline on the card (which stays open) instead of opening a modal.
 */
export interface ActorRepoPermissionDenied {
  /** Provider kind name (e.g. "GitLab") for context. */
  provider: string;
  /** The repo full path (e.g. "acme/web"). */
  repository: string;
  /** The backend's actionable reason — shown to the responder verbatim. */
  message: string;
}

/** Returns the repo-permission-denied details when `error` is the backend's 403 signal, else null. */
export function parseActorRepoPermissionDenied(error: unknown): ActorRepoPermissionDenied | null {
  if (!(error instanceof ApiError) || error.code !== "actor_repo_permission_denied") return null;

  const body = error.body as { provider?: string; repository?: string; message?: string } | undefined;

  return {
    provider: body?.provider ?? "the provider",
    repository: body?.repository ?? "this repository",
    message: body?.message ?? error.message,
  };
}
