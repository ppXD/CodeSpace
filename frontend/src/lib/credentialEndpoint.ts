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

function parseHttp(url: string | null | undefined): URL | null {
  try {
    const parsed = new URL((url ?? "").trim());
    return parsed.protocol === "http:" || parsed.protocol === "https:" ? parsed : null;
  } catch {
    return null;
  }
}

/**
 * Whether `next` is `original` with its scheme moved from http to https and nothing else: the same host (case aside) and the
 * same port — each scheme's own default (80 then 443), or one explicit port both spell. Mirrors the backend's
 * `CredentialUrl.IsHttpsUpgrade`. A git provider connection may make exactly this move while tokens are connected through
 * it (it sends them nowhere new, only over TLS); a model credential may not.
 */
export function isHttpsUpgrade(original: string | null | undefined, next: string | null | undefined): boolean {
  const from = parseHttp(original);
  const to = parseHttp(next);

  if (from === null || to === null || from.protocol !== "http:" || to.protocol !== "https:") return false;

  // `URL.port` is "" for the scheme's own default port, so 80 to 443 and an unspelled port on both sides compare equal.
  return from.hostname.toLowerCase() === to.hostname.toLowerCase() && from.port === to.port;
}

/** Whether saving `next` moves a git provider connection to another endpoint — a change of scheme, host or port, bar an http to https upgrade of the same host. */
export function providerEndpointMoves(original: string | null | undefined, next: string | null | undefined): boolean {
  return endpointChanged(original, next) && !isHttpsUpgrade(original, next);
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
 * edit form changes only the base URL, so this is the backend's `ProviderInstanceService.EndpointMoves` for that one URL —
 * which leaves an http to https upgrade of the same host alone.
 */
export function providerSecretMustBeReentered(instance: Pick<ProviderInstanceSummary, "oauthEnabled" | "baseUrl">, nextBaseUrl: string): boolean {
  return instance.oauthEnabled && providerEndpointMoves(instance.baseUrl, nextBaseUrl);
}
