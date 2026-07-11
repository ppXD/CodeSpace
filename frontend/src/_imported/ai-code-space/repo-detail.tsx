import { useNavigate } from "@tanstack/react-router";
import { useRef, useState, type CSSProperties, type MouseEvent as ReactMouseEvent } from "react";
import ReactMarkdown from "react-markdown";
import remarkGfm from "remark-gfm";

import { ApiError } from "@/api/request";
import type { LabelRef, PullRequestState, RemoteIssue, RemoteIssueComment, RemoteIssueEvent, RemotePullRequest, RemotePullRequestCheck, RemotePullRequestCommit, RemotePullRequestFile, RemoteRelease, RemoteTag } from "@/api/types";
import { useProviderInstances } from "@/hooks/use-credentials";
import {
  PR_PAGE_SIZE,
  useRepository,
  useRepositoryIssue,
  useRepositoryIssueComments,
  useRepositoryIssueCounts,
  useRepositoryIssueEvents,
  useRepositoryIssues,
  useRepositoryPullRequest,
  useRepositoryPullRequestChecks,
  useRepositoryPullRequestCommits,
  useRepositoryPullRequestCounts,
  useRepositoryPullRequestFiles,
  useRepositoryPullRequests,
  useRepositoryRelease,
  useRepositoryReleases,
  useRepositoryStats,
  useRepositoryTags,
} from "@/hooks/use-repositories";
import { PrReviewActions } from "@/components/repositories/PrReviewActions";
import { LaunchTaskModal } from "@/components/tasks/LaunchTaskModal";
import { formatBytes } from "@/lib/codeTree";

import { DiffViewer } from "./diff-viewer";
import { Ic } from "./icons";
import { Pager } from "./pager";

/**
 * Repository detail. Split into URL-driven pieces:
 *
 *   RepoDetailHeader   — breadcrumb + title row + tab strip; renders its body
 *                        via {children}. The active tab is a prop, so the tab
 *                        strip is owned by the route layout and shared across
 *                        every sub-route (no re-mount on tab switch).
 *
 *   RepoOverviewBody   — Overview tab body (stats, description, clone URLs).
 *   IssuesListBody     — Issues list body (filter + paginated list).
 *   PullRequestsListBody — Pull requests list body (filter + paginated list).
 *   PullRequestDetailRoute — Pull request detail body (header + tabs + grid).
 *
 * Each body re-resolves its own repo + provider-instance data via React Query
 * hooks. Since the hooks key on the same `repoId`, the cache is shared with
 * the header — no duplicate fetches.
 */

export type DetailTab = "overview" | "code" | "issues" | "pulls";

const PROVIDER_LABEL: Record<string, string> = { GitHub: "GitHub", GitLab: "GitLab", Git: "Git" };

interface RepoDetailHeaderProps {
  repoId: string;
  activeTab: DetailTab;
  onTabChange: (tab: DetailTab) => void;
  /**
   * Team slug from the route — needed so the breadcrumb's "Projects" and
   * "{project name}" crumbs can build proper `/teams/{slug}/projects[/id]`
   * URLs without coupling this shared component to the surrounding route shape.
   */
  teamSlug: string;
  children: React.ReactNode;
}

/**
 * Persistent repo-detail shell: breadcrumbs + title row + tab strip. Mounted by
 * the `/repos/$repoId` layout route, so it stays alive across tab navigations
 * (overview → pulls → issues etc.) — the counts query stays warm, no flicker.
 *
 * The body is `{children}` and is wired to the route `<Outlet/>`. URL is the
 * source of truth for the active tab; this component just reflects it.
 *
 * Mount-time prefetches stay here because the header is the single point that
 * runs once per repo entry: the counts call powers the tab badge AND warms the
 * cache so clicking "Pull requests" reads from cache instead of cold-starting.
 */
export function RepoDetailHeader({ repoId, activeTab, onTabChange, teamSlug, children }: RepoDetailHeaderProps) {
  const repository = useRepository(repoId);
  const instances = useProviderInstances();
  const navigate = useNavigate();
  const [launchOpen, setLaunchOpen] = useState(false);

  // Breadcrumb navigation handlers — kept here so the route layer doesn't have
  // to know about the per-crumb destinations. Both target the same place the
  // sibling project-detail page lives, so deep-linking ↔ in-app navigation are
  // symmetric. Phase 3.0 rule: every repo has a parent project (repository.
  // project_id NOT NULL), so projectId being missing means the API hasn't
  // returned RepositoryDetail yet — guard at the call site, not here.
  const goToProjects = () =>
    navigate({ to: "/teams/$teamSlug/projects", params: { teamSlug } });
  const goToParentProject = (projectSlug: string) =>
    navigate({ to: "/teams/$teamSlug/projects/$projectSlug", params: { teamSlug, projectSlug } });

  // Fire the PR-counts call as soon as the repo loads — not when the user clicks
  // into the Pull-requests tab. Two benefits:
  //   1. The count appears as a badge on the tab itself, so the user knows there
  //      are open PRs without having to switch tabs.
  //   2. By the time the user does click, the data is already cached. React Query
  //      keys on ["repository", repoId, "pull-requests", "counts"], so the inner
  //      PullRequestsListBody's call to the same hook is a free cache hit — no
  //      duplicate request, no second loading state.
  const counts = useRepositoryPullRequestCounts(repoId);

  // Issue counts power the Issues tab badge (total open+closed, same convention as the PR badge) AND
  // warm the cache so the Issues tab's own counts read is a free hit when the user clicks in.
  const issueCounts = useRepositoryIssueCounts(repoId);

  // Star / fork counts power the GitHub-style pills on the right of the title row. Shared with the
  // Code tab's stats (same query key), so this is a free cache hit there.
  const stats = useRepositoryStats(repoId);

  // Prefetch page 1 of the Open PR list at the same time. The user's most likely
  // next action after landing on a repo is clicking "Pull requests" — by then this
  // call has either already returned (small repos) or is in flight (large repos)
  // and the PullRequestsListBody mounts with the data ready instead of cold-starting
  // its own fetch. Shared queryKey = single network call across both components.
  useRepositoryPullRequests(repoId, "Open", 1);

  const repo = repository.data;
  const instance = repo ? instances.data?.find(i => i.id === repo.providerInstanceId) : null;

  const mark = instance?.provider === "GitHub" ? "GH" : instance?.provider === "GitLab" ? "GL" : "G";

  const VisIcon = repo?.visibility === "Private" ? Ic.Lock
    : repo?.visibility === "Public" ? Ic.Globe : Ic.Users;

  // Each entry is [id, label, badgeCount?]. The Pull-requests badge shows
  // the **total** PRs (open + closed + merged) — matches what the operator sees
  // listed across GitHub's and GitLab's filter tabs combined. Inside the panel,
  // the Open / Closed sub-tabs split this into the per-state numbers, but at
  // the top level a single "all activity" total conveys repo busyness better
  // than just the live-open subset.
  const prTotal = counts.data ? counts.data.open + counts.data.closed : undefined;
  const issueTotal = issueCounts.data ? issueCounts.data.open + issueCounts.data.closed : undefined;
  const detailTabs: Array<[DetailTab, string, number?]> = [
    ["overview", "Overview"],
    ["code", "Code"],
    ["issues", "Issues", issueTotal],
    ["pulls", "Pull requests", prTotal],
  ];

  if (repository.isLoading || instances.isLoading) {
    return (
      <section className="rd">
        <div className="rd-head">
          {/* Loading state: only the "Projects" ancestor is known (no repo yet),
              so the trail is just Projects / Loading… The crumb is still
              clickable so an impatient user can bail back to the projects list. */}
          <div className="rd-crumbs">
            <a onClick={goToProjects}>Projects</a>
            <span className="sep">/</span>
            <span className="cur">Loading…</span>
          </div>
        </div>
      </section>
    );
  }

  if (repository.error instanceof ApiError) {
    return (
      <section className="rd">
        <div className="rd-head">
          <div className="rd-crumbs">
            <a onClick={goToProjects}>Projects</a>
            <span className="sep">/</span>
            <span className="cur">Error</span>
          </div>
        </div>
        <div className="rd-body">
          <div className="cn-banner cn-banner-err">
            <div className="cn-banner-h">Couldn't load repository</div>
            <div className="cn-banner-p">{repository.error.message}</div>
          </div>
        </div>
      </section>
    );
  }

  if (!repo) return null;

  // Parent-project crumb safety: the field is required on RepositoryDetail at
  // the DTO contract level, but a still-running old backend (no /me restart
  // yet) could return undefined. Fall back to just "Projects / {repo.name}"
  // in that case rather than rendering an empty middle crumb.
  const hasParentProject = !!repo.projectId && !!repo.projectSlug && !!repo.projectName;

  return (
    <section className="rd">
      <div className="rd-head">
        <div className="rd-crumbs">
          <a onClick={goToProjects}>Projects</a>
          <span className="sep">/</span>
          {hasParentProject && (
            <>
              <a onClick={() => goToParentProject(repo.projectSlug)}>{repo.projectName}</a>
              <span className="sep">/</span>
            </>
          )}
          <span className="cur">{repo.name}</span>
        </div>

        <div className="rd-title-row">
          <div className="rd-title-l">
            {/* Back arrow now goes to the parent project's Repositories tab —
                the closest contextual ancestor. Falls back to the projects list
                if for some reason the project link is unavailable. */}
            <button
              className="rd-back"
              onClick={() => hasParentProject ? goToParentProject(repo.projectSlug) : goToProjects()}
              title={hasParentProject ? `Back to ${repo.projectName}` : "Back to projects"}
            >
              <Ic.ChevronLeft size={15} />
            </button>
            <div className="rd-mark" data-p={instance?.provider.toLowerCase()}>{mark}</div>
            <div className="rd-name-block">
              <div className="rd-name">
                {repo.name}
                <span className="rd-vis"><VisIcon size={11} /> {repo.visibility.toLowerCase()}</span>
              </div>
              <div className="rd-path">
                {/* Provider chip is suppressed when the instance's display name already
                    contains the provider word (case-insensitive). Default-named instances
                    are literally "GitHub" / "GitLab", so without this check the path read
                    "GitLab · GitLab · acme/foo" — same trick used on the credential
                    owner badge to avoid the "alice · alice's GitHub" double-print. */}
                {instance && !instance.displayName.toLowerCase().includes(PROVIDER_LABEL[instance.provider].toLowerCase()) && (
                  <>
                    <span>{PROVIDER_LABEL[instance.provider]}</span>
                    <span style={{ color: "var(--muted-2)" }}>·</span>
                  </>
                )}
                <span>{instance?.displayName ?? "—"}</span>
                <span style={{ color: "var(--muted-2)" }}>·</span>
                <span>{repo.fullPath}</span>
              </div>
            </div>
          </div>
          <div className="rd-actions">
            {stats.data && (stats.data.stars != null || stats.data.forks != null) && (
              <div className="rd-social">
                {stats.data.stars != null && (
                  <span className="rd-social-pill" title={`${stats.data.stars.toLocaleString()} stars`}>
                    <Ic.Star size={13} /> {stats.data.stars.toLocaleString()}
                  </span>
                )}
                {stats.data.forks != null && (
                  <span className="rd-social-pill" title={`${stats.data.forks.toLocaleString()} forks`}>
                    <Ic.Fork size={13} /> {stats.data.forks.toLocaleString()}
                  </span>
                )}
              </div>
            )}
            <button className="btn" onClick={() => window.open(repo.webUrl, "_blank", "noopener")}>
              <Ic.ArrowOut size={13} /> Open on {instance ? PROVIDER_LABEL[instance.provider] : "provider"}
            </button>
            <button className="btn btn-primary" onClick={() => setLaunchOpen(true)}>
              <Ic.Zap size={13} /> Launch a task
            </button>
          </div>
        </div>

        {launchOpen && (
          <LaunchTaskModal
            surface="repo"
            autofill={{ repositoryId: repo.id, repositoryLabel: repo.fullPath, baseBranch: repo.defaultBranch }}
            onClose={() => setLaunchOpen(false)}
            onLaunched={(runId) => {
              setLaunchOpen(false);
              navigate({ to: "/teams/$teamSlug/runs/$runId", params: { teamSlug, runId } });
            }}
          />
        )}

        <div className="rd-tabs">
          {detailTabs.map(([id, label, badge]) => (
            <div
              key={id}
              className="rd-tab"
              data-active={activeTab === id}
              onClick={() => onTabChange(id)}
            >
              {label}
              {/* Badge mirrors GitHub's repo header — only shown when the count
                  query resolved with a positive value. Hidden during the initial
                  load (badge=undefined) and when the count is 0, since "Pull
                  requests 0" reads as visual noise. */}
              {badge != null && badge > 0 && <span className="rd-tab-c">{badge.toLocaleString()}</span>}
            </div>
          ))}
        </div>
      </div>

      <div className="rd-body">
        {children}
      </div>
    </section>
  );
}

