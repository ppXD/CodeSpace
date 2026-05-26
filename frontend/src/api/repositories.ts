import { fetchJson } from "./request";
import type { BulkBindResult, PullRequestState, RemotePullRequest, RemotePullRequestCheck, RemotePullRequestCommit, RemotePullRequestCounts, RemotePullRequestFile, RemoteRepositoryPage, RepositoryDetail, RepositorySummary } from "./types";

export interface BindRepositoryInput {
  providerInstanceId: string;
  credentialId: string;
  projectIdentifier: string;
}

export interface BindRepositoriesBulkInput {
  providerInstanceId: string;
  credentialId: string;
  projectIdentifiers: string[];
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

  unbind: (repositoryId: string) => fetchJson<void>(`/api/repositories/${repositoryId}`, { method: "DELETE" }),

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
};
