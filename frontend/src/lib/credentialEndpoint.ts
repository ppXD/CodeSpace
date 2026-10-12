import type { ModelCredentialSummary } from "@/api/modelCredentials";
import type { ProviderInstanceSummary } from "@/api/types";

/**
 * The endpoint a stored secret is bound to — scheme, host and port, i.e. where the secret is sent. Mirrors the backend
 * rule (`CredentialUrl.SameEndpoint`): a model credential's update that moves it must carry the key again
 * (`model_endpoint_key_required`), and a git provider's must carry its OAuth client secret again
 * (`provider_endpoint_secret_required`) and is refused outright while tokens are connected through it
 * (`provider_endpoint_credentials_bound`). Path, query and userinfo are not part of it, so a path-only edit (or the
 * masked `***` credentials the lists show) never counts as a move.
 */
function endpointOf(baseUrl: string | null | undefined): string | null {
  const trimmed = baseUrl?.trim() ?? "";
  if (trimmed === "") return null;

  try {
    const url = new URL(trimmed);
    if (url.protocol !== "http:" && url.protocol !== "https:") return trimmed;

    return `${url.protocol}//${url.hostname.toLowerCase()}:${url.port || (url.protocol === "https:" ? "443" : "80")}`;
  } catch {
    return trimmed;   // not an absolute http(s) URL — it only matches itself
  }
}

/** Whether `next` sends a key somewhere other than `original` does. Blank on both sides is the provider default on both. */
export function endpointChanged(original: string | null | undefined, next: string | null | undefined): boolean {
  return endpointOf(original) !== endpointOf(next);
}

/** Whether saving `nextBaseUrl` on this credential needs the key typed again: a key is stored (readable or not) and the endpoint moves. */
export function keyMustBeReentered(credential: Pick<ModelCredentialSummary, "keyHint" | "keyUnreadable" | "baseUrl">, nextBaseUrl: string | null | undefined): boolean {
  const hasStoredKey = credential.keyHint != null || credential.keyUnreadable;

  return hasStoredKey && endpointChanged(credential.baseUrl, nextBaseUrl);
}

/** Whether a URL the list showed still carries masked (`***`) embedded credentials — sent back unchanged it keeps them; edited, they must be typed in full. */
export function showsMaskedCredentials(url: string | null | undefined): boolean {
  return url?.includes("***") ?? false;
}

/**
 * Whether saving `nextBaseUrl` on a git provider connection moves it to another host, which its stored OAuth client
 * secret may not follow: the save needs the secret (the new host's OAuth app's, with its client ID) typed again. The
 * edit form changes only the base URL, so this is the backend's `ProviderInstanceService.EndpointMoves` for that one URL.
 */
export function providerSecretMustBeReentered(instance: Pick<ProviderInstanceSummary, "oauthEnabled" | "baseUrl">, nextBaseUrl: string): boolean {
  return instance.oauthEnabled && endpointChanged(instance.baseUrl, nextBaseUrl);
}