interface RepoOverviewBodyProps {
  repoId: string;
}

/**
 * Overview tab body: stats grid (default branch, status, webhooks, last event),
 * the optional description card, and the clone URLs card. Re-resolves the repo
 * + provider-instance data internally so the route doesn't have to plumb it
 * through — the React Query cache shares the lookup with the header.
 */
export function RepoOverviewBody({ repoId }: RepoOverviewBodyProps) {
  const repository = useRepository(repoId);
  const repo = repository.data;

  // Header already shows a loading/error shell; bodies render nothing during
  // those states so the screen doesn't double up on placeholders.
  if (!repo) return null;

  return (
    <div className="ov">
      <div className="ov-stats">
        <div className="ov-stat">
          <div className="ov-stat-l">Default branch</div>
          <div className="ov-stat-v" style={{ fontFamily: "'Geist Mono',monospace", fontSize: 18 }}>{repo.defaultBranch}</div>
          <div className="ov-stat-sub">{repo.archived ? "archived" : "active"}</div>
        </div>
        <div className="ov-stat">
          <div className="ov-stat-l">Status</div>
          <div className="ov-stat-v">{repo.status}</div>
          {repo.lastError && <div className="ov-stat-sub" style={{ color: "var(--err)" }}>{repo.lastError}</div>}
        </div>
        <div className="ov-stat">
          <div className="ov-stat-l">Webhooks</div>
          <div className="ov-stat-v">{repo.activeWebhooksCount}</div>
          <div className="ov-stat-sub">active</div>
        </div>
        <div className="ov-stat">
          <div className="ov-stat-l">Last event</div>
          <div className="ov-stat-v" style={{ fontSize: 16 }}>{repo.lastEventDate ? new Date(repo.lastEventDate).toLocaleString() : "—"}</div>
          <div className="ov-stat-sub">webhook delivery</div>
        </div>
      </div>

      {repo.description && (
        <div className="ov-card">
          <div className="ov-card-h"><div className="ov-card-t">Description</div></div>
          <div className="ov-card-b" style={{ padding: "14px 18px", color: "var(--ink-2)", fontSize: 13.5, lineHeight: 1.55 }}>
            {repo.description}
          </div>
        </div>
      )}

      <div className="ov-card">
        <div className="ov-card-h"><div className="ov-card-t">Clone URLs</div></div>
        <div className="ov-card-b" style={{ padding: "10px 14px", fontFamily: "'Geist Mono',monospace", fontSize: 12, color: "var(--ink-2)" }}>
          {repo.cloneUrlHttps && <div>HTTPS  {repo.cloneUrlHttps}</div>}
          {repo.cloneUrlSsh && <div style={{ marginTop: 6 }}>SSH    {repo.cloneUrlSsh}</div>}
          {!repo.cloneUrlHttps && !repo.cloneUrlSsh && <div style={{ color: "var(--muted)" }}>None reported by the provider.</div>}
        </div>
      </div>
    </div>
  );
}

// ── Issues panel ──────────────────────────────────────────────────────────────
// Same GitHub-inspired layout as the Pull-requests panel — reuses every .pr-* class
// (filter tabs, rows, coloured labels, comment chip) so the two tabs read identically.
// Issues have only Open/Closed states (no draft/merged), so the filter is the same two tabs
// as the Pulls panel. There's no in-app issue detail yet, so a row click opens the issue on
// the provider in a new tab.

type IssueFilter = "Open" | "Closed";

interface IssuesListBodyProps {
  repoId: string;
  filter: IssueFilter;
  page: number;
  onFilterChange: (next: IssueFilter) => void;
  onPageChange: (next: number) => void;
  onSelectIssue: (number: number) => void;
}

/**
 * Issues tab body. All state lives in the URL; this is a pure read of `filter`/`page` props
 * plus change-callbacks (mirrors PullRequestsListBody). The server filters by state, so
 * rows render directly — no local re-filter. Counts power the filter chips + true pagination.
 */
export function IssuesListBody({ repoId, filter, page, onFilterChange, onPageChange, onSelectIssue }: IssuesListBodyProps) {
  const repository = useRepository(repoId);
  const instances = useProviderInstances();
  const repo = repository.data;
  const instance = repo ? instances.data?.find(i => i.id === repo.providerInstanceId) : null;
  const providerLabel = instance ? PROVIDER_LABEL[instance.provider] : "provider";
  const repoWebUrl = repo?.webUrl ?? "";

  const query = useRepositoryIssues(repoId, filter, page);
  const countsQuery = useRepositoryIssueCounts(repoId);

  const goToFilter = (next: IssueFilter) => {
    if (next === filter) return;
    onFilterChange(next);
  };

  const issues = query.data ?? [];

  // Total pages from the cheap counts call; fall back to "is the current page full?" when counts
  // are still loading or errored.
  const counts = countsQuery.data;
  const bucketTotal = counts ? (filter === "Open" ? counts.open : counts.closed) : null;
  const totalPages = bucketTotal != null ? Math.max(1, Math.ceil(bucketTotal / PR_PAGE_SIZE)) : null;
  const hasNextPage = totalPages != null ? page < totalPages : issues.length === PR_PAGE_SIZE;

  return (
    <div className="pr-panel">
      {query.error instanceof ApiError && (
        <div className="cn-banner cn-banner-err">
          <div className="cn-banner-h">Couldn't load issues</div>
          <div className="cn-banner-p">{query.error.message}</div>
        </div>
      )}

      <div className="pr-card">
        <div className="pr-toolbar">
          <div className="pr-tabs">
            <button className="pr-tab" data-active={filter === "Open"} onClick={() => goToFilter("Open")}>
              <Ic.IssueOpen size={13} /> Open
              {counts && <span className="pr-tab-count">{counts.open.toLocaleString()}</span>}
            </button>
            <button className="pr-tab" data-active={filter === "Closed"} onClick={() => goToFilter("Closed")}>
              <Ic.IssueClosed size={13} /> Closed
              {counts && <span className="pr-tab-count">{counts.closed.toLocaleString()}</span>}
            </button>
          </div>
          <button
            className="btn btn-ghost"
            onClick={() => window.open(`${repoWebUrl}/issues`, "_blank", "noopener")}
            title={`View all issues on ${providerLabel}`}
          >
            <Ic.ArrowOut size={12} /> Open on {providerLabel}
          </button>
        </div>

        {!query.error && issues.length === 0 && query.isFetching && (
          <div className="pr-empty">
            <div className="pr-empty-h">Loading issues…</div>
            <div className="pr-empty-p">Fetching live from {providerLabel}.</div>
          </div>
        )}

        {!query.error && issues.length === 0 && !query.isFetching && (
          <div className="pr-empty">
            <div className="pr-empty-h">No {filter.toLowerCase()} issues</div>
            <div className="pr-empty-p">
              {filter === "Closed"
                ? (page > 1 ? `No more closed issues past page ${page - 1}.` : "No closed issues yet.")
                : "When someone opens an issue on this repository, it'll show up here."}
            </div>
          </div>
        )}

        {issues.length > 0 && (
          <div className="pr-list" data-stale={query.isPlaceholderData}>
            {issues.map(issue => (
              <IssueRow key={issue.externalId} issue={issue} onSelect={() => onSelectIssue(issue.number)} />
            ))}
          </div>
        )}

        {(page > 1 || hasNextPage) && (
          <Pager
            current={page}
            totalPages={totalPages}
            hasNext={hasNextPage}
            loading={query.isPlaceholderData}
            onChange={onPageChange}
          />
        )}
      </div>
    </div>
  );
}

