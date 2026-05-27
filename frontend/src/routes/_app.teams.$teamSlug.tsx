import { Outlet, createFileRoute, useNavigate } from "@tanstack/react-router";
import { useEffect } from "react";

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
 *   2. Sync `localStorage.activeTeamId` to the URL-matched team's UUID so
 *      the api/client.ts + api/request.ts X-Team-Id middleware sees the
 *      right team for every backend call. The URL is the source of truth;
 *      localStorage is just the transport for the HTTP header injector
 *      until we have a cleaner way to thread it through.
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

  // Effect — not beforeLoad — because /me data lives in React Query and we want
  // it cached + reactive. beforeLoad would force a fetch on every route hit.
  useEffect(() => {
    if (!me.data) return; // still loading; effect re-runs when it lands

    if (!matched) {
      // Unknown / inaccessible slug → bounce to the first team the user has.
      // No teams at all is the empty-account edge case; we leave them on a
      // blank screen (better than a redirect loop) and the parent layout
      // will eventually catch this via the /me UI.
      const fallback = teams[0];
      if (fallback) {
        // Phase 3.0 — Projects is the primary nav row; route stale-slug bounces
        // there instead of the retired /repositories list.
        navigate({ to: "/teams/$teamSlug/projects", params: { teamSlug: teamToUrlSlug(fallback) }, replace: true });
      }
      return;
    }

    // Sync the localStorage value the X-Team-Id header injector reads.
    if (localStorage.getItem(ACTIVE_TEAM_STORAGE_KEY) !== matched.id) {
      localStorage.setItem(ACTIVE_TEAM_STORAGE_KEY, matched.id);
    }
  }, [matched, me.data, navigate, teams]);

  // While /me is loading or the redirect-from-unknown-slug effect hasn't run
  // yet, render the outlet anyway — the X-Team-Id header may be missing on the
  // very first request burst, but the components that fire those requests are
  // gated on the matched team's data downstream. Trade-off: a brief flash of
  // "no data" before the slug→id mapping lands. Acceptable for a sub-second window.
  return <Outlet />;
}
