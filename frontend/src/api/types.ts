/**
 * Hand-rolled shapes that mirror the backend DTOs. Lives alongside the auto-generated
 * `schema.ts` placeholder; once the backend Swagger doc stabilises run `npm run gen:api`
 * and migrate consumers to the typed openapi-fetch client. Until then these are the
 * source of truth and intentionally minimal — only fields the UI uses.
 */

export type ProviderKind = "GitHub" | "GitLab" | "Git";

export type AuthType = "Pat" | "ProjectAccessToken" | "GroupAccessToken" | "OAuth" | "GitHubApp" | "SshKey" | "BasicAuth";

export type CredentialStatus = "Active" | "Expired" | "Revoked" | "Error";

export interface ProviderInstanceSummary {
  id: string;
  teamId: string;
  provider: ProviderKind;
  displayName: string;
  baseUrl: string;
  apiUrl?: string | null;
  webUrl?: string | null;
  createdDate: string;
  oauthEnabled: boolean;
}

export interface CredentialSummary {
  id: string;
  teamId: string;
  providerInstanceId: string;
  ownerUserId?: string | null;
  /** Owner's display name. Surfaced so the Add Repository picker can disambiguate
   *  multiple credentials on the same provider ("alice's GitHub" vs "bob's GitHub")
   *  by identity instead of the user-editable display name. */
  ownerUserName?: string | null;
  authType: AuthType;
  displayName: string;
  status: CredentialStatus;
  expiresDate?: string | null;
  lastValidatedDate?: string | null;
  lastError?: string | null;
  createdDate: string;
}

export interface InitOAuthResponse {
  authorizeUrl: string;
  state: string;
}

export interface RevokeCredentialResponse {
  credentialId: string;
  providerAcknowledged: boolean;
  providerError?: string | null;
  /** Number of repositories that were just flipped to Status=Error because they
   *  were bound through this credential. Zero when the credential wasn't tied to
   *  any active repo. UI uses this to confirm the cascade hit what the preview said. */
  affectedRepositoryCount: number;
}

export interface CredentialUsage {
  credentialId: string;
  activeRepositoryCount: number;
}

/** Pre-delete preview for a provider instance — drives the confirm dialog's wording
 *  and the "Unbind all and remove" option when there are bound repos. */
export interface ProviderInstanceUsage {
  providerInstanceId: string;
  activeRepositoryCount: number;
  activeCredentialCount: number;
}

export interface DeleteProviderInstanceResponse {
  providerInstanceId: string;
  unboundRepositoryCount: number;
  revokedCredentialCount: number;
}

export type TeamRole = "Owner" | "Admin" | "Member" | "Viewer";

/** Personal = the user's solo space (auto-created on signup, never deleted, single
 *  member); Workspace = the shared multi-member team. Same data shape across both —
 *  the kind only drives sidebar labelling and (eventually) disabling team-management
 *  actions on Personal teams. */
export type TeamKind = "Personal" | "Workspace";

export interface MeTeam {
  id: string;
  slug: string;
  name: string;
  kind: TeamKind;
  role: TeamRole;
  memberCount: number;
  repositoryCount: number;
}

export interface MeResponse {
  id: string;
  email: string;
  name: string;
  avatarUrl?: string | null;
  teams: MeTeam[];
  /**
   * True when the user must rotate their password before doing anything else. The SPA
   * forces /change-password while this is true; the backend gates every non-rotation
   * request behind the same flag.
   */
  passwordMustChange: boolean;
}

export type RepositoryVisibility = "Public" | "Internal" | "Private";
export type RepositoryStatus = "Active" | "Paused" | "Error" | "Unreachable";

export interface RepositorySummary {
  id: string;
  teamId: string;
  providerInstanceId: string;
  credentialId?: string | null;
  fullPath: string;
  name: string;
  defaultBranch: string;
  visibility: RepositoryVisibility;
  status: RepositoryStatus;
  /** Populated when status != Active. Drives the "Needs new credential" / error hint
   *  inline with the row, no per-row detail fetch needed. */
  lastError?: string | null;
  webUrl: string;
  lastEventDate?: string | null;
  createdDate: string;
}