function IssueRow({ issue, onSelect }: { issue: RemoteIssue; onSelect: () => void }) {
  // Row click opens the in-app issue detail. Meta line mirrors the PR row: #number ·
  // opened/closed relative · by author · milestone, with a comment chip on the right.
  const closed = issue.state === "Closed";
  const stateDate = closed ? (issue.closedDate ?? issue.createdDate) : issue.createdDate;

  return (
    <div className="pr-row" onClick={onSelect}>
      <div className="pr-row-state" data-state={closed ? "closed" : "open"}>
        {closed ? <Ic.IssueClosed size={15} /> : <Ic.IssueOpen size={15} />}
      </div>
      <div className="pr-row-main">
        <div className="pr-row-title">
          <span className="pr-row-title-text">{issue.title}</span>
          {issue.labels.slice(0, 3).map(l => <PrLabel key={l.name} label={l} />)}
        </div>
        <div className="pr-row-meta">
          <span className="pr-row-num">#{issue.number}</span>
          <span>{closed ? "closed" : "opened"} {formatRelative(stateDate)}</span>
          {issue.authorLogin && (
            <span>by <span className="pr-row-author">{issue.authorLogin}</span></span>
          )}
          {issue.milestoneTitle && (
            <span className="pr-row-milestone" title={`Milestone: ${issue.milestoneTitle}`}>
              <Ic.Milestone size={11} />
              <span>{issue.milestoneTitle}</span>
            </span>
          )}
        </div>
      </div>
      {issue.commentsCount > 0 && (
        <div className="pr-row-comments" title={`${issue.commentsCount} comment${issue.commentsCount === 1 ? "" : "s"}`}>
          <Ic.Chat size={12} />
          <span>{issue.commentsCount}</span>
        </div>
      )}
    </div>
  );
}

// ── Issue detail ─────────────────────────────────────────────────────────────
// In-app issue view — mirrors the PR detail's .prd-* layout but simpler (no sub-tabs):
// a single Conversation→Activity timeline (body, then comments + events interleaved by
// date) plus a right sidebar (Assignees · Labels · Milestone). There's no in-app write
// surface yet, so commenting/closing points to the provider.

interface IssueDetailRouteProps {
  repoId: string;
  number: number;
  onBack: () => void;
}

export function IssueDetailRoute({ repoId, number, onBack }: IssueDetailRouteProps) {
  const repository = useRepository(repoId);
  const instances = useProviderInstances();
  const repo = repository.data;
  const instance = repo ? instances.data?.find(i => i.id === repo.providerInstanceId) : null;
  const providerLabel = instance ? PROVIDER_LABEL[instance.provider] : "provider";

  const detailQuery = useRepositoryIssue(repoId, number);
  const commentsQuery = useRepositoryIssueComments(repoId, number);
  const eventsQuery = useRepositoryIssueEvents(repoId, number);

  if (detailQuery.isLoading) {
    return (
      <div className="pr-panel">
        <PrDetailBackLink onBack={onBack} label="issues" />
        <div className="pr-card"><div className="pr-empty">
          <div className="pr-empty-h">Loading issue…</div>
          <div className="pr-empty-p">Fetching #{number} live from {providerLabel}.</div>
        </div></div>
      </div>
    );
  }

  if (detailQuery.error instanceof ApiError) {
    return (
      <div className="pr-panel">
        <PrDetailBackLink onBack={onBack} label="issues" />
        <div className="cn-banner cn-banner-err">
          <div className="cn-banner-h">Couldn't load issue #{number}</div>
          <div className="cn-banner-p">{detailQuery.error.message}</div>
        </div>
      </div>
    );
  }

  if (!detailQuery.data) return null;
  const issue = detailQuery.data;
  const closed = issue.state === "Closed";

  return (
    <div className="pr-panel prd">
      <PrDetailBackLink onBack={onBack} label="issues" />

      <div className="prd-head">
        <div className="prd-title-row">
          <h1 className="prd-title">{issue.title}<span className="prd-num">#{issue.number}</span></h1>
          <span className="prd-pill" data-state={closed ? "closed" : "open"}>
            {closed ? <Ic.IssueClosed size={14} /> : <Ic.IssueOpen size={14} />}
            {closed ? "Closed" : "Open"}
          </span>
        </div>
        <div className="prd-sub">
          <span className="prd-sub-author">{issue.authorLogin ?? "unknown"}</span>
          {" "}{closed ? "closed this issue" : "opened this issue"}{" · "}
          <span>{formatRelative(closed ? (issue.closedDate ?? issue.createdDate) : issue.createdDate)}</span>
        </div>
      </div>

      <div className="prd-grid">
        <div className="prd-main">
          <IssueConversation issue={issue} comments={commentsQuery.data ?? []} events={eventsQuery.data ?? []} />
        </div>

        <aside className="prd-side">
          <PrSidebarBlock title="Assignees">
            {(issue.assignees?.length ?? 0) === 0
              ? <span className="prd-side-empty">No one</span>
              : issue.assignees!.map(a => <UserChip key={a} login={a} />)}
          </PrSidebarBlock>

          <PrSidebarBlock title="Labels">
            {issue.labels.length === 0
              ? <span className="prd-side-empty">None yet</span>
              : <div className="prd-side-labels">{issue.labels.map(l => <PrLabel key={l.name} label={l} />)}</div>}
          </PrSidebarBlock>

          <PrSidebarBlock title="Milestone">
            {issue.milestoneTitle
              ? <span className="prd-side-milestone">{issue.milestoneTitle}</span>
              : <span className="prd-side-empty">No milestone</span>}
          </PrSidebarBlock>

          <div className="prd-side-foot">
            <button className="btn btn-ghost" onClick={() => window.open(issue.webUrl, "_blank", "noopener")}>
              <Ic.ArrowOut size={12} /> Open on {providerLabel}
            </button>
          </div>
        </aside>
      </div>
    </div>
  );
}

/** Body card + a single chronological timeline interleaving user comments (cards) and activity events (rows). */
function IssueConversation({ issue, comments, events }: { issue: RemoteIssue; comments: RemoteIssueComment[]; events: RemoteIssueEvent[] }) {
  type Item = { date: string } & ({ t: "comment"; c: RemoteIssueComment } | { t: "event"; e: RemoteIssueEvent });
  const items: Item[] = [
    ...comments.map(c => ({ date: c.createdAt, t: "comment" as const, c })),
    ...events.map(e => ({ date: e.createdDate, t: "event" as const, e })),
  ].sort((a, b) => new Date(a.date).getTime() - new Date(b.date).getTime());

  return (
    <div className="prd-conv">
      <CommentCard author={issue.authorLogin ?? "unknown"} date={issue.createdDate} body={issue.body} emptyText="No description provided." />
      {items.map((it, i) => it.t === "comment"
        ? <CommentCard key={`c${i}`} author={it.c.authorName} date={it.c.createdAt} body={it.c.body} emptyText="(no content)" />
        : <IssueEventRow key={`e${i}`} event={it.e} />)}
    </div>
  );
}

/** A comment card (avatar + author + markdown body) — used for the issue body and each comment. */
function CommentCard({ author, date, body, emptyText }: { author: string; date: string; body?: string | null; emptyText: string }) {
  return (
    <div className="prd-card">
      <div className="prd-card-h">
        <div className="prd-card-h-l">
          <div className="prd-avatar">{author.charAt(0).toUpperCase()}</div>
          <div>
            <div className="prd-card-h-name">{author}</div>
            <div className="prd-card-h-sub">commented {formatRelative(date)}</div>
          </div>
        </div>
      </div>
      <div className="prd-card-b">
        {body && body.trim().length > 0
          ? <div className="prd-body prd-markdown"><ReactMarkdown remarkPlugins={[remarkGfm]} components={{ a: ({ href, children, ...rest }) => <a href={href} target="_blank" rel="noopener noreferrer" {...rest}>{children}</a> }}>{body}</ReactMarkdown></div>
          : <div className="prd-body-empty">{emptyText}</div>}
      </div>
    </div>
  );
}

function IssueEventRow({ event }: { event: RemoteIssueEvent }) {
  return (
    <div className="prd-evt" data-kind={event.kind}>
      <div className="prd-evt-icon">{issueEventIcon(event.kind)}</div>
      <div className="prd-evt-text">
        {event.actorLogin && <><span className="prd-evt-name">{event.actorLogin}</span>{" "}</>}{event.summary}
      </div>
      <div className="prd-evt-right">
        <span className="prd-evt-time">{formatRelative(event.createdDate)}</span>
      </div>
    </div>
  );
}

function issueEventIcon(kind: string) {
  switch (kind) {
    case "assigned": case "unassigned": return <Ic.Users size={12} />;
    case "labeled": case "unlabeled": return <Ic.Tag size={12} />;
    case "milestoned": case "demilestoned": return <Ic.Milestone size={12} />;
    case "closed": return <Ic.IssueClosed size={12} />;
    case "reopened": return <Ic.IssueOpen size={12} />;
    case "mentioned": case "referenced": return <Ic.Commit size={12} />;
    default: return <Ic.Dot size={12} />;
  }
}

// ── Releases page ────────────────────────────────────────────────────────────
// In-app Releases page reached from the Code tab's Releases card. Releases / Tags tabs
// (GitHub style): each release shows its tag · Latest badge · author · date · notes ·
// assets; the Tags tab is a version list. Reuses the .pr-* toolbar + Pager.

type ReleasesTab = "releases" | "tags";

interface ReleasesPanelProps {
  repoId: string;
  tab: ReleasesTab;
  page: number;
  onTabChange: (next: ReleasesTab) => void;
  onPageChange: (next: number) => void;
  onBack: () => void;
  onSelectRelease: (tag: string) => void;
}

