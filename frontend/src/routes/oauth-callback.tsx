import { createFileRoute } from "@tanstack/react-router";
import { useEffect } from "react";

/**
 * Renders inside the OAuth popup window after the backend's /api/credentials/oauth/callback
 * has exchanged the code and redirected here with either:
 *   • `?oauthCredentialId=<guid>` on success
 *   • `?oauthError=<error>&oauthErrorDescription=<text>` on failure
 *
 * Responsibilities (in this order, fast):
 *   1. Post the outcome to window.opener so the originating modal can react.
 *   2. Close the popup so the user lands back on the original page.
 *   3. As a fallback (if window.close is blocked), show a short instruction.
 *
 * No router navigation, no analytics — this page must be cheap and predictable.
 */

const OAUTH_MESSAGE_TYPE = "codespace:oauth-result";

export const Route = createFileRoute("/oauth-callback")({
  component: OAuthCallback,
});

function OAuthCallback() {
  useEffect(() => {
    const params = new URLSearchParams(window.location.search);
    const credentialId = params.get("oauthCredentialId");
    const error = params.get("oauthError");
    const errorDescription = params.get("oauthErrorDescription");

    const payload = credentialId
      ? { type: OAUTH_MESSAGE_TYPE, status: "success" as const, credentialId }
      : { type: OAUTH_MESSAGE_TYPE, status: "error" as const, error: error ?? "unknown", description: errorDescription ?? undefined };

    // The popup was opened by the SPA; opener is the same origin. Restrict the message
    // to that origin so a malicious co-tenant page can't intercept by claiming opener.
    if (window.opener && !window.opener.closed) window.opener.postMessage(payload, window.location.origin);

    // Slight delay so the receiver has a chance to attach before we close — also gives
    // the user a brief glimpse of the "Connected" state in case close is blocked.
    const timer = window.setTimeout(() => { try { window.close(); } catch { /* popup blockers may refuse */ } }, 250);

    return () => window.clearTimeout(timer);
  }, []);

  const params = new URLSearchParams(window.location.search);
  const hasError = params.has("oauthError");

  return (
    <div style={{ display: "flex", flexDirection: "column", alignItems: "center", justifyContent: "center", minHeight: "100vh", fontFamily: "system-ui, sans-serif", color: "#1F1E1D" }}>
      <div style={{ width: 64, height: 64, borderRadius: 16, background: hasError ? "#FEE2E2" : "#DCFCE7", display: "flex", alignItems: "center", justifyContent: "center", fontSize: 28, marginBottom: 16 }}>
        {hasError ? "!" : "OK"}
      </div>
      <div style={{ fontSize: 16, fontWeight: 500 }}>
        {hasError ? "Authorization failed" : "Connected"}
      </div>
      <div style={{ fontSize: 13, color: "#71717A", marginTop: 6 }}>
        You can close this window.
      </div>
    </div>
  );
}
