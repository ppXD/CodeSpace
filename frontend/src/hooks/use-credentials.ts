import { useEffect, useMemo } from "react";
import { useInfiniteQuery, useMutation, useQuery, useQueryClient } from "@tanstack/react-query";

import { oauthApi, type AddProviderInstanceRequest, type UpdateProviderInstanceRequest } from "@/api/oauth";
import { repositoriesApi } from "@/api/repositories";
import type { ProviderKind, RemoteRepository } from "@/api/types";

/** Page size for the Add-Repository picker's display pager. Independent of the
 *  network page size — the modal slices a client-side cached list, so this is
 *  purely a UI choice for how many rows to show per pager step. */
export const ACCESSIBLE_REPOS_PAGE_SIZE = 30;

/** Network page size for the eager-fetch loop. 100 is the per-provider cap on
 *  both GitHub and GitLab; bigger pages = fewer round-trips to load everything. */
const EAGER_FETCH_NETWORK_PAGE_SIZE = 100;

export function useCredentials() {
  return useQuery({ queryKey: ["credentials"], queryFn: () => oauthApi.listCredentials() });
}

export function useProviderInstances() {
  return useQuery({ queryKey: ["provider-instances"], queryFn: () => oauthApi.listProviderInstances() });
}

export function useAddProviderInstance() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (input: AddProviderInstanceRequest) => oauthApi.addProviderInstance(input),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ["provider-instances"] }),
  });
}

export function useUpdateProviderInstance() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: ({ id, input }: { id: string; input: UpdateProviderInstanceRequest }) => oauthApi.updateProviderInstance(id, input),
    onSuccess: () => {
      // Both the list and the per-credential capability badges depend on the underlying
      // OAuth-enabled flag, so invalidate both so the row goes from "Needs OAuth setup"
      // → "OAuth ready" without a manual refresh.
      queryClient.invalidateQueries({ queryKey: ["provider-instances"] });
      queryClient.invalidateQueries({ queryKey: ["credential-capabilities"] });
    },
  });
}

export function useDeleteProviderInstance() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: ({ id, force }: { id: string; force?: boolean }) => oauthApi.deleteProviderInstance(id, { force }),
    onSuccess: () => {
      // Provider gone → cascade-revoked credentials are now Revoked, cascaded repos
      // (when force=true) are soft-deleted. Invalidate all three lists so the UI
      // reflects the cascade without a manual refresh.
      queryClient.invalidateQueries({ queryKey: ["provider-instances"] });
      queryClient.invalidateQueries({ queryKey: ["credentials"] });
      queryClient.invalidateQueries({ queryKey: ["repositories"] });
    },
  });
}

/**
 * Single-page server-search call used by GitLab (which natively supports
 * `?search=foo&membership=true` against the membership-scoped /projects list).
 * GitHub uses the eager-fetch hook below instead — see `useAllAccessibleRepositories`
 * for why.
 *
 * `placeholderData` is selective on purpose: across page or search changes
 * within the SAME credential we keep the previous data so the picker doesn't
 * flicker to empty between calls. Across a CREDENTIAL change we drop it
 * immediately — otherwise the user sees the previous credential's repo list
 * for the duration of the new credential's fetch, which is the exact behaviour
 * the Add Repository flow has been bug-reported for.
 */
export function useAccessibleRepositories(credentialId: string | null, page: number, search: string) {
  return useQuery({
    queryKey: ["accessible-repositories", credentialId, search, page],
    queryFn: () => repositoriesApi.accessibleFor(credentialId!, search || undefined, page, ACCESSIBLE_REPOS_PAGE_SIZE),
    enabled: credentialId != null,
    // The user expects this to reflect new repos they may have just been granted on the
    // provider — keep it shorter than the default to avoid stale lists.
    staleTime: 10_000,
    placeholderData: (previousData, previousQuery) => {
      // queryKey shape: ["accessible-repositories", credentialId, search, page].
      // Index 1 is the credentialId. When it matches, this is a page/search
      // transition on the same credential — keep continuity. When it doesn't,
      // it's a credential switch and we want a clean loading state.
      if (previousQuery && previousQuery.queryKey[1] === credentialId) return previousData;
      return undefined;
    },
  });
}

