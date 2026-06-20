import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";

import { repositoriesApi, type BindRepositoriesBulkInput } from "@/api/repositories";
import type { IssueState, PullRequestReviewVerdict, PullRequestState } from "@/api/types";

/** Live source reads (branches/tree/file) share a 60s staleTime — source changes far less
 *  often than the user clicks around the tree, so we avoid re-fetching every navigation. */
const SOURCE_STALE_MS = 60_000;

/** Page size for PR list pagination — matches the backend default. Same value
 *  is used by the "has next page" inference (`page.length === PR_PAGE_SIZE`)
 *  and the network request itself, so they can't drift. */
export const PR_PAGE_SIZE = 30;

interface UseRepositoriesFilter {
  providerInstanceId?: string;
  projectId?: string;
}

/**
 * List the team's bound repositories with optional filters. Phase 3.0 — added
 * <c>projectId</c> for the project-detail page (renders only repos attached to
 * that project). Both filters are independent and additive at the backend.
 *
 * <para>Backwards-compatible call shape: the hook also accepts a bare
 * <c>providerInstanceId</c> string so existing call sites don't need updates.</para>
 */
export function useRepositories(filter?: UseRepositoriesFilter | string) {
  const normalized: UseRepositoriesFilter =
    typeof filter === "string" ? { providerInstanceId: filter } : (filter ?? {});

  return useQuery({
    queryKey: ["repositories", normalized.providerInstanceId ?? "all", normalized.projectId ?? "all"],
    queryFn: () => repositoriesApi.list(normalized.providerInstanceId, normalized.projectId),
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
  const id = match?.id ?? null;
  const detail = useRepository(id);

  // Stale-while-revalidate: `detail` serves the stored metadata snapshot instantly, so the page paints
  // with no network wait. This second read asks the backend to re-sync visibility/description/default
  // branch/… from the provider (~1-2s); once it lands we prefer it, so the header updates to the live
  // values. `isLoading` deliberately ignores it — the initial render must never block on the refresh.
  const refreshed = useQuery({
    queryKey: ["repository", id, "metadata-refresh"],
    queryFn: () => repositoriesApi.get(id!, true),
    enabled: id != null,
    staleTime: 30_000,
  });

  return {
    repo: refreshed.data ?? detail.data ?? null,
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
    // projectId set → remove from that project only (N:M); omitted → remove from the team entirely.
    mutationFn: ({ repositoryId, projectId }: { repositoryId: string; projectId?: string }) => repositoriesApi.unbind(repositoryId, projectId),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["repositories"] });
      // The per-project repo count + the project detail change too.
      queryClient.invalidateQueries({ queryKey: ["projects"] });
      queryClient.invalidateQueries({ queryKey: ["project"] });
    },
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
 * Submit a review (Approve / RequestChanges / Comment) back to the PR/MR as the caller's own
 * linked identity. On success, stale the PR's cache family so the detail + reviewers refresh.
 * The caller handles a 428 actor_identity_required via the ActorIdentityGate (see PrReviewActions).
 */
export function useSubmitPullRequestReview(repositoryId: string, number: number) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (input: { verdict: PullRequestReviewVerdict; body?: string | null }) =>
      repositoriesApi.submitPullRequestReview(repositoryId, number, input),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ["repository", repositoryId, "pull-requests"] }),
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
 * Issue list — same pagination + placeholder discipline as useRepositoryPullRequests.
 * `state` undefined = all states (the "All" filter). Across page/state changes within the
 * SAME repo we keep the previous result so the list doesn't flicker to empty between fetches;
 * across a REPO change we drop the placeholder so the previous repo's issues don't linger.
 */
export function useRepositoryIssues(repositoryId: string | null, state: IssueState | undefined, page: number) {
  return useQuery({
    queryKey: ["repository", repositoryId, "issues", state ?? "all", page],
    queryFn: () => repositoriesApi.listIssues(repositoryId!, state, page, PR_PAGE_SIZE),
    enabled: repositoryId != null,
    staleTime: 30_000,
    placeholderData: (previousData, previousQuery) => {
      if (previousQuery && previousQuery.queryKey[1] === repositoryId) return previousData;
      return undefined;
    },
  });
}

