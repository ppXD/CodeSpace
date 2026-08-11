import type { ReactNode } from "react";

import { CodeSpaceWordmark } from "@/components/brand/CodeSpaceWordmark";

import "@/styles/auth.css";

/**
 * The frame every signed-out page shares: a dark brand panel beside a warm form panel.
 *
 * The split exists because the two halves want opposite grounds. The pixel wordmark needs high
 * contrast on dark to read as a constructed mark; the form belongs in the same warm light the rest
 * of the product uses, so a user who signs in doesn't watch the palette change under them. Giving
 * each half its own panel avoids compromising either into a middle tone that serves neither.
 *
 * `context` is for pages that have something true to say next to the mark — who invited you, which
 * team. Sign-in has nothing, and gets nothing: an empty panel with a wordmark centred in it is the
 * point, not a slot waiting for a tagline.
 */
export function AuthShell({ context, children }: { context?: ReactNode; children: ReactNode }) {
  return (
    <div className="auth-root">
      <aside className="auth-brand" style={context ? undefined : { alignItems: "center" }}>
        <CodeSpaceWordmark height={44} />
        {context && <div className="auth-brand-context">{context}</div>}
      </aside>

      <main className="auth-panel">{children}</main>
    </div>
  );
}
