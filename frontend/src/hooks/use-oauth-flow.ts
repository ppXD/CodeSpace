import { useQueryClient } from "@tanstack/react-query";
import { useCallback } from "react";

import { ApiError, oauthApi, type InitOAuthRequest } from "@/api/oauth";

/**
 * Runs an OAuth Authorization Code + PKCE flow in a popup. Resolves with the freshly
 * persisted credential id on success; rejects with a typed error otherwise.
 *
 * Mechanism:
 *   1. Call POST /api/credentials/oauth/init → { authorizeUrl }
 *   2. window.open(authorizeUrl) — popup with the provider's consent page
 *   3. After consent, provider hits backend /callback, backend hits frontend /oauth-callback
 *   4. /oauth-callback posts { type: 'codespace:oauth-result' } back via window.opener
 *   5. This hook's message listener receives it, closes the popup, resolves
 *
 * Cancellation paths covered:
 *   • User closes popup manually → reject with `cancelled`
 *   • Init network call fails → reject with the ApiError
 *   • Popup blocked by browser → reject with `popup_blocked`
 */

const OAUTH_MESSAGE_TYPE = "codespace:oauth-result";
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

interface RunInput extends Omit<InitOAuthRequest, "returnUrl"> {
  /** Optional override; defaults to the in-app `/oauth-callback` route on the current origin. */
  returnUrl?: string;
}

export function useOAuthFlow() {
  const queryClient = useQueryClient();

  return useCallback(async (input: RunInput): Promise<{ credentialId: string }> => {
    const returnUrl = input.returnUrl ?? `${window.location.origin}/oauth-callback`;

    const init = await initOrThrow({ ...input, returnUrl });

    const popup = window.open(init.authorizeUrl, "codespace-oauth", POPUP_FEATURES);
    if (!popup) throw new OAuthFlowError("popup_blocked", "Popup blocked by the browser. Allow popups for this site and try again.");

    try {
      const outcome = await awaitOutcome(popup);
      // Successful OAuth always lands a new credential row. Invalidate so any open
      // modal / sidebar showing connection status re-fetches immediately rather than
      // waiting for the next mount.
      await queryClient.invalidateQueries({ queryKey: ["credentials"] });
      return outcome;
    } finally {
      try { if (!popup.closed) popup.close(); } catch { /* cross-origin during provider visit; harmless */ }
    }
  }, [queryClient]);
}

async function initOrThrow(input: InitOAuthRequest) {
  try {
    return await oauthApi.initOAuth(input);
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