export function ReleasesPanel({ repoId, tab, page, onTabChange, onPageChange, onBack, onSelectRelease }: ReleasesPanelProps) {
  const repository = useRepository(repoId);
  const instances = useProviderInstances();
  const repo = repository.data;
  const instance = repo ? instances.data?.find(i => i.id === repo.providerInstanceId) : null;
  const providerLabel = instance ? PROVIDER_LABEL[instance.provider] : "provider";

  const releasesQuery = useRepositoryReleases(repoId, page);
  const tagsQuery = useRepositoryTags(repoId, page);
  const activeQuery = tab === "releases" ? releasesQuery : tagsQuery;
  const count = activeQuery.data?.length ?? 0;
  const hasNextPage = count === PR_PAGE_SIZE;
  const providerReleasesPath = instance?.provider === "GitLab" ? "/-/releases" : "/releases";

  return (
    <div className="pr-panel">
      <PrDetailBackLink onBack={onBack} label="code" />

      <div className="pr-card">
        <div className="pr-toolbar">
          <div className="pr-tabs">
            <button className="pr-tab" data-active={tab === "releases"} onClick={() => onTabChange("releases")}>
              <Ic.Release size={13} /> Releases
            </button>
            <button className="pr-tab" data-active={tab === "tags"} onClick={() => onTabChange("tags")}>
              <Ic.Tag size={13} /> Tags
            </button>
          </div>
          {repo && (
            <button className="btn btn-ghost" onClick={() => window.open(`${repo.webUrl}${providerReleasesPath}`, "_blank", "noopener")} title={`View on ${providerLabel}`}>
              <Ic.ArrowOut size={12} /> Open on {providerLabel}
            </button>
          )}
        </div>

        {activeQuery.error instanceof ApiError && (
          <div className="pr-empty"><div className="pr-empty-h">Couldn't load {tab}</div><div className="pr-empty-p">{activeQuery.error.message}</div></div>
        )}
        {!activeQuery.error && count === 0 && activeQuery.isFetching && (
          <div className="pr-empty"><div className="pr-empty-h">Loading {tab}…</div><div className="pr-empty-p">Fetching live from {providerLabel}.</div></div>
        )}
        {!activeQuery.error && count === 0 && !activeQuery.isFetching && (
          <div className="pr-empty"><div className="pr-empty-h">No {tab} yet</div><div className="pr-empty-p">{tab === "releases" ? "When a release is published it'll appear here." : "This repository has no tags."}</div></div>
        )}

        {tab === "releases" && (releasesQuery.data?.length ?? 0) > 0 && (
          <div className="rel-list" data-stale={releasesQuery.isPlaceholderData}>
            {releasesQuery.data!.map(r => <ReleaseCard key={r.tagName} release={r} onSelect={() => onSelectRelease(r.tagName)} />)}
          </div>
        )}
        {tab === "tags" && (tagsQuery.data?.length ?? 0) > 0 && (
          <div className="pr-list" data-stale={tagsQuery.isPlaceholderData}>
            {tagsQuery.data!.map(t => <TagRow key={t.name} tag={t} />)}
          </div>
        )}

        {(page > 1 || hasNextPage) && (
          <Pager current={page} totalPages={null} hasNext={hasNextPage} loading={activeQuery.isPlaceholderData} onChange={onPageChange} />
        )}
      </div>
    </div>
  );
}

function ReleaseCard({ release, onSelect }: { release: RemoteRelease; onSelect: () => void }) {
  return (
    <div className="rel-card">
      <div className="rel-card-h">
        <Ic.Tag size={16} className="rel-card-ic" />
        <button className="rel-card-tag" onClick={onSelect} title={`Open ${release.name ?? release.tagName}`}>{release.name ?? release.tagName}</button>
        {release.isLatest && <span className="cb-release-badge">Latest</span>}
        {release.isPrerelease && <span className="cb-release-badge" data-pre="true">Pre-release</span>}
        <div className="rel-card-meta">
          {release.authorLogin && <span>{release.authorLogin}</span>}
          {release.publishedDate && <span>released {formatRelative(release.publishedDate)}</span>}
          <span className="rel-card-tagname"><Ic.Tag size={11} /> {release.tagName}</span>
        </div>
      </div>
      {release.body && release.body.trim().length > 0 && (
        <div className="rel-card-b prd-markdown">
          <ReactMarkdown remarkPlugins={[remarkGfm]} components={{ a: ({ href, children, ...rest }) => <a href={href} target="_blank" rel="noopener noreferrer" {...rest}>{children}</a> }}>
            {release.body}
          </ReactMarkdown>
        </div>
      )}
      {(release.assets?.length ?? 0) > 0 && (
        <div className="rel-assets">
          <div className="rel-assets-h"><Ic.Box size={12} /> Assets <span className="rel-assets-c">{release.assets!.length}</span></div>
          {release.assets!.map(a => (
            <a key={a.name + a.downloadUrl} className="rel-asset" href={a.downloadUrl} target="_blank" rel="noopener noreferrer">
              <Ic.File size={13} />
              <span className="rel-asset-name">{a.name}</span>
              {a.sizeBytes != null && <span className="rel-asset-size">{formatBytes(a.sizeBytes)}</span>}
            </a>
          ))}
        </div>
      )}
    </div>
  );
}

function TagRow({ tag }: { tag: RemoteTag }) {
  return (
    <div className="pr-row" onClick={() => tag.webUrl && window.open(tag.webUrl, "_blank", "noopener")}>
      <div className="pr-row-state" data-state="open"><Ic.Tag size={14} /></div>
      <div className="pr-row-main">
        <div className="pr-row-title"><span className="pr-row-title-text">{tag.name}</span></div>
        <div className="pr-row-meta">
          {tag.commitSha && <code className="prd-commit-sha">{tag.commitSha.slice(0, 7)}</code>}
          {tag.message && <span>{tag.message}</span>}
        </div>
      </div>
    </div>
  );
}

// ── Release detail ───────────────────────────────────────────────────────────
// Single-release page (GitHub layout): "Releases / {tag}" breadcrumb back-link, the
// title + Latest/Pre-release badge, an author·date·tag·open-on-provider sub-line, then
// the full markdown notes and the assets list. Reached from a release card's title.

interface ReleaseDetailRouteProps {
  repoId: string;
  tag: string;
  onBack: () => void;
}

export function ReleaseDetailRoute({ repoId, tag, onBack }: ReleaseDetailRouteProps) {
  const repository = useRepository(repoId);
  const instances = useProviderInstances();
  const repo = repository.data;
  const instance = repo ? instances.data?.find(i => i.id === repo.providerInstanceId) : null;
  const providerLabel = instance ? PROVIDER_LABEL[instance.provider] : "provider";

  const query = useRepositoryRelease(repoId, tag);

  if (query.isLoading) {
    return (
      <div className="pr-panel">
        <PrDetailBackLink onBack={onBack} label="releases" />
        <div className="pr-card"><div className="pr-empty">
          <div className="pr-empty-h">Loading release…</div>
          <div className="pr-empty-p">Fetching {tag} live from {providerLabel}.</div>
        </div></div>
      </div>
    );
  }

  if (query.error instanceof ApiError) {
    return (
      <div className="pr-panel">
        <PrDetailBackLink onBack={onBack} label="releases" />
        <div className="cn-banner cn-banner-err">
          <div className="cn-banner-h">Couldn't load release {tag}</div>
          <div className="cn-banner-p">{query.error.message}</div>
        </div>
      </div>
    );
  }

  if (!query.data) return null;
  const r = query.data;

  return (
    <div className="pr-panel prd">
      <PrDetailBackLink onBack={onBack} label="releases" />

      <div className="prd-head">
        <div className="prd-title-row">
          <h1 className="prd-title">{r.name ?? r.tagName}</h1>
          {r.isLatest && <span className="cb-release-badge">Latest</span>}
          {r.isPrerelease && <span className="cb-release-badge" data-pre="true">Pre-release</span>}
        </div>
        <div className="prd-sub">
          {r.authorLogin && <><span className="prd-sub-author">{r.authorLogin}</span> released this </>}
          {r.publishedDate && <span>{formatRelative(r.publishedDate)}</span>}
          {" · "}
          <span className="rel-card-tagname"><Ic.Tag size={11} /> {r.tagName}</span>
          {" · "}
          <a href={r.webUrl} target="_blank" rel="noopener noreferrer">Open on {providerLabel}</a>
        </div>
      </div>

      <div className="prd-main">
        <div className="prd-card">
          <div className="prd-card-b">
            {r.body && r.body.trim().length > 0
              ? <div className="prd-body prd-markdown"><ReactMarkdown remarkPlugins={[remarkGfm]} components={{ a: ({ href, children, ...rest }) => <a href={href} target="_blank" rel="noopener noreferrer" {...rest}>{children}</a> }}>{r.body}</ReactMarkdown></div>
              : <div className="prd-body-empty">No release notes.</div>}
          </div>
        </div>

        {(r.assets?.length ?? 0) > 0 && (
          <div className="rel-assets">
            <div className="rel-assets-h"><Ic.Box size={12} /> Assets <span className="rel-assets-c">{r.assets!.length}</span></div>
            {r.assets!.map(a => (
              <a key={a.name + a.downloadUrl} className="rel-asset" href={a.downloadUrl} target="_blank" rel="noopener noreferrer">
                <Ic.File size={13} />
                <span className="rel-asset-name">{a.name}</span>
                {a.sizeBytes != null && <span className="rel-asset-size">{formatBytes(a.sizeBytes)}</span>}
              </a>
            ))}
          </div>
        )}
      </div>
    </div>
  );
}

// ── Pull requests panel ──────────────────────────────────────────────────────
// GitHub-inspired layout: a horizontal filter bar with Open / Closed counts on
// the left, then a flat list of rows. Each row leads with a state icon, then a
// bold title (clickable to open the PR on the provider in a new tab), with a
// secondary meta line beneath — #number · author · "wants to merge X into Y" ·
// updated-relative · comments. The visual style stays in the existing CodeSpace
// palette; only the layout/density follows GitHub's pattern.

type PrFilter = "Open" | "Closed";

interface PullRequestsListBodyProps {
  repoId: string;
  filter: PrFilter;
  page: number;
  onFilterChange: (next: PrFilter) => void;
  onPageChange: (next: number) => void;
  onSelectPr: (number: number) => void;
}

/**
 * Pull requests tab body (list view only — detail is a separate route).
 *
 * All state lives in the URL; this component is a pure read of `filter`/`page`
 * props plus three change-callbacks. Resolves repo + instance internally to
 * surface the provider label on the empty state and the outbound link to the
 * provider's PR page.
 *
 * `goToFilter` resets to page 1 when the filter changes so paging through
 * "Closed" then clicking "Open" lands on the first page of Open — not page 17
 * of whatever happens to live there.
 */