/** Total open + closed issue counts. Long staleTime like the PR counts — stable across paging. */
export function useRepositoryIssueCounts(repositoryId: string | null) {
  return useQuery({
    queryKey: ["repository", repositoryId, "issues", "counts"],
    queryFn: () => repositoriesApi.getIssueCounts(repositoryId!),
    enabled: repositoryId != null,
    staleTime: 120_000,
  });
}

/** Single issue with body + sidebar fields — the in-app detail. Same cache family as the list. */
export function useRepositoryIssue(repositoryId: string | null, number: number | null) {
  return useQuery({
    queryKey: ["repository", repositoryId, "issues", "detail", number],
    queryFn: () => repositoriesApi.getIssue(repositoryId!, number!),
    enabled: repositoryId != null && number != null,
    staleTime: 30_000,
  });
}

/** Issue comments (Conversation). */
export function useRepositoryIssueComments(repositoryId: string | null, number: number | null) {
  return useQuery({
    queryKey: ["repository", repositoryId, "issues", "comments", number],
    queryFn: () => repositoriesApi.listIssueComments(repositoryId!, number!),
    enabled: repositoryId != null && number != null,
    staleTime: 30_000,
  });
}

/** Issue activity-timeline events. */
export function useRepositoryIssueEvents(repositoryId: string | null, number: number | null) {
  return useQuery({
    queryKey: ["repository", repositoryId, "issues", "events", number],
    queryFn: () => repositoriesApi.listIssueEvents(repositoryId!, number!),
    enabled: repositoryId != null && number != null,
    staleTime: 30_000,
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

// ── Code browser (live source reads) ──────────────────────────────────────────

/** All branches for the repo's branch picker. */
export function useRepositoryBranches(repositoryId: string | null) {
  return useQuery({
    queryKey: ["repository", repositoryId, "source", "branches"],
    queryFn: () => repositoriesApi.listBranches(repositoryId!),
    enabled: repositoryId != null,
    staleTime: SOURCE_STALE_MS,
  });
}

/**
 * One level of the file tree at `path` on `ref`. The caller gates `enabled` until the
 * effective ref is known (default branch or the picked one) so we fetch each folder exactly
 * once per (ref, path) instead of double-fetching with an unresolved ref.
 */
export function useRepositoryTree(repositoryId: string | null, path: string, ref: string | null, enabled = true) {
  return useQuery({
    queryKey: ["repository", repositoryId, "source", "tree", ref ?? "default", path],
    queryFn: () => repositoriesApi.listTree(repositoryId!, path || undefined, ref || undefined),
    enabled: enabled && repositoryId != null,
    staleTime: SOURCE_STALE_MS,
  });
}

/**
 * A single file's content for the viewer / README card. `enabled` lets the caller defer the
 * fetch until a file is actually selected (or a README was found in the current tree level).
 */
export function useRepositoryFile(repositoryId: string | null, path: string | null, ref: string | null, enabled = true) {
  return useQuery({
    queryKey: ["repository", repositoryId, "source", "file", ref ?? "default", path],
    queryFn: () => repositoriesApi.getFile(repositoryId!, path!, ref || undefined),
    enabled: enabled && repositoryId != null && path != null && path.length > 0,
    staleTime: SOURCE_STALE_MS,
  });
}

/** Headline stats for the Code tab's right rail (stars/forks/counts/storage). */
export function useRepositoryStats(repositoryId: string | null) {
  return useQuery({
    queryKey: ["repository", repositoryId, "source", "stats"],
    queryFn: () => repositoriesApi.getStats(repositoryId!),
    enabled: repositoryId != null,
    staleTime: SOURCE_STALE_MS,
  });
}

/** Language composition for the Languages bar. */
export function useRepositoryLanguages(repositoryId: string | null) {
  return useQuery({
    queryKey: ["repository", repositoryId, "source", "languages"],
    queryFn: () => repositoriesApi.getLanguages(repositoryId!),
    enabled: repositoryId != null,
    staleTime: SOURCE_STALE_MS,
  });
}

/** Latest release for the Code tab's Releases card. Resolves to null when the repo has no releases. */
export function useRepositoryLatestRelease(repositoryId: string | null) {
  return useQuery({
    queryKey: ["repository", repositoryId, "source", "latest-release"],
    queryFn: () => repositoriesApi.getLatestRelease(repositoryId!),
    enabled: repositoryId != null,
    staleTime: SOURCE_STALE_MS,
  });
}

/** Releases list for the Releases page. Same page placeholder discipline as the PR/issue lists. */
export function useRepositoryReleases(repositoryId: string | null, page: number) {
  return useQuery({
    queryKey: ["repository", repositoryId, "source", "releases", page],
    queryFn: () => repositoriesApi.listReleases(repositoryId!, page, PR_PAGE_SIZE),
    enabled: repositoryId != null,
    staleTime: SOURCE_STALE_MS,
    placeholderData: (prev, prevQuery) => (prevQuery && prevQuery.queryKey[1] === repositoryId ? prev : undefined),
  });
}

/** Git tags for the Releases page's Tags tab. */
export function useRepositoryTags(repositoryId: string | null, page: number) {
  return useQuery({
    queryKey: ["repository", repositoryId, "source", "tags", page],
    queryFn: () => repositoriesApi.listTags(repositoryId!, page, PR_PAGE_SIZE),
    enabled: repositoryId != null,
    staleTime: SOURCE_STALE_MS,
    placeholderData: (prev, prevQuery) => (prevQuery && prevQuery.queryKey[1] === repositoryId ? prev : undefined),
  });
}

/** Single release by tag — the release-detail page. */
export function useRepositoryRelease(repositoryId: string | null, tag: string | null) {
  return useQuery({
    queryKey: ["repository", repositoryId, "source", "release", tag],
    queryFn: () => repositoriesApi.getRelease(repositoryId!, tag!),
    enabled: repositoryId != null && tag != null && tag.length > 0,
    staleTime: SOURCE_STALE_MS,
  });
}

/** Latest commit on the current path/ref — the header bar above the file list. */
export function useRepositoryLatestCommit(repositoryId: string | null, path: string, ref: string | null, enabled = true) {
  return useQuery({
    queryKey: ["repository", repositoryId, "source", "commit", ref ?? "default", path],
    queryFn: () => repositoriesApi.getLatestCommit(repositoryId!, path || undefined, ref || undefined),
    enabled: enabled && repositoryId != null,
    staleTime: SOURCE_STALE_MS,
  });
}

/**
 * Per-entry last commit for the current folder's children — the file rows' last-commit column. Keyed on
 * the path set so navigating folders refetches; the tree renders immediately and these fill in when ready.
 */
export function useRepositoryTreeCommits(repositoryId: string | null, paths: string[], ref: string | null, enabled = true) {
  return useQuery({
    queryKey: ["repository", repositoryId, "source", "tree-commits", ref ?? "default", ...paths],
    queryFn: () => repositoriesApi.getTreeCommits(repositoryId!, paths, ref || undefined),
    enabled: enabled && repositoryId != null && paths.length > 0,
    staleTime: SOURCE_STALE_MS,
  });
}

/**
 * Render markdown to HTML via the repo's provider — the high-fidelity README render. Content-addressed
 * (keyed on the markdown itself) so identical content is rendered once. `retry: false`: a provider with
 * no render capability errors immediately, and the caller drops to client-side rendering rather than
 * retrying a call that can't succeed.
 */
export function useRenderMarkdown(repositoryId: string | null, markdown: string, enabled = true) {
  return useQuery({
    queryKey: ["repository", repositoryId, "render-markdown", markdown],
    queryFn: () => repositoriesApi.renderMarkdown(repositoryId!, markdown),
    enabled: enabled && repositoryId != null && markdown.length > 0,
    staleTime: SOURCE_STALE_MS,
    retry: false,
  });
}
