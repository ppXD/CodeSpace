import { Outlet, createFileRoute, useLocation, useNavigate } from "@tanstack/react-router";

import { RepoDetailHeader, type DetailTab } from "@/_imported/ai-code-space/repo-detail";
import { useRepositoryByFullPath } from "@/hooks/use-repositories";

/**
 * Repo-detail layout. Renders the breadcrumb / title / per-repo tab strip and
 * leaves the tab body to the sub-route via <Outlet/>. The tab strip is shared
 * across /overview, /pulls, /pulls/{n}, /issues, /branches, /activity so it
 * doesn't remount per tab — counts query stays warm, no flicker.
 *
 * URL contract: `$repoFullPath` is the provider-side fullPath ("acme/postboy.api"),
 * URL-encoded so the slash survives as `%2F` and the whole identifier stays a single
 * path segment. We decode once at the route edge to recover the readable form, then
 * resolve the UUID via the team-wide repo list (downstream components and API calls
 * still key on the UUID).
 *
 * Active-tab highlight is derived from the URL: we read `useLocation().pathname`
 * and pick whichever tab the path's first segment after `/repositories/{encoded}/`
 * matches. Falling back to "overview" when nothing matches keeps the strip
 * honest even for unknown deep links.
 */
export const Route = createFileRoute("/_app/teams/$teamSlug/repositories/$repoFullPath")({
  component: RepoDetailLayoutRoute,
});

function RepoDetailLayoutRoute() {
  const { teamSlug, repoFullPath } = Route.useParams();
  const navigate = useNavigate();
  const { pathname } = useLocation();

  // `repoFullPath` from useParams is URL-decoded by TanStack ("acme/postboy.api") —
  // use that for the team-list lookup. Navigation back to the URL needs the encoded
  // form, so we re-encode at the navigate site below.
  const fullPath = decodeURIComponent(repoFullPath);
  const { repo, isLoading, notFound } = useRepositoryByFullPath(fullPath);

  const activeTab = pickActiveTabFromPath(pathname);

  if (isLoading) return null;
  if (notFound || !repo) return null;

  return (
    <RepoDetailHeader
      repoId={repo.id}
      activeTab={activeTab}
      onTabChange={(tab) => navigate({ to: tabToPath(tab), params: { teamSlug, repoFullPath: encodeURIComponent(fullPath) } })}
      onBack={() => navigate({ to: "/teams/$teamSlug/repositories", params: { teamSlug } })}
    >
      <Outlet />
    </RepoDetailHeader>
  );
}

function pickActiveTabFromPath(pathname: string): DetailTab {
  // URL shape under this layout: /teams/{slug}/repositories/{encoded-fullPath}/{tab}/...
  // Positional split is more robust than substring matching against the encoded
  // fullPath — useParams gives us the decoded form ("acme/postboy.api") while
  // pathname keeps the encoded one ("...%2F..."), so anchoring on either alone
  // creates a mismatch. Segment 5 is always the tab regardless of encoding.
  const segs = pathname.split("/").filter(Boolean);
  // ["teams", teamSlug, "repositories", encodedFullPath, tab, ...rest]
  const seg = (segs[4] ?? "overview") as DetailTab;
  const known: DetailTab[] = ["overview", "pulls", "issues", "branches", "activity"];
  return known.includes(seg) ? seg : "overview";
}

function tabToPath(tab: DetailTab) {
  // Type-safe mapping to the matching child route. All routes are nested under
  // /teams/$teamSlug so the calling navigate() must supply both teamSlug + repoFullPath.
  switch (tab) {
    case "overview": return "/teams/$teamSlug/repositories/$repoFullPath/overview" as const;
    case "pulls": return "/teams/$teamSlug/repositories/$repoFullPath/pulls" as const;
    case "issues": return "/teams/$teamSlug/repositories/$repoFullPath/issues" as const;
    case "branches": return "/teams/$teamSlug/repositories/$repoFullPath/branches" as const;
    case "activity": return "/teams/$teamSlug/repositories/$repoFullPath/activity" as const;
  }
}