export interface RepositoryDetail extends RepositorySummary {
  externalId: string;
  namespacePath: string;
  description?: string | null;
  cloneUrlHttps?: string | null;
  cloneUrlSsh?: string | null;
  archived: boolean;
  lastSyncedDate?: string | null;
  lastError?: string | null;
  activeWebhooksCount: number;
}

export interface RemoteRepository {
  externalId: string;
  namespacePath: string;
  name: string;
  fullPath: string;
  defaultBranch: string;
  visibility: RepositoryVisibility;
  description?: string | null;
  webUrl: string;
  cloneUrlHttps?: string | null;
  cloneUrlSsh?: string | null;
  archived: boolean;
}

export interface RemoteRepositoryPage {
  items: RemoteRepository[];
  /** Provider-side total. Populated when the provider gives us a cheap count
   *  (GitLab GraphQL). Null on GitHub where /user/repos doesn't return a total
   *  — the SPA eager-fetches the full list anyway and computes the count itself. */
  totalCount: number | null;
}

export interface BulkBindItemResult {
  projectIdentifier: string;
  repositoryId?: string | null;
  error?: string | null;
}

export interface BulkBindResult {
  items: BulkBindItemResult[];
  successCount: number;
  failureCount: number;
}

/**
 * Provider-neutral pull request shape. GitHub calls these "pull requests", GitLab
 * "merge requests" — we normalise to "pull request" everywhere on the UI side.
 * Live-fetched per request; never cached locally.
 */
export type PullRequestState = "Open" | "Draft" | "Merged" | "Closed";

/**
 * Single PR label with the provider's own colour. `color` is hex WITHOUT the leading `#`
 * (e.g. "f29513"), matching how GitHub's API serialises it. Null when the provider didn't
 * give us a colour — the renderer falls back to a deterministic palette derived from
 * `name` so the pill is still visually distinct.
 */
export interface LabelRef {
  name: string;
  color: string | null;
}

export interface RemotePullRequest {
  externalId: string;
  /** `#42` on GitHub, `!42` on GitLab. Per-repo number, not globally unique. */
  number: number;
  title: string;
  state: PullRequestState;
  sourceBranch: string;
  targetBranch: string;
  authorLogin?: string | null;
  authorAvatarUrl?: string | null;
  commentsCount: number;
  createdDate: string;
  updatedDate: string;
  mergedDate?: string | null;
  closedDate?: string | null;
  webUrl: string;
  labels: LabelRef[];
  /** Markdown body. Only populated by the detail endpoint; null on list responses. */
  body?: string | null;
  /** Diff stats populated on detail responses (GitHub only — GitLab leaves these null). */
  commitsCount?: number | null;
  additions?: number | null;
  deletions?: number | null;
  changedFilesCount?: number | null;
  /** Detail-only sidebar fields. Empty array / null on list responses. */
  assignees?: string[];
  requestedReviewers?: string[];
  milestoneTitle?: string | null;
  /** GitHub-style task progress — counts of `- [x]` (completed) and `- [ ] | - [x]` (total)
   *  parsed from the body. Both populated even on list responses (so the row can show "N
   *  of M tasks") even though `body` itself is list-omitted. Null when the body has zero
   *  task-list items at all. */
  tasksCompleted?: number | null;
  tasksTotal?: number | null;
}

export interface RemotePullRequestCommit {
  sha: string;
  shortSha: string;
  title: string;
  body?: string | null;
  authorLogin?: string | null;
  authorAvatarUrl?: string | null;
  authorName?: string | null;
  authorEmail?: string | null;
  authoredDate: string;
  webUrl?: string | null;
}

/** Mirrors backend FileChangeStatus enum. */
export type FileChangeStatus = "Added" | "Modified" | "Removed" | "Renamed";

