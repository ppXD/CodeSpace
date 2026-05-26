import { createFileRoute, useNavigate } from "@tanstack/react-router";

import { RepositoryListPage } from "@/_imported/ai-code-space/content";

/**
 * Repo list at `/teams/{slug}/repositories`. Tab + search are URL-driven so
 * deep-linking like `/teams/{slug}/repositories?tab=github&q=api` is shareable
 * across teammates. Clicking a row navigates to `/teams/{slug}/repositories/{id}`
 * — the URL is the source of truth for "what am I looking at AND which team".
 *
 * `tab` and `q` are both optional in the typed search shape so default values
 * don't pollute the URL bar: typing nothing into the search box gives a clean
 * `/teams/{slug}/repositories`, not `?q=` (noisy). Only when the user picks a
 * non-default does the param surface in the URL.
 */
type ListTab = "all" | "github" | "gitlab" | "git" | "archived";
const VALID_TABS: ReadonlyArray<ListTab> = ["all", "github", "gitlab", "git", "archived"];

interface ListSearch {
  tab?: ListTab;
  q?: string;
}

export const Route = createFileRoute("/_app/teams/$teamSlug/repositories/")({
  validateSearch: (raw: Record<string, unknown>): ListSearch => {
    const result: ListSearch = {};

    if (typeof raw.tab === "string") {
      const lower = raw.tab.toLowerCase();
      // Only surface a tab on the URL when it's both valid AND non-default.
      // `tab=all` is the cold-load view and would clutter every link.
      if ((VALID_TABS as readonly string[]).includes(lower) && lower !== "all") {
        result.tab = lower as ListTab;
      }
    }

    if (typeof raw.q === "string" && raw.q.length > 0) {
      result.q = raw.q;
    }

    return result;
  },
  component: RepoListRoute,
});

function RepoListRoute() {
  const { teamSlug } = Route.useParams();
  // Defaults applied at the consumer side, not in validateSearch — keeps the
  // URL clean while the component still gets concrete values.
  const search = Route.useSearch();
  const tab: ListTab = search.tab ?? "all";
  const q = search.q ?? "";

  const navigate = useNavigate();

  // Build the next search object by COPYING only the keys we want to surface in
  // the URL — never set `q: undefined` or `tab: undefined` keys, because some
  // serialisers still emit `?q=` for an explicit-undefined key. Conditional
  // assignment is the only way to guarantee the param drops out of the URL.
  const buildSearch = (nextTab: ListTab, nextQ: string): ListSearch => {
    const out: ListSearch = {};
    if (nextTab !== "all") out.tab = nextTab;
    if (nextQ.length > 0) out.q = nextQ;
    return out;
  };

  return (
    <RepositoryListPage
      tab={tab}
      query={q}
      onTabChange={(next) => navigate({ to: "/teams/$teamSlug/repositories", params: { teamSlug }, search: buildSearch(next, q) })}
      onQueryChange={(next) => navigate({ to: "/teams/$teamSlug/repositories", params: { teamSlug }, search: buildSearch(tab, next) })}
      // `r.fullPath` looks like "acme/postboy.api". URL-encode so the slash becomes %2F and
      // the whole identifier stays a single path segment. The matching route uses
      // decodeURIComponent to get the readable form back.
      onOpenRepo={(repoFullPath) => navigate({
        to: "/teams/$teamSlug/repositories/$repoFullPath",
        params: { teamSlug, repoFullPath: encodeURIComponent(repoFullPath) },
      })}
    />
  );
}