/**
 * Eagerly fetches **every** repo this credential can see, paging the backend in
 * 100-row chunks until exhausted, then lets the caller filter + paginate on
 * the cached list in memory. **GitHub only** — GitLab has native server-side
 * search that the `useAccessibleRepositories` hook above uses directly.
 *
 * Why eager-fetch for GitHub: there is NO GitHub API surface that combines
 * full visibility (own + collaborator + organisation-member) with name
 * search:
 *   - REST `/user/repos` has the visibility but no `q`/`search`/`name`.
 *   - REST `/search/repositories` has search but no `affiliation:` or
 *     `collaborator:` qualifier (and boolean operators don't compose
 *     `user:X` with `org:Y` in practice).
 *   - GraphQL `viewer.repositories(affiliations:[...])` matches /user/repos
 *     visibility but the `repositories` connection has no `query`/`search`
 *     argument.
 *
 * Splitting the picker between two visibility sets is the bug we shipped and
 * pulled — repos visible in the browse list disappeared the moment the user
 * typed into the search box. Eager-fetching the full visible list once and
 * filtering client-side guarantees search visibility exactly matches browse
 * visibility. The trade-off is an initial latency cost proportional to repo
 * count (~one round-trip per 100 repos), amortised across all subsequent
 * searches / page steps that hit zero network.
 */
export function useAllAccessibleRepositories(credentialId: string | null) {
  const query = useInfiniteQuery({
    queryKey: ["accessible-repositories-all", credentialId],
    enabled: credentialId != null,
    initialPageParam: 1,
    queryFn: ({ pageParam }) => repositoriesApi.accessibleFor(credentialId!, undefined, pageParam, EAGER_FETCH_NETWORK_PAGE_SIZE),
    // Short-circuit when a page came back under the network page size — that's
    // the provider telling us we've hit the end. Otherwise advance to the next
    // 1-based page number.
    getNextPageParam: (lastPage, allPages) => lastPage.items.length < EAGER_FETCH_NETWORK_PAGE_SIZE ? undefined : allPages.length + 1,
    // Same staleness window as the single-page hook above — fresh enough to
    // pick up newly-granted repos on the provider without re-paging on every
    // modal open.
    staleTime: 10_000,
  });

  // Drive the loop: as soon as one page lands, kick off the next until the
  // provider says we're out. `useInfiniteQuery` doesn't auto-page — it pauses
  // between pages waiting for a `fetchNextPage()` call (designed for "load
  // more" buttons). We want the full list as fast as possible, so we chain
  // it ourselves here.
  useEffect(() => {
    if (query.hasNextPage && !query.isFetchingNextPage) {
      query.fetchNextPage();
    }
  }, [query.hasNextPage, query.isFetchingNextPage, query.fetchNextPage, query]);

  const items = useMemo(
    () => query.data?.pages.flatMap(p => p.items) ?? [],
    [query.data],
  );

  // "Loading" stays true while any page is in flight (first or subsequent),
  // so the UI can render the cumulative-progress hint. "Fully loaded" requires
  // both: at least one page landed AND no more pages remain.
  const isLoading = query.isLoading || query.isFetchingNextPage;
  const isFullyLoaded = !isLoading && query.isSuccess && !query.hasNextPage;

  return { items, isLoading, isFullyLoaded, error: query.error };
}

/**
 * Adapter the Add-Repository picker calls. Returns a uniform shape regardless of
 * provider — the picker doesn't have to branch.
 *
 * Both GitHub and GitLab go through `useAllAccessibleRepositories` (eager-fetch
 * the full visible list once, filter+paginate client-side). Each provider has a
 * different reason for needing this:
 *
 *   - **GitHub**: no API surface combines full visibility (own + collaborator
 *     + org-member) with name search. REST `/user/repos` has visibility but no
 *     search; REST `/search/repositories` has search but no affiliation
 *     qualifier; GraphQL `viewer.repositories` has visibility but no search arg.
 *
 *   - **GitLab**: `/projects?search=` silently rejects queries shorter than 3
 *     characters (verified empirically on self-hosted: `search=a/po` returns 0,
 *     `search=pock` returns the expected 4 hits). Server-side search is unusable
 *     for the first 1–2 keystrokes — the worst time to silently break.
 *
 * Trade-off vs single-page server-side search: one-time upfront fetch cost on
 * modal open, proportional to total repo count (~one round-trip per 100 repos).
 * For workspaces with low-thousands of repos this is sub-second and amortises
 * across every subsequent search/page step that hits zero network. For the
 * pathological 10k+ workspace case, this UX would need to switch strategies —
 * not a worry until we see it.
 *
 * `providerKind` is accepted but currently unused — kept on the signature so we
 * can re-introduce per-provider branching (e.g. for very-large GitLab
 * workspaces) without rewiring the call site.
 */
