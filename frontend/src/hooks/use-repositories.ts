import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";

import { repositoriesApi, type BindRepositoriesBulkInput } from "@/api/repositories";
import type { PullRequestState } from "@/api/types";

/** Page size for PR list pagination — matches the backend default. Same value
 *  is used by the "has next page" inference (`page.length === PR_PAGE_SIZE`)
 *  and the network request itself, so they can't drift. */
export const PR_PAGE_SIZE = 30;

export function useRepositories(providerInstanceId?: string) {
  return useQuery({
    queryKey: ["repositories", providerInstanceId ?? "all"],
    queryFn: () => repositoriesApi.list(providerInstanceId),
  });
}

export function useRepository(repositoryId: string | null) {
  return useQuery({
    queryKey: ["repository", repositoryId],
    queryFn: () => repositoriesApi.get(repositoryId!),
    enabled: repositoryId != null,
  });
}

/**
 * Resolve a repo by its provider-side fullPath ("acme/postboy.api") within the active
 * team's scope, then fetch its detail. URL-driven navigation uses fullPath (readable);
 * downstream API calls need the UUID — this hook bridges the two by looking up the
 * UUID from the team-wide repo list, then chaining into useRepository.
 *
 * Returns `{ repo, isLoading, error }` shape similar to useRepository so callers can
 * substitute it without restructuring. `repo` is null while either the lookup-list or
 * the detail fetch is still in flight, OR if no repo matches the fullPath.
 */
export function useRepositoryByFullPath(fullPath: string | null) {
  const list = useRepositories();
  // O(N) scan over the cached list — fine for typical team sizes (<1000 repos).
  // A future backend `GET /api/repositories/by-full-path` would make this O(1)
  // but isn't worth shipping until a large-team customer hits this path.
  const match = fullPath ? list.data?.find(r => r.fullPath === fullPath) : undefined;
  const detail = useRepository(match?.id ?? null);

  return {
    repo: detail.data ?? null,
    isLoading: list.isLoading || (match != null && detail.isLoading),
    error: list.error ?? detail.error,
    // The repository wasn't in the team's list — either it doesn't exist, doesn't belong
    // to this team, or the user lacks access. Distinct from `isLoading` so the caller can
    // render a 404-ish state.
    notFound: !list.isLoading && fullPath != null && match == null,
  };
}

export function useBindRepositoriesBulk() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (input: BindRepositoriesBulkInput) => repositoriesApi.bindBulk(input),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ["repositories"] }),
  });
}

export function useUnbindRepository() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (repositoryId: string) => repositoriesApi.unbind(repositoryId),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ["repositories"] }),
  });
}

/**
 * Live PR/MR list — one page at a time, keyed by `[repoId, state, page]`.
 * Each numbered-page click swaps the data; React Query caches each page
 * independently so clicking back to a previous page is instant.
 *
 * `placeholderData` is selective on purpose: across page or state changes
 * within the SAME repo we keep the previous result so the list doesn't
 * flicker to empty between fetches (and the picker can render it dimmed
 * via `isPlaceholderData` to signal "this is stale, new one is on the way").
 * Across a REPO change we drop the placeholder immediately so the user
 * doesn't see the previous repo's PRs for the duration of the new fetch.
 *
 * "Has next page" is inferred from `result.length === PR_PAGE_SIZE`: the
 * provider returned a full page, so there's at least one more. The corner
 * case where the total is an exact multiple of the page size shows an
 * extra empty page that hides the Next button cleanly — preferable to
 * faking a total we don't have.
 */
export function useRepositoryPullRequests(repositoryId: string | null, state: PullRequestState | undefined, page: number) {
  return useQuery({
    queryKey: ["repository", repositoryId, "pull-requests", state ?? "all", page],
    queryFn: () => repositoriesApi.listPullRequests(repositoryId!, state, page, PR_PAGE_SIZE),
    enabled: repositoryId != null,
    staleTime: 30_000,
    placeholderData: (previousData, previousQuery) => {
      // queryKey: ["repository", repositoryId, "pull-requests", state, page].
      // Index 1 is repoId. Same repo → keep previous (paging or state switch);
      // different repo → drop so the picker shows a clean loading panel.
      if (previousQuery && previousQuery.queryKey[1] === repositoryId) return previousData;
      return undefined;
    },
  });
}