export function PullRequestsListBody({ repoId, filter, page, onFilterChange, onPageChange, onSelectPr }: PullRequestsListBodyProps) {
  const repository = useRepository(repoId);
  const instances = useProviderInstances();
  const repo = repository.data;
  const instance = repo ? instances.data?.find(i => i.id === repo.providerInstanceId) : null;
  const providerLabel = instance ? PROVIDER_LABEL[instance.provider] : "provider";
  const repoWebUrl = repo?.webUrl ?? "";

  const stateParam: PullRequestState | undefined = filter === "Open" ? "Open" : "Closed";
  const query = useRepositoryPullRequests(repoId, stateParam, page);
  const countsQuery = useRepositoryPullRequestCounts(repoId);

  const goToFilter = (next: PrFilter) => {
    if (next === filter) return;
    onFilterChange(next);
  };

  const prs = query.data ?? [];

  // Open-tab also surfaces drafts because GitHub treats draft as a flag on an open PR.
  // Closed-tab surfaces both Merged and Closed for the same reason.
  //
  // When `isPlaceholderData` is true (user just clicked Open→Closed or changed page,
  // we're showing the previous result while the new one is in flight) we deliberately
  // SKIP the local state filter — otherwise placeholder rows fail the new filter and
  // visiblePrs collapses to an empty array, flashing the "no PRs" empty state for a
  // moment. The dim affordance (data-stale) carries the "this is stale" signal instead.
  const visiblePrs = query.isPlaceholderData
    ? prs
    : filter === "Open"
      ? prs.filter(p => p.state === "Open" || p.state === "Draft")
      : prs.filter(p => p.state === "Merged" || p.state === "Closed");

  // Derive total page count from the cheap count call when we have it. Falls
  // back to the "is the current page full?" heuristic so the pager keeps
  // working even while counts are still loading or have errored.
  const currentBucketTotal = countsQuery.data
    ? (filter === "Open" ? countsQuery.data.open : countsQuery.data.closed)
    : null;
  const totalPages = currentBucketTotal != null
    ? Math.max(1, Math.ceil(currentBucketTotal / PR_PAGE_SIZE))
    : null;
  const hasNextPage = totalPages != null
    ? page < totalPages
    : prs.length === PR_PAGE_SIZE;

  return (
    <div className="pr-panel">
      {query.error instanceof ApiError && (
        <div className="cn-banner cn-banner-err">
          <div className="cn-banner-h">Couldn't load pull requests</div>
          <div className="cn-banner-p">{query.error.message}</div>
        </div>
      )}

      <div className="pr-card">
        <div className="pr-toolbar">
          <div className="pr-tabs">
            <button
              className="pr-tab"
              data-active={filter === "Open"}
              onClick={() => goToFilter("Open")}
            >
              <Ic.PrOpen size={13} /> Open
              {countsQuery.data && <span className="pr-tab-count">{countsQuery.data.open.toLocaleString()}</span>}
            </button>
            <button
              className="pr-tab"
              data-active={filter === "Closed"}
              onClick={() => goToFilter("Closed")}
            >
              <Ic.Check size={13} /> Closed
              {countsQuery.data && <span className="pr-tab-count">{countsQuery.data.closed.toLocaleString()}</span>}
            </button>
          </div>
          <button
            className="btn btn-ghost"
            onClick={() => window.open(`${repoWebUrl}/pulls`, "_blank", "noopener")}
            title={`View all pull requests on ${providerLabel}`}
          >
            <Ic.ArrowOut size={12} /> Open on {providerLabel}
          </button>
        </div>

        {/* Three mutually-exclusive states. Loading panel only fires when there
            is genuinely nothing to show (cold-start with no placeholder, or
            repo-switch where we drop the previous placeholder). Once any
            data — fresh or placeholder — is on screen, we render rows and let
            the dim affordance (`data-stale={isPlaceholderData}`) convey the
            refetch state. Empty state only fires when the fetch genuinely
            settled on zero rows. */}
        {!query.error && visiblePrs.length === 0 && query.isFetching && (
          <div className="pr-empty">
            <div className="pr-empty-h">Loading pull requests…</div>
            <div className="pr-empty-p">Fetching live from {providerLabel}.</div>
          </div>
        )}

        {!query.error && visiblePrs.length === 0 && !query.isFetching && (
          <div className="pr-empty">
            <div className="pr-empty-h">No {filter.toLowerCase()} pull requests</div>
            <div className="pr-empty-p">
              {filter === "Open"
                ? "When someone opens a PR or MR on this repository, it'll show up here."
                : page > 1
                  ? `No more ${filter.toLowerCase()} pull requests past page ${page - 1}.`
                  : "No closed or merged pull requests yet."}
            </div>
          </div>
        )}

        {visiblePrs.length > 0 && (
          <div className="pr-list" data-stale={query.isPlaceholderData}>
            {visiblePrs.map(pr => (
              <PullRequestRow key={pr.externalId} pr={pr} onSelect={() => onSelectPr(pr.number)} />
            ))}
          </div>
        )}

        {(page > 1 || hasNextPage) && (
          <Pager
            current={page}
            totalPages={totalPages}
            hasNext={hasNextPage}
            loading={query.isPlaceholderData}
            onChange={onPageChange}
          />
        )}
      </div>
    </div>
  );
}


function PullRequestRow({ pr, onSelect }: { pr: RemotePullRequest; onSelect: () => void }) {
  // GitHub's list intentionally OMITS the branches from each row — the meta line is
  // `#number opened TIME by AUTHOR · labels`, and the branch flow only appears in
  // the detail page. We mirror that: less noise on the list, more density signal
  // (more PRs visible without scroll), and the branch detail lives one click away
  // in the detail view we just built.
  const stateVerb = pr.state === "Merged" ? "merged" : pr.state === "Closed" ? "closed" : "opened";
  const stateDate = pr.state === "Merged"
    ? (pr.mergedDate ?? pr.updatedDate)
    : pr.state === "Closed"
      ? (pr.closedDate ?? pr.updatedDate)
      : pr.createdDate;

  return (
    <div className="pr-row" onClick={onSelect}>
      <div className="pr-row-state" data-state={pr.state.toLowerCase()}>
        <PrStateIcon state={pr.state} />
      </div>
      <div className="pr-row-main">
        <div className="pr-row-title">
          <span className="pr-row-title-text">{pr.title}</span>
          {pr.labels.slice(0, 3).map(l => <PrLabel key={l.name} label={l} />)}
        </div>
        <div className="pr-row-meta">
          <span className="pr-row-num">#{pr.number}</span>
          <span>{stateVerb} {formatRelative(stateDate)}</span>
          {pr.authorLogin && (
            <span>by <span className="pr-row-author">{pr.authorLogin}</span></span>
          )}
          <TaskProgressBadge done={pr.tasksCompleted ?? null} total={pr.tasksTotal ?? null} />
          {pr.milestoneTitle && (
            <span className="pr-row-milestone" title={`Milestone: ${pr.milestoneTitle}`}>
              <Ic.Milestone size={11} />
              <span>{pr.milestoneTitle}</span>
            </span>
          )}
        </div>
      </div>
      {pr.commentsCount > 0 && (
        <div className="pr-row-comments" title={`${pr.commentsCount} comment${pr.commentsCount === 1 ? "" : "s"}`}>
          <Ic.Chat size={12} />
          <span>{pr.commentsCount}</span>
        </div>
      )}
    </div>
  );
}

/**
 * GitHub-style task progress chip — "N of M tasks" while in-progress, "M tasks done"
 * when fully complete, hidden when the PR body has no task-list items at all
 * (both nulls). Counts are pre-computed on the backend from the markdown body so
 * the list payload doesn't need to ship the body string just to do this math.
 */
function TaskProgressBadge({ done, total }: { done: number | null; total: number | null }) {
  if (done == null || total == null || total === 0) return null;
  const allDone = done === total;
  return (
    <span className="pr-row-tasks" data-done={allDone} title={allDone ? `All ${total} tasks done` : `${done} of ${total} tasks completed`}>
      <Ic.Check size={11} />
      <span>{allDone ? `${total} ${total === 1 ? "task" : "tasks"} done` : `${done} of ${total} tasks`}</span>
    </span>
  );
}

function PrStateIcon({ state }: { state: PullRequestState }) {
  if (state === "Draft") return <Ic.PrDraft size={15} />;
  if (state === "Merged") return <Ic.PrMerged size={15} />;
  if (state === "Closed") return <Ic.PrClosed size={15} />;
  return <Ic.PrOpen size={15} />;
}

/**
 * Tiny relative-time formatter — keeps the panel free of the dayjs/luxon dependency.
 * Mirrors GitHub's "2 hours ago" / "3 days ago" style and falls back to a localised
 * date once we're past a week so the meta line doesn't grow stale.
 */
function formatRelative(iso: string): string {
  const date = new Date(iso);
  const diffMs = Date.now() - date.getTime();
  const sec = Math.floor(diffMs / 1000);

  if (sec < 60) return "just now";
  const min = Math.floor(sec / 60);
  if (min < 60) return `${min} minute${min === 1 ? "" : "s"} ago`;
  const hr = Math.floor(min / 60);
  if (hr < 24) return `${hr} hour${hr === 1 ? "" : "s"} ago`;
  const day = Math.floor(hr / 24);
  if (day < 7) return `${day} day${day === 1 ? "" : "s"} ago`;
  return `on ${date.toLocaleDateString()}`;
}

// ── Pull request detail ──────────────────────────────────────────────────────
// In-app PR view. Layout follows GitHub's PR page:
//
//   Back link
//   ── Header ──
//     Title + #number                   [state pill]
//     "@author wants to merge X commits into TARGET from SOURCE"  ·  N days ago
//   ── Tab strip ──
//     Conversation | Commits N | Files changed N  +Y -Z
//   ── Body grid ──
//     Main column (varies by tab)             Right sidebar
//                                              Assignees
//                                              Reviewers
//                                              Labels
//                                              Milestone
//                                              Open on provider
//
// Tabs we DON'T ship yet (review comments, checks, the merge button) sit one
// backend ticket away — the tab strip's open architecture absorbs them later
// without a re-layout.

