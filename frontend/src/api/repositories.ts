import { fetchJson } from "./request";
import type { BulkBindResult, IssueState, PullRequestReviewVerdict, PullRequestState, RemoteBranch, RemoteIssue, RemoteIssueCounts, RemoteRelease, RemoteCommitSummary, RemoteFileContent, RemoteLanguage, RemotePullRequest, RemotePullRequestCheck, RemotePullRequestCommit, RemotePullRequestCounts, RemotePullRequestFile, RemotePullRequestReview, RemoteRenderedMarkdown, RemoteRepositoryPage, RemoteRepositoryStats, RemoteTreeEntry, RepositoryDetail, RepositorySummary } from "./types";

export interface BindRepositoryInput {
  providerInstanceId: string;
  credentialId: string;
  projectIdentifier: string;
}

export interface BindRepositoriesBulkInput {
  providerInstanceId: string;
  credentialId: string;
  projectIdentifiers: string[];
  /** Phase 3.0 — target CodeSpace Project; omit to land in the team's Default. */
  projectId?: string;
}

export const repositoriesApi = {
  list: (providerInstanceId?: string, projectId?: string) => {
    const params = new URLSearchParams();
    if (providerInstanceId) params.set("providerInstanceId", providerInstanceId);
    if (projectId) params.set("projectId", projectId);
    const qs = params.toString();
    return fetchJson<RepositorySummary[]>(`/api/repositories${qs ? `?${qs}` : ""}`);
  },

  get: (repositoryId: string) => fetchJson<RepositoryDetail>(`/api/repositories/${repositoryId}`),

  bind: (input: BindRepositoryInput) => fetchJson<{ id: string }>("/api/repositories/bind", {
    method: "POST",
    body: JSON.stringify(input),
  }),

  bindBulk: (input: BindRepositoriesBulkInput) => fetchJson<BulkBindResult>("/api/repositories/bind-bulk", {
    method: "POST",
    body: JSON.stringify(input),
  }),

  // projectId set → remove only that project's link (the repo survives while other projects use it,
  // N:M); omitted → remove the repository from the team entirely.
  unbind: (repositoryId: string, projectId?: string) => {
    const qs = projectId ? `?projectId=${encodeURIComponent(projectId)}` : "";
    return fetchJson<void>(`/api/repositories/${repositoryId}${qs}`, { method: "DELETE" });
  },

  // Re-point a repo at another active credential of the same provider — recovery path
  // for the "credential disconnected → repo went Error" cascade. Body field is
  // `newCredentialId` to mirror the command record's NewCredentialId property so
  // ASP.NET model binding can take the command directly (Rule 17).
  relinkCredential: (repositoryId: string, newCredentialId: string) =>
    fetchJson<void>(`/api/repositories/${encodeURIComponent(repositoryId)}/relink-credential`, {
      method: "POST",
      body: JSON.stringify({ newCredentialId }),
    }),

  accessibleFor: (credentialId: string, search?: string, page = 1, perPage = 30) => {
    const params = new URLSearchParams();
    if (search) params.set("search", search);
    params.set("page", String(page));
    params.set("perPage", String(perPage));
    return fetchJson<RemoteRepositoryPage>(`/api/credentials/${credentialId}/accessible-repositories?${params.toString()}`);
  },

  // Live PR/MR listing, paginated. `state` omitted = all states; `page` is 1-based
  // and `perPage` defaults to 30 server-side (max 100). Callers detect "more available"
  // when the returned array length equals `perPage`.
  listPullRequests: (repositoryId: string, state?: PullRequestState, page = 1, perPage = 30) => {
    const params = new URLSearchParams();
    if (state) params.set("state", state);
    params.set("page", String(page));
    params.set("perPage", String(perPage));
    return fetchJson<RemotePullRequest[]>(`/api/repositories/${encodeURIComponent(repositoryId)}/pull-requests?${params.toString()}`);
  },

  // Single-PR live fetch — returns Body + diff stats in addition to the list shape.
  getPullRequest: (repositoryId: string, number: number) =>
    fetchJson<RemotePullRequest>(`/api/repositories/${encodeURIComponent(repositoryId)}/pull-requests/${number}`),

  listPullRequestCommits: (repositoryId: string, number: number) =>
    fetchJson<RemotePullRequestCommit[]>(`/api/repositories/${encodeURIComponent(repositoryId)}/pull-requests/${number}/commits`),

  listPullRequestFiles: (repositoryId: string, number: number) =>
    fetchJson<RemotePullRequestFile[]>(`/api/repositories/${encodeURIComponent(repositoryId)}/pull-requests/${number}/files`),

  // CI / checks for the PR's HEAD commit. Normalised across GitHub Actions check_runs
  // and GitLab pipeline jobs. Empty list when the provider has no checks configured
  // or the token lacks the required scope — the backend swallows the error and
  // returns empty rather than failing the whole PR detail view.
  listPullRequestChecks: (repositoryId: string, number: number) =>
    fetchJson<RemotePullRequestCheck[]>(`/api/repositories/${encodeURIComponent(repositoryId)}/pull-requests/${number}/checks`),

  // Total open + closed counts. Cheap one-shot per repo (GitHub Search / GitLab GraphQL).
  getPullRequestCounts: (repositoryId: string) =>
    fetchJson<RemotePullRequestCounts>(`/api/repositories/${encodeURIComponent(repositoryId)}/pull-requests/counts`),

  // Live issue listing, paginated — mirrors listPullRequests. `state` omitted = all states; pull
  // requests are excluded server-side. Callers detect "more available" when length === perPage.
  listIssues: (repositoryId: string, state?: IssueState, page = 1, perPage = 30) => {
    const params = new URLSearchParams();
    if (state) params.set("state", state);
    params.set("page", String(page));
    params.set("perPage", String(perPage));
    return fetchJson<RemoteIssue[]>(`/api/repositories/${encodeURIComponent(repositoryId)}/issues?${params.toString()}`);
  },

  // Total open + closed issue counts. Cheap one-shot per repo (GitHub Search / GitLab GraphQL).
  getIssueCounts: (repositoryId: string) =>
    fetchJson<RemoteIssueCounts>(`/api/repositories/${encodeURIComponent(repositoryId)}/issues/counts`),

  // Submit a review back to the PR/MR AS the caller's own linked identity (Model B). Returns 428
  // actor_identity_required (surfaced as ApiError) when the caller hasn't linked one — the global
  // ActorIdentityGate catches that and prompts a link, then the caller retries.
  submitPullRequestReview: (repositoryId: string, number: number, input: { verdict: PullRequestReviewVerdict; body?: string | null }) =>
    fetchJson<RemotePullRequestReview>(`/api/repositories/${encodeURIComponent(repositoryId)}/pull-requests/${number}/review`, {
      method: "POST",
      body: JSON.stringify(input),
    }),

  // ── Code browser (live source reads, never cached locally) ──

  // All branches for the repo — the Code tab's branch picker. The repo's default is flagged on each row.
  listBranches: (repositoryId: string) =>
    fetchJson<RemoteBranch[]>(`/api/repositories/${encodeURIComponent(repositoryId)}/branches`),

  // One level of the file tree. `path` omitted = root; `ref` omitted = the repo's default branch.
  // Non-recursive — the browser drills into each folder lazily.
  listTree: (repositoryId: string, path?: string, ref?: string) => {
    const params = new URLSearchParams();
    if (path) params.set("path", path);
    if (ref) params.set("ref", ref);
    const qs = params.toString();
    return fetchJson<RemoteTreeEntry[]>(`/api/repositories/${encodeURIComponent(repositoryId)}/tree${qs ? `?${qs}` : ""}`);
  },

  // A single file's content for the viewer. `ref` omitted = default branch. Binary / oversized
  // files come back flagged (isBinary / isTruncated) with no inline text.
  getFile: (repositoryId: string, path: string, ref?: string) => {
    const params = new URLSearchParams();
    params.set("path", path);
    if (ref) params.set("ref", ref);
    return fetchJson<RemoteFileContent>(`/api/repositories/${encodeURIComponent(repositoryId)}/file?${params.toString()}`);
  },

  // ── Code browser v2: stats sidebar · Languages bar · commit columns ──

  getStats: (repositoryId: string) =>
    fetchJson<RemoteRepositoryStats>(`/api/repositories/${encodeURIComponent(repositoryId)}/stats`),

  getLanguages: (repositoryId: string) =>
    fetchJson<RemoteLanguage[]>(`/api/repositories/${encodeURIComponent(repositoryId)}/languages`),

  // Latest release for the Code tab's Releases card. Resolves to null when the repo has no releases.
  getLatestRelease: (repositoryId: string) =>
    fetchJson<RemoteRelease | null>(`/api/repositories/${encodeURIComponent(repositoryId)}/releases/latest`),

  // Latest commit on a path/ref — the header bar. Null body when the path has no history.
  getLatestCommit: (repositoryId: string, path?: string, ref?: string) => {
    const params = new URLSearchParams();
    if (path) params.set("path", path);
    if (ref) params.set("ref", ref);
    const qs = params.toString();
    return fetchJson<RemoteCommitSummary | null>(`/api/repositories/${encodeURIComponent(repositoryId)}/commit${qs ? `?${qs}` : ""}`);
  },

  // Per-entry last commit for the file rows. Returns a { path: commit } map; failed/absent paths are omitted.
  getTreeCommits: (repositoryId: string, paths: string[], ref?: string) => {
    const params = new URLSearchParams();
    for (const p of paths) params.append("paths", p);
    if (ref) params.set("ref", ref);
    return fetchJson<Record<string, RemoteCommitSummary>>(`/api/repositories/${encodeURIComponent(repositoryId)}/tree-commits?${params.toString()}`);
  },

  // Render markdown to HTML through the repo's provider (so refs / relative links resolve like on the
  // provider's site). POST so large READMEs aren't constrained by query-string limits. The caller falls
  // back to client-side rendering on any failure (providers without a render capability return an error).
  renderMarkdown: (repositoryId: string, markdown: string) =>
    fetchJson<RemoteRenderedMarkdown>(`/api/repositories/${encodeURIComponent(repositoryId)}/render-markdown`, {
      method: "POST",
      body: JSON.stringify({ markdown }),
    }),
};