/**
 * Single-PR fetch. Same cache key family as the list so invalidating one PR also
 * stales the list view — useful when the detail page mutates state (close / reopen
 * actions land in a later slice).
 */
export function useRepositoryPullRequest(repositoryId: string | null, number: number | null) {
  return useQuery({
    queryKey: ["repository", repositoryId, "pull-requests", "detail", number],
    queryFn: () => repositoriesApi.getPullRequest(repositoryId!, number!),
    enabled: repositoryId != null && number != null,
    staleTime: 30_000,
  });
}

/**
 * Commits-tab data. Same cache family as the detail so any future
 * "refetch this PR" gesture stales both at once.
 */
export function useRepositoryPullRequestCommits(repositoryId: string | null, number: number | null, enabled = true) {
  return useQuery({
    queryKey: ["repository", repositoryId, "pull-requests", "commits", number],
    queryFn: () => repositoriesApi.listPullRequestCommits(repositoryId!, number!),
    enabled: enabled && repositoryId != null && number != null,
    staleTime: 30_000,
  });
}

/**
 * Total open + closed PR counts for a repository. Long staleTime because the
 * counts are stable across page navigation — the tab chip stays accurate as
 * the user pages through Closed without re-fetching the count on every step.
 * A new PR being merged elsewhere would make the count stale; the 2-minute
 * window strikes a balance between freshness and request count.
 */
export function useRepositoryPullRequestCounts(repositoryId: string | null) {
  return useQuery({
    queryKey: ["repository", repositoryId, "pull-requests", "counts"],
    queryFn: () => repositoriesApi.getPullRequestCounts(repositoryId!),
    enabled: repositoryId != null,
    staleTime: 120_000,
  });
}

/**
 * Files-tab data. Patch text can be large — the `enabled` gate lets the caller
 * defer the fetch until the user actually clicks the Files changed tab.
 */
export function useRepositoryPullRequestFiles(repositoryId: string | null, number: number | null, enabled = true) {
  return useQuery({
    queryKey: ["repository", repositoryId, "pull-requests", "files", number],
    queryFn: () => repositoriesApi.listPullRequestFiles(repositoryId!, number!),
    enabled: enabled && repositoryId != null && number != null,
    staleTime: 30_000,
  });
}

/**
 * CI checks for a PR's HEAD commit. While any check is still pending we poll every
 * 30s so the user sees the spinner progress to ✓ / ✗ without manually reloading.
 * Once all checks are terminal we stop polling — React Query's staleTime carries
 * the cached value through subsequent renders.
 */
export function useRepositoryPullRequestChecks(repositoryId: string | null, number: number | null) {
  return useQuery({
    queryKey: ["repository", repositoryId, "pull-requests", "checks", number],
    queryFn: () => repositoriesApi.listPullRequestChecks(repositoryId!, number!),
    enabled: repositoryId != null && number != null,
    staleTime: 15_000,
    // refetchInterval callback runs against the last-fetched data — return a
    // number (ms) to keep polling, or false to stop. We poll only while at
    // least one check is still Pending; everything else is terminal.
    refetchInterval: (query) => {
      const data = query.state.data;
      if (!data) return false;
      const anyPending = data.some(c => c.status === "Pending");
      return anyPending ? 30_000 : false;
    },
  });
}

export function useRelinkRepositoryCredential() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: ({ repositoryId, credentialId }: { repositoryId: string; credentialId: string }) =>
      repositoriesApi.relinkCredential(repositoryId, credentialId),    // hook still calls its param "credentialId" — the wire field becomes "newCredentialId" inside repositoriesApi.
    onSuccess: () => {
      // The repo flips Error → Active and its credentialId changes. Both the list and
      // the detail view need a refresh so the status badge clears immediately.
      queryClient.invalidateQueries({ queryKey: ["repositories"] });
      queryClient.invalidateQueries({ queryKey: ["repository"] });
    },
  });
}