type PrDetailTab = "conversation" | "commits" | "files";

interface PullRequestDetailRouteProps {
  repoId: string;
  number: number;
  onBack: () => void;
}

/**
 * Pull request detail page. Mounted at `/repos/{id}/pulls/{number}` — a real
 * URL, so deep linking works. Resolves repo + provider-instance internally to
 * derive the provider label rather than have the route plumb it through.
 */
export function PullRequestDetailRoute({ repoId, number, onBack }: PullRequestDetailRouteProps) {
  const [tab, setTab] = useState<PrDetailTab>("conversation");

  const repository = useRepository(repoId);
  const instances = useProviderInstances();
  const repo = repository.data;
  const instance = repo ? instances.data?.find(i => i.id === repo.providerInstanceId) : null;
  const providerLabel = instance ? PROVIDER_LABEL[instance.provider] : "provider";

  const detailQuery = useRepositoryPullRequest(repoId, number);
  // Eagerly fetch commits + files for the counts in the tab strip — but defer the
  // body queries' enabled gates so they only fire when the user lands on that tab.
  // (Counts come from the cached responses too, since the detail tabs reuse the
  // same query keys.)
  const commitsQuery = useRepositoryPullRequestCommits(repoId, number);
  const filesQuery = useRepositoryPullRequestFiles(repoId, number);
  // Checks: light polling while any check is still running so the spinner walks
  // to ✓/✗ without a manual reload. Hook handles the polling cadence internally.
  const checksQuery = useRepositoryPullRequestChecks(repoId, number);

  if (detailQuery.isLoading) {
    return (
      <div className="pr-panel">
        <PrDetailBackLink onBack={onBack} />
        <div className="pr-card">
          <div className="pr-empty">
            <div className="pr-empty-h">Loading pull request…</div>
            <div className="pr-empty-p">Fetching #{number} live from {providerLabel}.</div>
          </div>
        </div>
      </div>
    );
  }

  if (detailQuery.error instanceof ApiError) {
    return (
      <div className="pr-panel">
        <PrDetailBackLink onBack={onBack} />
        <div className="cn-banner cn-banner-err">
          <div className="cn-banner-h">Couldn't load pull request #{number}</div>
          <div className="cn-banner-p">{detailQuery.error.message}</div>
        </div>
      </div>
    );
  }

  if (!detailQuery.data) return null;
  const pr = detailQuery.data;

  const commitsCount = commitsQuery.data?.length ?? pr.commitsCount ?? null;
  const filesCount = filesQuery.data?.length ?? pr.changedFilesCount ?? null;

  return (
    <div className="pr-panel prd">
      <PrDetailBackLink onBack={onBack} />

      <div className="prd-head">
        <div className="prd-title-row">
          <h1 className="prd-title">
            {pr.title}
            <span className="prd-num">#{pr.number}</span>
          </h1>
          <PrStatePill state={pr.state} />
        </div>
        <div className="prd-sub">
          <span className="prd-sub-author">{pr.authorLogin ?? "unknown"}</span>
          {" "}
          {pr.state === "Merged" ? "merged" : pr.state === "Closed" ? "closed" : "wants to merge"}
          {commitsCount != null && pr.state === "Open" && <> {commitsCount} commit{commitsCount === 1 ? "" : "s"}</>}
          {" "}into{" "}
          <span className="prd-branch">{pr.targetBranch}</span>
          {" "}from{" "}
          <span className="prd-branch">{pr.sourceBranch}</span>
          {" · "}
          <span>{formatRelative(pr.state === "Merged" ? pr.mergedDate ?? pr.updatedDate : pr.state === "Closed" ? pr.closedDate ?? pr.updatedDate : pr.createdDate)}</span>
        </div>
      </div>

      <div className="prd-tabs">
        <PrTab active={tab === "conversation"} onClick={() => setTab("conversation")}>
          <Ic.Chat size={13} /> Conversation
        </PrTab>
        <PrTab active={tab === "commits"} onClick={() => setTab("commits")}>
          <Ic.Branch size={13} /> Commits
          {commitsCount != null && <span className="prd-tab-c">{commitsCount}</span>}
        </PrTab>
        <PrTab active={tab === "files"} onClick={() => setTab("files")}>
          <Ic.Book size={13} /> Files changed
          {filesCount != null && <span className="prd-tab-c">{filesCount}</span>}
        </PrTab>

        <div className="prd-tabs-spacer" />
        {(pr.additions != null || pr.deletions != null) && (
          <div className="prd-diff-badge">
            <span className="prd-diff-add">+{pr.additions ?? 0}</span>
            <span className="prd-diff-del">−{pr.deletions ?? 0}</span>
          </div>
        )}
      </div>

      <div className="prd-grid">
        <div className="prd-main">
          {tab === "conversation" && <ConversationPanel pr={pr} commitsQuery={commitsQuery} checksQuery={checksQuery} />}
          {tab === "commits" && <CommitsPanel query={commitsQuery} />}
          {tab === "files" && <FilesPanel query={filesQuery} providerLabel={providerLabel} />}
        </div>

        <aside className="prd-side">
          {pr.state === "Open" && <PrReviewActions repoId={repoId} number={number} />}

          <PrSidebarBlock title="Assignees">
            {(pr.assignees?.length ?? 0) === 0
              ? <span className="prd-side-empty">No one</span>
              : pr.assignees!.map(a => <UserChip key={a} login={a} />)}
          </PrSidebarBlock>

          <PrSidebarBlock title="Reviewers">
            {(pr.requestedReviewers?.length ?? 0) === 0
              ? <span className="prd-side-empty">No reviews requested</span>
              : pr.requestedReviewers!.map(r => <UserChip key={r} login={r} />)}
          </PrSidebarBlock>

          <PrSidebarBlock title="Labels">
            {pr.labels.length === 0
              ? <span className="prd-side-empty">None yet</span>
              : <div className="prd-side-labels">
                  {pr.labels.map(l => <PrLabel key={l.name} label={l} />)}
                </div>}
          </PrSidebarBlock>

          <PrSidebarBlock title="Milestone">
            {pr.milestoneTitle
              ? <span className="prd-side-milestone">{pr.milestoneTitle}</span>
              : <span className="prd-side-empty">No milestone</span>}
          </PrSidebarBlock>

          <div className="prd-side-foot">
            <button className="btn btn-ghost" onClick={() => window.open(pr.webUrl, "_blank", "noopener")}>
              <Ic.ArrowOut size={12} /> Open on {providerLabel}
            </button>
          </div>
        </aside>
      </div>
    </div>
  );
}

// ── Sub-components ───────────────────────────────────────────────────────────

/**
 * Conversation = the original PR body (rendered as the author's first comment),
 * followed by a vertical-thread timeline of derived events (commits, milestone,
 * assignees, reviewers, labels, merge/close), capped with a merge-status card
 * and a "comment on provider" hint. We DO NOT compose comments yet — there's
 * no review API wired — so the footer points to the provider for that.
 *
 * Timeline events are derived from the current detail state rather than from a
 * real history API. That trade-off matches what we can ship today:
 *   - Commits come from /pull-requests/{n}/commits (already loaded for the tab counter).
 *   - Assignment / review-request / label / milestone events are anchored to the
 *     PR's createdDate because we don't know WHEN each was applied. The text is
 *     phrased without a timestamp to avoid lying ("assigned to X" instead of
 *     "assigned X 2 hours ago").
 *   - State-change events (merged / closed) ARE accurately timestamped.
 *
 * If we later add a real timeline API, we swap the derivation for the live data
 * without changing the TimelineEvent rendering — the event shape is stable.
 */
function ConversationPanel({ pr, commitsQuery, checksQuery }: {
  pr: RemotePullRequest;
  commitsQuery: ReturnType<typeof useRepositoryPullRequestCommits>;
  checksQuery: ReturnType<typeof useRepositoryPullRequestChecks>;
}) {
  const events = buildTimelineEvents(pr, commitsQuery.data ?? []);
  const checks = checksQuery.data ?? [];

  return (
    <div className="prd-conv">
      <PrBodyCard pr={pr} />

      {events.length > 0 && (
        <div className="prd-timeline">
          {events.map((e, i) => <TimelineEvent key={i} event={e} />)}
        </div>
      )}

      {/* Checks card sits between the timeline and the merge-status card — mirrors
          GitHub's PR conversation layout where checks appear directly above the
          merge box. Hidden entirely when the provider returned no checks
          (no Actions / no pipelines / token lacks scope). */}
      {checks.length > 0 && <ChecksCard checks={checks} />}

      <MergeStatusCard pr={pr} />
    </div>
  );
}

/**
 * Checks card — summary banner (X passing / Y failing) on top, then one row per
 * check with status icon, name, duration, and a click-through to the provider's
 * logs. Ordered: failures first (most actionable), then pending (still going),
 * then successful (least interesting). Doesn't paginate — most PRs have <50
 * checks total; if a workflow exceeds that we'd add a "show more" toggle later.
 */
function ChecksCard({ checks }: { checks: RemotePullRequestCheck[] }) {
  const counts = checks.reduce(
    (acc, c) => { acc[c.status] = (acc[c.status] ?? 0) + 1; return acc; },
    {} as Record<RemotePullRequestCheck["status"], number>,
  );

  // Sort by priority — failures and pending first so the operator's eye lands
  // on the actionable rows.
  const orderPriority: Record<RemotePullRequestCheck["status"], number> = {
    Failure: 0, Pending: 1, Neutral: 2, Cancelled: 3, Skipped: 4, Success: 5,
  };
  const ordered = [...checks].sort((a, b) => orderPriority[a.status] - orderPriority[b.status]);

  const summary = buildChecksSummary(counts, checks.length);

  return (
    <div className="prd-card prd-checks">
      <div className="prd-checks-h">
        <Ic.Check size={14} />
        <span className="prd-checks-h-t">Checks</span>
        <span className="prd-checks-h-s">{summary}</span>
      </div>
      <div className="prd-checks-list">
        {ordered.map((c, i) => <ChecksRow key={`${c.name}-${i}`} check={c} />)}
      </div>
    </div>
  );
}

