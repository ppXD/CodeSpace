import { Outlet, createFileRoute, useNavigate } from "@tanstack/react-router";
import { useEffect, useState } from "react";

import { resolveTeamByUrlSlug, teamToUrlSlug, useMe } from "@/hooks/use-me";

/**
 * Team scope layout. Every team-scoped URL (`/teams/{slug}/repositories/...`,
 * etc.) passes through this component first.
 *
 * Responsibilities:
 *   1. Resolve the URL `teamSlug` against the user's actual team membership.
 *      If it doesn't match anything in the user's `/me` response, silently
 *      redirect to their default team — same intent as GitHub's "you can't
 *      see this repo, here's where you can go" bounce.
 *   2. Point the X-Team-Id header (api/client.ts + api/request.ts read it from
 *      `localStorage.activeTeamId`) at the URL-matched team, and GATE the outlet
 *      until that's in sync. The URL is the source of truth. Rendering children
 *      before the header matches this URL's team is the wrong-team-deep-link bug:
 *      their queries would fire under whatever team id was last in localStorage
 *      (another tab, a stale session), and the backend would serve THAT team's
 *      data because the caller is a legitimate member of it.
 */
export const Route = createFileRoute("/_app/teams/$teamSlug")({
  component: TeamScopeLayout,
});

const ACTIVE_TEAM_STORAGE_KEY = "codespace.activeTeamId";

function TeamScopeLayout() {
  const { teamSlug } = Route.useParams();
  const me = useMe();
  const navigate = useNavigate();

  const teams = me.data?.teams ?? [];
  // resolveTeamByUrlSlug also handles the special `personal` alias so URLs like
  // /teams/personal/... map to whichever team has kind === "Personal".
  const matched = resolveTeamByUrlSlug(teams, teamSlug);

  // Sync the header to THIS URL's team, then remember which team we synced so the outlet gate
  // below only opens once localStorage actually points at it. Effect (not render-time write)
  // keeps render pure; the gate is what guarantees no child query fires under a stale team id.
  const [syncedTeamId, setSyncedTeamId] = useState<string | null>(null);
  useEffect(() => {
    if (!matched) return;
    localStorage.setItem(ACTIVE_TEAM_STORAGE_KEY, matched.id);
    setSyncedTeamId(matched.id);
  }, [matched]);

  // Unknown / inaccessible slug → bounce to the first team the user has. No teams at all is
  // the empty-account edge case; we leave them on a blank screen (better than a redirect loop).
  useEffect(() => {
    if (!me.data || matched) return;
    const fallback = teams[0];
    // Phase 3.0 — Projects is the primary nav row; stale-slug bounces there.
    if (fallback) navigate({ to: "/teams/$teamSlug/projects", params: { teamSlug: teamToUrlSlug(fallback) }, replace: true });
  }, [matched, me.data, teams, navigate]);

  // /me loaded but the slug names no team we can see → the effect above is redirecting.
  if (me.data && !matched) return null;

  // Hold at a placeholder until /me resolves the slug AND the header is synced to it. On the
  // common in-team navigation the id is already synced, so this never shows; it only gates the
  // cold deep-link / second-tab window where the previous team's id is still in localStorage.
  if (!matched || syncedTeamId !== matched.id) {
    return (
      <section className="ct">
        <div className="ct-body"><div className="ct-empty"><div className="ct-empty-h">Loading…</div></div></div>
      </section>
    );
  }

  return <Outlet />;
}
