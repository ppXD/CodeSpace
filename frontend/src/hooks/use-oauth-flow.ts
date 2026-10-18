import { useQueryClient } from "@tanstack/react-query";
import { useCallback } from "react";

import { ApiError, oauthApi, type InitOAuthRequest } from "@/api/oauth";
import type { InitOAuthResponse } from "@/api/types";

/**
 * Runs an OAuth Authorization Code + PKCE flow in a popup. Resolves with the freshly
 * persisted credential id on success; rejects with a typed error otherwise.
 *
 * Mechanism:
 *   1. Call POST /api/credentials/oauth/init → { authorizeUrl } — at the first return address the API accepts
 *   2. window.open(authorizeUrl) — popup with the provider's consent page
 *   3. After consent, provider hits backend /callback, backend hits frontend /oauth-callback — with the finished
 *      credential when the popup carried the binding cookie, or with the code and state to finish signed in when it
 *      could not (the API on another site, or a connect started on another host than the callback's), which
 *      /oauth-callback then does
 *   4. /oauth-callback posts { type: 'codespace:oauth-result' } back via window.opener
 *   5. This hook's message listener receives it, closes the popup, resolves
 *
 * Cancellation paths covered:
 *   • User closes popup manually → reject with `cancelled`
 *   • Init network call fails → reject with the ApiError
 *   • Popup blocked by browser → reject with `popup_blocked`
 */

const OAUTH_MESSAGE_TYPE = "codespace:oauth-result";
const CALLBACK_ROUTE = "/oauth-callback";
const POPUP_FEATURES = "width=600,height=720,resizable=yes,scrollbars=yes,status=yes";
const POPUP_POLL_INTERVAL_MS = 500;

export type OAuthFlowOutcome =
  | { status: "success"; credentialId: string }
  | { status: "error"; error: string; description?: string };

interface OAuthFlowMessage {
  type: typeof OAUTH_MESSAGE_TYPE;
  status: "success" | "error";
  credentialId?: string;
  error?: string;
  description?: string;
}

export class OAuthFlowError extends Error {
  readonly code: "popup_blocked" | "cancelled" | "init_failed" | "provider_error" | "timeout";
  readonly providerError?: string;

  constructor(code: "popup_blocked" | "cancelled" | "init_failed" | "provider_error" | "timeout", message: string, providerError?: string) {
    super(message);
    this.name = "OAuthFlowError";
    this.code = code;
    this.providerError = providerError;
  }
}

/** What a failed connect says to the person who started it — shared by every screen that starts one (the Providers dialog, a remediation card). */
export function describeOAuthFlowError(err: OAuthFlowError): string {
  switch (err.code) {
    case "popup_blocked": return "Your browser blocked the OAuth popup. Allow popups for this site and try again.";
    case "cancelled":     return "You closed the authorization window. No connection was made.";
    case "init_failed":   return `Couldn't start the OAuth flow: ${err.message}`;
    case "provider_error": return err.providerError ? `Provider rejected: ${err.providerError}` : err.message;
    case "timeout":       return "The authorization took too long. Try again.";
    default:              return err.message;
  }
}

interface RunInput extends Omit<InitOAuthRequest, "returnUrl"> {
  /** Optional override; defaults to the in-app `/oauth-callback` route on this page's origin (see `oauthReturnUrlsFor`). */
  returnUrl?: string;
}

/** The answer both API builds give, before starting anything, to an argument they refuse — a return address they do not accept among them. */
const REFUSED_ARGUMENT = "invalid_request";

/**
 * Where a connect may bring the popup back to, in the order to ask. This page's own origin first: a connect whose
 * callback cannot find the binding cookie — the API on another site, or a connect started on another host than
 * OAuth:CallbackUrl names — is handed to the completion page on the origin the connect named, and only this origin
 * holds the session and the opener that finish it. Vite prints http://127.0.0.1:5180 while the callback names
 * localhost:5180; a path there brought the popup back to localhost, signed out and unheard by this page.
 *
 * A path is the fallback when the API answers on this page's own origin (the dev proxy, or one host fronting both): it
 * needs no origin configured on the API, so a page on an alias the API does not list — or an API pod from before it
 * read Cors:AllowedOrigins, which matched App:PublicBaseUrl alone — can still start a connect. It finishes on the
 * callback's own host, which is this page's only when this page is on that host. In direct mode (VITE_API_URL on another
 * origin) the callback runs on the API's host and a path would land the popup there; this page's own origin, which the
 * API lists in Cors:AllowedOrigins for the page to reach it at all, is the one address.
 */