function ChecksRow({ check }: { check: RemotePullRequestCheck }) {
  const onOpen = () => {
    if (check.detailsUrl) window.open(check.detailsUrl, "_blank", "noopener");
  };
  return (
    <div className="prd-checks-row" data-status={check.status.toLowerCase()} onClick={onOpen}>
      <CheckStatusIcon status={check.status} />
      <span className="prd-checks-name">{check.name}</span>
      {check.durationSeconds != null && (
        <span className="prd-checks-dur">{formatDuration(check.durationSeconds)}</span>
      )}
      {check.detailsUrl && <Ic.ArrowOut size={11} className="prd-checks-link" />}
    </div>
  );
}

function CheckStatusIcon({ status }: { status: RemotePullRequestCheck["status"] }) {
  // Icons mirror GitHub's check-status visuals: green ✓ success, red ✗ failure,
  // yellow dot pending, grey for skipped/neutral/cancelled. No animation on
  // pending — keeping the card calm; the auto-refetch will swap the row out
  // when the check settles.
  switch (status) {
    case "Success": return <Ic.Check size={13} className="prd-checks-ic prd-checks-ic-ok" />;
    case "Failure": return <Ic.X size={13} className="prd-checks-ic prd-checks-ic-err" />;
    case "Pending": return <Ic.Clock size={13} className="prd-checks-ic prd-checks-ic-pending" />;
    case "Cancelled": return <Ic.X size={13} className="prd-checks-ic prd-checks-ic-muted" />;
    case "Skipped":
    case "Neutral":
    default:
      return <Ic.Dot size={13} className="prd-checks-ic prd-checks-ic-muted" />;
  }
}

function buildChecksSummary(counts: Partial<Record<RemotePullRequestCheck["status"], number>>, total: number): string {
  const parts: string[] = [];
  if (counts.Success) parts.push(`${counts.Success} passing`);
  if (counts.Failure) parts.push(`${counts.Failure} failing`);
  if (counts.Pending) parts.push(`${counts.Pending} running`);
  if (counts.Cancelled) parts.push(`${counts.Cancelled} cancelled`);
  if (counts.Skipped) parts.push(`${counts.Skipped} skipped`);
  if (counts.Neutral) parts.push(`${counts.Neutral} neutral`);
  if (parts.length === 0) return `${total} ${total === 1 ? "check" : "checks"}`;
  return parts.join(" · ");
}

/** Compact duration: `12s`, `4m 30s`, `1h 5m`. Saves the user from doing the math. */
function formatDuration(seconds: number): string {
  if (seconds < 60) return `${seconds}s`;
  const minutes = Math.floor(seconds / 60);
  const remSec = seconds % 60;
  if (minutes < 60) return remSec > 0 ? `${minutes}m ${remSec}s` : `${minutes}m`;
  const hours = Math.floor(minutes / 60);
  const remMin = minutes % 60;
  return remMin > 0 ? `${hours}h ${remMin}m` : `${hours}h`;
}

function PrBodyCard({ pr }: { pr: RemotePullRequest }) {
  return (
    <div className="prd-card">
      <div className="prd-card-h">
        <div className="prd-card-h-l">
          <div className="prd-avatar">{(pr.authorLogin ?? "?").charAt(0).toUpperCase()}</div>
          <div>
            <div className="prd-card-h-name">{pr.authorLogin ?? "unknown"}</div>
            <div className="prd-card-h-sub">commented {formatRelative(pr.createdDate)}</div>
          </div>
        </div>
      </div>
      <div className="prd-card-b">
        {pr.body && pr.body.trim().length > 0
          ? (
            // Markdown body with GitHub-flavored extensions: tables, task lists, autolinks,
            // strikethrough. Renderer is intentionally minimal — no syntax-highlighting plugin
            // yet (would add ~100KB for highlight.js; not worth it until someone asks). Links
            // open in a new tab so the user doesn't lose their place in the PR view.
            <div className="prd-body prd-markdown">
              <ReactMarkdown
                remarkPlugins={[remarkGfm]}
                components={{
                  a: ({ href, children, ...rest }) => (
                    <a href={href} target="_blank" rel="noopener noreferrer" {...rest}>{children}</a>
                  ),
                }}
              >
                {pr.body}
              </ReactMarkdown>
            </div>
          )
          : <div className="prd-body-empty">No description provided.</div>}
      </div>
    </div>
  );
}

// ── Timeline ─────────────────────────────────────────────────────────────────

type TimelineEventKind =
  | "commit"
  | "milestone"
  | "assignee"
  | "reviewer"
  | "label"
  | "state-merged"
  | "state-closed";

interface TimelineEventData {
  kind: TimelineEventKind;
  icon: React.ReactNode;
  /** Pre-rendered fragment so each callsite can mix bold names + plain text inline. */
  text: React.ReactNode;
  /** Optional ISO timestamp; rendered as "N hours ago" on the right. */
  date?: string;
  /** Optional trailing chip (e.g. commit short SHA). */
  trailing?: React.ReactNode;
  /** Optional click handler (e.g. open commit on provider). */
  onClick?: () => void;
}

function buildTimelineEvents(pr: RemotePullRequest, commits: RemotePullRequestCommit[]): TimelineEventData[] {
  const events: TimelineEventData[] = [];

  for (const c of commits) {
    events.push({
      kind: "commit",
      icon: <Ic.Branch size={12} />,
      text: (
        <>
          <span className="prd-evt-name">{c.authorLogin ?? c.authorName ?? "someone"}</span>
          {" pushed a commit: "}
          <span className="prd-evt-strong">{c.title}</span>
        </>
      ),
      date: c.authoredDate,
      trailing: <code className="prd-evt-sha">{c.shortSha}</code>,
      onClick: c.webUrl ? () => window.open(c.webUrl!, "_blank", "noopener") : undefined,
    });
  }

  if (pr.milestoneTitle) {
    events.push({
      kind: "milestone",
      icon: <Ic.Milestone size={12} />,
      text: <>added this to the <span className="prd-evt-strong">{pr.milestoneTitle}</span> milestone</>,
    });
  }

  for (const a of pr.assignees ?? []) {
    events.push({
      kind: "assignee",
      icon: <Ic.Users size={12} />,
      text: <>assigned <span className="prd-evt-name">{a}</span></>,
    });
  }

  for (const r of pr.requestedReviewers ?? []) {
    events.push({
      kind: "reviewer",
      icon: <Ic.Eye size={12} />,
      text: <>requested a review from <span className="prd-evt-name">{r}</span></>,
    });
  }

  for (const l of pr.labels) {
    events.push({
      kind: "label",
      icon: <Ic.Star size={12} />,
      text: <>added the <span className="prd-evt-strong">{l.name}</span> label</>,
    });
  }

  if (pr.state === "Merged") {
    events.push({
      kind: "state-merged",
      icon: <Ic.PrMerged size={12} />,
      text: (
        <>
          <span className="prd-evt-name">{pr.authorLogin ?? "someone"}</span>
          {" merged commit "}
          <span className="prd-evt-strong">{pr.sourceBranch}</span>
          {" into "}
          <span className="prd-evt-strong">{pr.targetBranch}</span>
        </>
      ),
      date: pr.mergedDate ?? pr.updatedDate,
    });
  } else if (pr.state === "Closed") {
    events.push({
      kind: "state-closed",
      icon: <Ic.PrClosed size={12} />,
      text: <>closed this pull request</>,
      date: pr.closedDate ?? pr.updatedDate,
    });
  }

  return events;
}

function TimelineEvent({ event }: { event: TimelineEventData }) {
  return (
    <div
      className="prd-evt"
      data-kind={event.kind}
      data-clickable={event.onClick != null}
      onClick={event.onClick}
    >
      <div className="prd-evt-icon">{event.icon}</div>
      <div className="prd-evt-text">{event.text}</div>
      <div className="prd-evt-right">
        {event.trailing}
        {event.date && <span className="prd-evt-time">{formatRelative(event.date)}</span>}
      </div>
    </div>
  );
}

// ── Merge status ─────────────────────────────────────────────────────────────

/**
 * Factual-summary card for terminal states only. We don't ship an in-app merge
 * action yet (no mergeability polling, no merge button), so an Open/Draft card
 * would be vapor — the state pill at the top already conveys those. Once real
 * merge wiring lands, Open/Draft branches can return here and the card layout
 * already accommodates them via the `data-kind` palette.
 */
function MergeStatusCard({ pr }: { pr: RemotePullRequest }) {
  if (pr.state === "Merged") {
    return (
      <div className="prd-merge" data-kind="merged">
        <div className="prd-merge-icon"><Ic.PrMerged size={18} /></div>
        <div className="prd-merge-body">
          <div className="prd-merge-h">Pull request merged</div>
          <div className="prd-merge-p">
            Merged {formatRelative(pr.mergedDate ?? pr.updatedDate)} into{" "}
            <code className="prd-evt-strong">{pr.targetBranch}</code>.
          </div>
        </div>
      </div>
    );
  }

  if (pr.state === "Closed") {
    return (
      <div className="prd-merge" data-kind="closed">
        <div className="prd-merge-icon"><Ic.PrClosed size={18} /></div>
        <div className="prd-merge-body">
          <div className="prd-merge-h">Pull request closed</div>
          <div className="prd-merge-p">
            Closed without merge {formatRelative(pr.closedDate ?? pr.updatedDate)}.
          </div>
        </div>
      </div>
    );
  }

  // Open / Draft — no card. State pill at the top already signals the state,
  // and the sidebar's "Open on provider" link covers the escape hatch.
  return null;
}

function CommitsPanel({ query }: { query: ReturnType<typeof useRepositoryPullRequestCommits> }) {
  if (query.isLoading) {
    return <div className="prd-card"><div className="pr-empty"><div className="pr-empty-h">Loading commits…</div></div></div>;
  }
  if (query.error instanceof ApiError) {
    return (
      <div className="cn-banner cn-banner-err">
        <div className="cn-banner-h">Couldn't load commits</div>
        <div className="cn-banner-p">{query.error.message}</div>
      </div>
    );
  }
  const commits = query.data ?? [];
  if (commits.length === 0) {
    return <div className="prd-card"><div className="pr-empty"><div className="pr-empty-h">No commits</div></div></div>;
  }

  return (
    <div className="prd-card">
      <div className="prd-commit-list">
        {commits.map(c => <CommitRow key={c.sha} commit={c} />)}
      </div>
    </div>
  );
}