export interface AccessibleRepoPickerData {
  pageItems: RemoteRepository[];
  /** Total matches across the full visible list (GitHub: filtered count; GitLab:
   *  provider-reported total when available). Null when GitLab couldn't get a
   *  cheap total — the picker falls back to an open-ended pager. */
  totalCount: number | null;
  /** Total pages when known; null = open-ended (only GitLab + no GraphQL total). */
  totalPages: number | null;
  /** Cumulative items fetched so far (GitHub eager path only). 0 for GitLab. */
  loadedCount: number;
  /** Provider is still fetching — drives the "loading…" panel and the cumulative
   *  progress hint. */
  isLoading: boolean;
  /** User-initiated refetch in flight while previous data is still on screen.
   *  True only for GitLab when the user changes the page or types into search
   *  (queryKey changes, placeholder keeps the previous result). False for
   *  GitHub because its page/search transitions are pure client-side over the
   *  cached eager-fetch list — no network, no staleness to flag.
   *  Drives the picker's dim-while-refetching affordance. */
  isRefetching: boolean;
  /** Fully exhausted (GitHub: all pages fetched; GitLab: current page fetched). */
  isFullyLoaded: boolean;
  error: unknown;
}

export function useAccessibleRepositoriesForPicker(credentialId: string | null, _providerKind: ProviderKind | null, page: number, search: string): AccessibleRepoPickerData {
  const eager = useAllAccessibleRepositories(credentialId);

  return useMemo<AccessibleRepoPickerData>(() => {
    const filtered = search
      ? eager.items.filter(r => {
          const needle = search.toLowerCase();
          return r.fullPath.toLowerCase().includes(needle) || r.name.toLowerCase().includes(needle);
        })
      : eager.items;
    const totalCount = filtered.length;
    const totalPages = Math.max(1, Math.ceil(totalCount / ACCESSIBLE_REPOS_PAGE_SIZE));
    const safePage = Math.min(Math.max(1, page), totalPages);
    const pageItems = filtered.slice((safePage - 1) * ACCESSIBLE_REPOS_PAGE_SIZE, safePage * ACCESSIBLE_REPOS_PAGE_SIZE);
    return {
      pageItems,
      totalCount,
      totalPages,
      loadedCount: eager.items.length,
      isLoading: eager.isLoading,
      // The eager-loop doesn't produce user-stale data — the displayed
      // filter+paginate view always reflects the cached items; new pages
      // just grow the cache. So no dim needed.
      isRefetching: false,
      isFullyLoaded: eager.isFullyLoaded,
      error: eager.error,
    };
  }, [search, page, eager.items, eager.isLoading, eager.isFullyLoaded, eager.error]);
}

export function useRevokeCredential() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (credentialId: string) => oauthApi.revokeCredential(credentialId),
    onSuccess: () => {
      // Revoke also cascade-marks dependent repos as Error, so invalidate both lists —
      // otherwise the repo table would still show them as Active until the next refresh.
      queryClient.invalidateQueries({ queryKey: ["credentials"] });
      queryClient.invalidateQueries({ queryKey: ["repositories"] });
      queryClient.invalidateQueries({ queryKey: ["credential-capabilities"] });
    },
  });
}

/** Backend-driven defaults for the "Add provider" form. Cached aggressively — the source
 *  is a static IProviderModule, so the values change only on a backend deploy. */
export function useProviderDefaults(provider: ProviderKind | null) {
  return useQuery({
    queryKey: ["provider-defaults", provider],
    queryFn: () => oauthApi.getProviderDefaults(provider!),
    enabled: provider != null,
    staleTime: 5 * 60_000,
  });
}

/** Per-credential capability availability. Refetches alongside the credential list so
 *  ✓/⚠ badges stay in sync when a credential is added, revoked, or re-scoped. */
export function useCredentialCapabilities(credentialId: string | null) {
  return useQuery({
    queryKey: ["credential-capabilities", credentialId],
    queryFn: () => oauthApi.getCredentialCapabilities(credentialId!),
    enabled: credentialId != null,
    staleTime: 30_000,
  });
}
