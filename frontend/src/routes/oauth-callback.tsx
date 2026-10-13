import { createFileRoute } from "@tanstack/react-router";
import { useEffect, useRef, useState } from "react";

import { ApiError, oauthApi } from "@/api/oauth";

/**
 * Renders inside the OAuth popup window after the backend's /api/credentials/oauth/callback
 * redirected here with one of:
 *   • `?oauthCredentialId=<guid>` — the callback finished the connect (this browser held its binding cookie)
 *   • `?oauthError=<error>&oauthErrorDescription=<text>` — the provider refused
 *   • `#oauthCode=<code>&oauthState=<state>` — the callback could not tell this browser started the connect and
 *     handed it here instead: a browser keeps no cookie from an API on another site, so with the SPA and the API on
 *     different sites every connect arrives this way. This page finishes it as the signed-in user, and the API accepts
 *     that only from the person who started the connect. The fragment, unlike a query, reaches no server log.
 *
 * Responsibilities (in this order, fast):
 *   1. Settle the outcome — finishing a handed-off connect first, exactly once.
 *   2. Post the outcome to window.opener so the originating modal can react.
 *   3. Close the popup so the user lands back on the original page — only once that page heard: with no opener on this
 *      origin, the popup stays open and is the one place the outcome is shown.
 *   4. As a fallback (if window.close is blocked), show a short instruction.
 *
 * No router navigation, no analytics — this page must be cheap and predictable.
 */

const OAUTH_MESSAGE_TYPE = "codespace:oauth-result";

/** A pod from before the completion endpoint answers 404 for it and consumes nothing; mid rolling deploy, the next try may reach one that has it. */
const COMPLETE_ATTEMPTS = 3;
const COMPLETE_RETRY_DELAY_MS = 200;

type Outcome =
  | { status: "success"; credentialId: string }
  | { status: "error"; error: string; description?: string };

interface HandOff {
  code: string;
  state: string;
}

export const Route = createFileRoute("/oauth-callback")({
  component: OAuthCallback,
});

function OAuthCallback() {
  const [outcome, setOutcome] = useState<Outcome | null>(null);
  const settling = useRef(false);

  useEffect(() => {
    // StrictMode runs effects twice in development, and a handed-off code is single-use: spend it once.
    if (settling.current) return;
    settling.current = true;

    void settle().then(setOutcome);
  }, []);

  useEffect(() => {
    if (!outcome) return;

    // Nobody on this origin will hear how it ended — so this window stays open and says it, rather than leaving the
    // opener to report a cancel for a connect that asked the person to sign in, or that created a credential.
    if (!tellOpener(outcome)) return;

    // Slight delay so the receiver has a chance to attach before we close — also gives
    // the user a brief glimpse of the "Connected" state in case close is blocked.
    const timer = window.setTimeout(() => { try { window.close(); } catch { /* popup blockers may refuse */ } }, 250);

    return () => window.clearTimeout(timer);
  }, [outcome]);

  return <OAuthCallbackView outcome={outcome} />;
}

async function settle(): Promise<Outcome> {
  const handOff = takeHandOff();

  if (handOff) return finish(handOff);

  const params = new URLSearchParams(window.location.search);
  const credentialId = params.get("oauthCredentialId");

  if (credentialId) return { status: "success", credentialId };

  return { status: "error", error: params.get("oauthError") ?? "unknown", description: params.get("oauthErrorDescription") ?? undefined };
}

/** The code and state the callback handed over — read once and cleared from the address bar, history and any later Referer. */
function takeHandOff(): HandOff | null {
  const fragment = new URLSearchParams(window.location.hash.slice(1));
  const code = fragment.get("oauthCode");
  const state = fragment.get("oauthState");

  if (!code || !state) return null;

  window.history.replaceState(window.history.state, "", window.location.pathname + window.location.search);

  return { code, state };
}

async function finish(handOff: HandOff): Promise<Outcome> {
  try {
    const { credentialId } = await completeOnAPodThatCan(handOff);

    return { status: "success", credentialId };
  } catch (err) {
    return failureOf(err);
  }
}

async function completeOnAPodThatCan(handOff: HandOff) {
  for (let attempt = 1; ; attempt++) {
    try {
      return await oauthApi.completeOAuth(handOff);
    } catch (err) {
      if (!(err instanceof ApiError) || err.status !== 404 || attempt >= COMPLETE_ATTEMPTS) throw err;

      await new Promise((resolve) => window.setTimeout(resolve, COMPLETE_RETRY_DELAY_MS));
    }
  }
}

/**
 * Posts the outcome to the SPA page that opened this popup; false when no such page is there to hear it. The message is
 * restricted to this origin so a co-tenant page can't intercept it by claiming to be the opener, so an opener on any
 * other origin — the connect came back to another host than it started on — would drop it unheard.
 */
function tellOpener(outcome: Outcome): boolean {
  const opener = sameOriginOpener();

  opener?.postMessage({ type: OAUTH_MESSAGE_TYPE, ...outcome }, window.location.origin);

  return opener != null;
}

/** The window that opened this popup when it is still open and on this origin; a browser refuses to read another origin's location. */
function sameOriginOpener(): Window | null {
  const opener = window.opener as Window | null;

  if (!opener || opener.closed) return null;

  try {
    return opener.location.origin === window.location.origin ? opener : null;
  } catch {
    return null;
  }
}

function failureOf(err: unknown): Outcome {
  if (err instanceof ApiError && err.status === 401) return { status: "error", error: err.code, description: "Sign in to CodeSpace in this browser, then connect again." };

  if (err instanceof ApiError) return { status: "error", error: err.code, description: err.message };

  return { status: "error", error: "complete_failed", description: err instanceof Error ? err.message : undefined };
}

/** The warm palette the signed-out pages use (styles/auth.css): this popup sits outside the app shell and its stylesheet. */
const PALETTE = { ground: "#FAF9F5", ink: "#1F1E1D", muted: "#75736E", pending: "#F5F4EE", good: "#3E8E63", goodSoft: "#E3F1E9", danger: "#C0623D", dangerSoft: "#F4E3D8" };

function OAuthCallbackView({ outcome }: { outcome: Outcome | null }) {
  const failed = outcome?.status === "error";
  const badge = outcome == null ? { ground: PALETTE.pending, ink: PALETTE.muted, mark: "…" } : failed ? { ground: PALETTE.dangerSoft, ink: PALETTE.danger, mark: "!" } : { ground: PALETTE.goodSoft, ink: PALETTE.good, mark: "OK" };
  const title = outcome == null ? "Finishing connection…" : failed ? "Authorization failed" : "Connected";
  const detail = failed && outcome.description ? outcome.description : "You can close this window.";

  return (
    <div style={{ display: "flex", flexDirection: "column", alignItems: "center", justifyContent: "center", minHeight: "100vh", padding: 24, background: PALETTE.ground, color: PALETTE.ink }}>
      <div style={{ width: 64, height: 64, borderRadius: 16, background: badge.ground, color: badge.ink, display: "flex", alignItems: "center", justifyContent: "center", fontSize: 24, marginBottom: 16 }}>
        {badge.mark}
      </div>
      <div style={{ fontSize: 16, fontWeight: 500 }}>{title}</div>
      {outcome != null && <div style={{ fontSize: 13, color: PALETTE.muted, marginTop: 6, maxWidth: 360, textAlign: "center" }}>{detail}</div>}
    </div>
  );
}