function CommitRow({ commit }: { commit: RemotePullRequestCommit }) {
  const display = commit.authorLogin ?? commit.authorName ?? "unknown";
  return (
    <div className="prd-commit" onClick={() => commit.webUrl && window.open(commit.webUrl, "_blank", "noopener")}>
      <div className="prd-commit-avatar">{display.charAt(0).toUpperCase()}</div>
      <div className="prd-commit-main">
        <div className="prd-commit-title">{commit.title}</div>
        <div className="prd-commit-sub">
          <span className="prd-commit-author">{display}</span>
          <span className="pr-row-meta-sep">·</span>
          <span>{formatRelative(commit.authoredDate)}</span>
        </div>
      </div>
      <code className="prd-commit-sha">{commit.shortSha}</code>
    </div>
  );
}

function FilesPanel({ query, providerLabel }: { query: ReturnType<typeof useRepositoryPullRequestFiles>; providerLabel: string }) {
  // Selected file = the one whose diff is currently rendered. Defaults to the
  // first file once the query resolves. Local state, not URL — same rationale
  // as the tab selection above.
  const [selectedPath, setSelectedPath] = useState<string | null>(null);

  // Draggable width of the file-list pane, remembered across visits so long paths can be read in full.
  const [treeWidth, setTreeWidth] = useState<number>(() => {
    const saved = Number(localStorage.getItem(TREE_WIDTH_KEY));
    return saved >= TREE_WIDTH_MIN && saved <= TREE_WIDTH_MAX ? saved : TREE_WIDTH_DEFAULT;
  });
  const filesRef = useRef<HTMLDivElement>(null);

  // Resize the file list by dragging the splitter. During the drag we write the CSS var straight to
  // the DOM (no React re-render, so the heavy diff doesn't repaint on every mousemove); on release we
  // commit the final width to state + localStorage.
  function startResize(e: ReactMouseEvent) {
    e.preventDefault();
    const startX = e.clientX;
    const startW = treeWidth;
    let next = startW;

    const onMove = (ev: MouseEvent) => {
      next = Math.min(TREE_WIDTH_MAX, Math.max(TREE_WIDTH_MIN, startW + (ev.clientX - startX)));
      filesRef.current?.style.setProperty("--prd-tree-w", `${next}px`);
    };
    const onUp = () => {
      window.removeEventListener("mousemove", onMove);
      window.removeEventListener("mouseup", onUp);
      document.body.style.cursor = "";
      document.body.style.userSelect = "";
      setTreeWidth(next);
      localStorage.setItem(TREE_WIDTH_KEY, String(next));
    };

    window.addEventListener("mousemove", onMove);
    window.addEventListener("mouseup", onUp);
    document.body.style.cursor = "col-resize";
    document.body.style.userSelect = "none";
  }

  if (query.isLoading) {
    return <div className="prd-card"><div className="pr-empty"><div className="pr-empty-h">Loading files…</div></div></div>;
  }
  if (query.error instanceof ApiError) {
    return (
      <div className="cn-banner cn-banner-err">
        <div className="cn-banner-h">Couldn't load files</div>
        <div className="cn-banner-p">{query.error.message}</div>
      </div>
    );
  }
  const files = query.data ?? [];
  if (files.length === 0) {
    return <div className="prd-card"><div className="pr-empty"><div className="pr-empty-h">No file changes</div></div></div>;
  }

  // Pick the first file if nothing selected yet (or if the selection vanished after a refetch).
  const active = files.find(f => f.fileName === selectedPath) ?? files[0];

  return (
    <div className="prd-files" ref={filesRef} style={{ "--prd-tree-w": `${treeWidth}px` } as CSSProperties}>
      <div className="prd-file-tree">
        {files.map(f => (
          <div
            key={f.fileName}
            className="prd-file-tree-row"
            data-active={f.fileName === active.fileName}
            onClick={() => setSelectedPath(f.fileName)}
            title={f.fileName}
          >
            <span className="prd-file-tree-status" data-status={f.status.toLowerCase()}>
              {fileStatusGlyph(f.status)}
            </span>
            <span className="prd-file-tree-name">{f.fileName}</span>
            <span className="prd-file-tree-stats">
              <span className="prd-diff-add">+{f.additions}</span>
              <span className="prd-diff-del">−{f.deletions}</span>
            </span>
          </div>
        ))}
      </div>

      <div
        className="prd-files-splitter"
        role="separator"
        aria-orientation="vertical"
        title="Drag to resize the file list"
        onMouseDown={startResize}
      />

      <div className="prd-file-pane">
        <div className="prd-file-pane-h">
          <span className="prd-file-pane-name" title={active.fileName}>
            {active.status === "Renamed" && active.previousFileName
              ? <>{active.previousFileName} → {active.fileName}</>
              : active.fileName}
          </span>
          <span className="prd-file-pane-stats">
            <span className="prd-diff-add">+{active.additions}</span>
            <span className="prd-diff-del">−{active.deletions}</span>
          </span>
        </div>
        <div className="prd-file-pane-b">
          {active.patch
            ? <DiffViewer patch={active.patch} />
            : <div className="diff-empty">
                Diff suppressed — file is binary or too large to render inline.
                {" "}Open it on {providerLabel} to view the full change.
              </div>}
        </div>
      </div>
    </div>
  );
}

function PrSidebarBlock({ title, children }: { title: string; children: React.ReactNode }) {
  return (
    <div className="prd-side-block">
      <div className="prd-side-h">{title}</div>
      <div className="prd-side-b">{children}</div>
    </div>
  );
}

function UserChip({ login }: { login: string }) {
  return (
    <span className="prd-user-chip">
      <span className="prd-user-chip-avatar">{login.charAt(0).toUpperCase()}</span>
      <span>{login}</span>
    </span>
  );
}

function PrTab({ active, onClick, children }: { active: boolean; onClick: () => void; children: React.ReactNode }) {
  return (
    <button className="prd-tab" data-active={active} onClick={onClick}>
      {children}
    </button>
  );
}

function PrDetailBackLink({ onBack, label = "pull requests" }: { onBack: () => void; label?: string }) {
  return (
    <div className="prd-back">
      <button className="btn btn-ghost" onClick={onBack}>
        <Ic.ChevronLeft size={13} /> Back to {label}
      </button>
    </div>
  );
}

function PrStatePill({ state }: { state: PullRequestState }) {
  const label = state === "Draft" ? "Draft" : state === "Merged" ? "Merged" : state === "Closed" ? "Closed" : "Open";
  return (
    <span className="prd-pill" data-state={state.toLowerCase()}>
      <PrStateIcon state={state} />
      {label}
    </span>
  );
}

// ── Per-file helpers ──────────────────────────────────────────────────────────

function fileStatusGlyph(status: RemotePullRequestFile["status"]): string {
  if (status === "Added") return "A";
  if (status === "Removed") return "D";
  if (status === "Renamed") return "R";
  return "M";
}

/**
 * Trim deep paths to "…/parent/file.ext" so the tree column stays narrow.
 * The full path is in the row's `title` attribute for hover tooltip.
 */
// Bounds + storage key for the draggable file-list pane (see FilesPanel.startResize). The width is
// remembered per browser so long paths stay readable across visits.
const TREE_WIDTH_KEY = "codespace.prd-tree-w";
const TREE_WIDTH_MIN = 180;
const TREE_WIDTH_MAX = 640;
const TREE_WIDTH_DEFAULT = 280;

// ── Label pill ───────────────────────────────────────────────────────────────────

/**
 * One coloured label pill, used in both the PR list row and the PR detail sidebar.
 * Uses the provider's hex colour when available (GitHub: from Octokit's Label.Color;
 * GitLab: from the project-labels endpoint, plumbed through the backend's LabelRef).
 * Falls back to a deterministic palette derived from the label name when the
 * provider didn't supply a colour — keeps pills visually distinct without a
 * second network call.
 */
function PrLabel({ label }: { label: LabelRef }) {
  const bgHex = label.color ?? hashedLabelColor(label.name);
  const fg = pickReadableTextColor(bgHex);
  return (
    <span
      className="pr-row-label"
      style={{ background: `#${bgHex}`, color: fg, borderColor: `#${bgHex}` }}
      title={label.name}
    >
      {label.name}
    </span>
  );
}

/** Pleasant pastel-ish palette for the colour-fallback path. Picked to be visually
 *  distinct from one another at small sizes and to all work with dark text — keeps
 *  the contrast computation moot when the fallback path fires. */
const LABEL_FALLBACK_PALETTE = [
  "d4c5f9", "fef2c0", "c2e0c6", "f9d0c4", "bfdadc",
  "fbca04", "0e8a16", "1d76db", "5319e7", "d93f0b",
  "e99695", "c5def5", "bfe5bf", "fad8c7", "fef2c0",
];

function hashedLabelColor(name: string): string {
  let hash = 0;
  for (let i = 0; i < name.length; i++) {
    hash = ((hash << 5) - hash) + name.charCodeAt(i);
    hash |= 0;
  }
  return LABEL_FALLBACK_PALETTE[Math.abs(hash) % LABEL_FALLBACK_PALETTE.length];
}

/**
 * GitHub's algorithm for label text contrast: compute relative luminance from sRGB
 * and switch to white text below threshold, dark text above. Picks the boundary at
 * ~0.5 — matches the cutoff GitHub itself uses for its labels (verified against
 * the GitHub source: primer/primitives's `getTextColor` for label hex inputs).
 */
function pickReadableTextColor(hex: string): string {
  if (hex.length !== 6) return "#1a1a1a";
  const r = parseInt(hex.slice(0, 2), 16) / 255;
  const g = parseInt(hex.slice(2, 4), 16) / 255;
  const b = parseInt(hex.slice(4, 6), 16) / 255;
  const luminance = 0.2126 * r + 0.7152 * g + 0.0722 * b;
  return luminance > 0.55 ? "#1a1a1a" : "#ffffff";
}