export interface RemotePullRequestCounts {
  open: number;
  closed: number;
}

/** Mirrors backend PullRequestCheckStatus enum. */
export type PullRequestCheckStatus = "Pending" | "Success" | "Failure" | "Cancelled" | "Skipped" | "Neutral";

/**
 * One CI check entry on a PR — normalised across GitHub Actions check_runs and
 * GitLab pipeline jobs. Multiple checks per PR are normal (one workflow → many jobs).
 * Polling cadence: while ANY check has status="Pending", refetch every 30s; otherwise
 * the result is terminal and React Query's staleTime carries it.
 */
export interface RemotePullRequestCheck {
  /** Display name, e.g. "build / test (ubuntu-latest)" or "lint / eslint". */
  name: string;
  status: PullRequestCheckStatus;
  /** Provider's raw conclusion ("success", "failure", "neutral", ...). Null while running. */
  conclusion?: string | null;
  startedAt?: string | null;
  completedAt?: string | null;
  /** Pre-computed duration in whole seconds when both timestamps exist. */
  durationSeconds?: number | null;
  /** Link to the check's page on the provider — opens logs / job output. */
  detailsUrl?: string | null;
}

export interface RemotePullRequestFile {
  fileName: string;
  /** Set when status = Renamed; the file's previous path. */
  previousFileName?: string | null;
  status: FileChangeStatus;
  additions: number;
  deletions: number;
  /**
   * Unified-diff patch hunks (`@@ -a,b +c,d @@` headers + `+`/`-`/` ` body lines).
   * Null when the provider suppressed the diff (binary, too large) — UI shows
   * a "diff suppressed" placeholder and lets the user open the file on the provider.
   */
  patch?: string | null;
}

/**
 * Returned by GET /api/provider-instances/defaults/{provider}. Backend reads its own
 * IProviderModule and emits the recommended UI defaults (base URL, OAuth scope list,
 * callback URL). Frontend pulls this instead of hard-coding scope strings — when a
 * scope contract changes in the module, the UI picks it up on next render.
 */
export interface ProviderDefaults {
  provider: ProviderKind;
  defaultBaseUrl: string;
  defaultDisplayName: string;
  defaultOAuthScopes: string[];
  oAuthCallbackUrl: string;
}

/**
 * Per-capability availability snapshot for one credential. Drives "✓ Read · ⚠ Webhooks"
 * style badges so the user knows which features work before they try to bind a repo.
 */
export interface CredentialCapabilityStatus {
  capability: string;
  isAvailable: boolean;
  missingScopes: string[];
}

export interface CredentialCapabilitiesResponse {
  credentialId: string;
  grantedScopes: string[];
  capabilities: CredentialCapabilityStatus[];
}

/**
 * Structured 422 body returned when an OAuth credential's granted scopes don't cover
 * the operation. Mirrors backend GlobalExceptionFilter.BuildScopeProblemResult.
 */
export interface InsufficientScopeErrorBody {
  code: "oauth_insufficient_scope";
  message: string;
  provider: ProviderKind;
  capability: string;
  missingScopes: string[];
  grantedScopes: string[];
}

// ── Projects (Phase 3.0) ─────────────────────────────────────────────────────
//
// A Project is a team-scoped container for Repositories + project-scoped
// Variables. Workflows reference project variables via the dotted path
// `project.{slug}.{name}`. Every team has at least one Project named "default";
// new repositories default to that project unless the operator picks another.

export interface ProjectSummary {
  id: string;
  teamId: string;
  slug: string;
  name: string;
  description?: string | null;
  createdDate: string;
  activeRepositoryCount: number;
  activeVariableCount: number;
}

export interface CreateProjectInput {
  /** Display name. Backend derives the slug from this via SlugifyName; collision throws. */
  name: string;
  description?: string | null;
}

export interface UpdateProjectInput {
  name: string;
  description?: string | null;
}