export function oauthReturnUrlsFor(pageOrigin: string, apiBase: string): string[] {
  const apiOrigin = new URL(apiBase || "/", pageOrigin).origin;
  const ownOrigin = `${pageOrigin}${CALLBACK_ROUTE}`;

  return apiOrigin === pageOrigin ? [ownOrigin, CALLBACK_ROUTE] : [ownOrigin];
}

/**
 * Starts the connect at the first return address the API accepts. Only a refused argument moves on to the next: both API
 * builds refuse a return address that way before starting anything, and the return address is the one argument that
 * differs between tries, so any other answer — or the last refusal — is the outcome.
 */
export async function initAtFirstAcceptedReturnUrl(request: Omit<InitOAuthRequest, "returnUrl">, [returnUrl, ...fallbacks]: string[]): Promise<InitOAuthResponse> {
  try {
    return await oauthApi.initOAuth({ ...request, returnUrl });
  } catch (err) {
    if (fallbacks.length === 0 || !isRefusedArgument(err)) throw err;

    return initAtFirstAcceptedReturnUrl(request, fallbacks);
  }
}

function isRefusedArgument(err: unknown): boolean {
  return err instanceof ApiError && err.status === 400 && err.code === REFUSED_ARGUMENT;
}

export function useOAuthFlow() {
  const queryClient = useQueryClient();

  return useCallback(async ({ returnUrl, ...request }: RunInput): Promise<{ credentialId: string }> => {
    const returnUrls = returnUrl ? [returnUrl] : oauthReturnUrlsFor(window.location.origin, import.meta.env.VITE_API_URL ?? "");

    const init = await initOrThrow(request, returnUrls);

    const popup = window.open(init.authorizeUrl, "codespace-oauth", POPUP_FEATURES);
    if (!popup) throw new OAuthFlowError("popup_blocked", "Popup blocked by the browser. Allow popups for this site and try again.");

    try {
      const outcome = await awaitOutcome(popup);
      // A successful OAuth lands a new credential row — or, for a reconnect that named one (`replaceCredentialId`),
      // brings the same row back to life along with the repositories its dead token had turned Error. Invalidate so any
      // open modal / sidebar showing connection status re-fetches immediately rather than waiting for the next mount.
      await queryClient.invalidateQueries({ queryKey: ["credentials"] });
      await queryClient.invalidateQueries({ queryKey: ["repositories"] });
      // A run page that offered this reconnect re-reads its remedy, so the card turns to "run it again" instead of waiting for a focus change.
      await queryClient.invalidateQueries({ queryKey: ["run-remedy"] });
      await queryClient.invalidateQueries({ queryKey: ["repository"] });
      return outcome;
    } finally {
      try { if (!popup.closed) popup.close(); } catch { /* cross-origin during provider visit; harmless */ }
    }
  }, [queryClient]);
}

async function initOrThrow(request: Omit<InitOAuthRequest, "returnUrl">, returnUrls: string[]) {
  try {
    return await initAtFirstAcceptedReturnUrl(request, returnUrls);
  } catch (err) {
    if (err instanceof ApiError) throw new OAuthFlowError("init_failed", err.message);
    throw new OAuthFlowError("init_failed", err instanceof Error ? err.message : "Failed to start OAuth flow");
  }
}

function awaitOutcome(popup: Window): Promise<{ credentialId: string }> {
  return new Promise((resolve, reject) => {
    let settled = false;

    const cleanup = () => {
      window.removeEventListener("message", onMessage);
      window.clearInterval(closeWatcher);
    };

    const onMessage = (event: MessageEvent) => {
      // Strict origin check — the callback route runs on this exact origin.
      if (event.origin !== window.location.origin) return;

      const data = event.data as OAuthFlowMessage | undefined;
      if (data?.type !== OAUTH_MESSAGE_TYPE) return;

      settled = true;
      cleanup();

      if (data.status === "success" && data.credentialId) {
        resolve({ credentialId: data.credentialId });
      } else {
        reject(new OAuthFlowError("provider_error", data?.description ?? data?.error ?? "Authorization failed", data?.error));
      }
    };

    const closeWatcher = window.setInterval(() => {
      // popup.closed is the only cross-origin-safe property — the rest is locked while
      // the user is on the provider's domain. If it flips to true without a message,
      // the user dismissed the window.
      if (popup.closed && !settled) {
        settled = true;
        cleanup();
        reject(new OAuthFlowError("cancelled", "Authorization window was closed before completing."));
      }
    }, POPUP_POLL_INTERVAL_MS);

    window.addEventListener("message", onMessage);
  });
}
