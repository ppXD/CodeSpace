import { useMemo, useState } from "react";

import { ApiError } from "@/api/request";
import type { CredentialSummary, ProviderInstanceSummary, ProviderKind, RepositorySummary } from "@/api/types";
import { useConfirm } from "@/components/dialog";
import { ConnectionIssueChip, RepositoryConnectionFix } from "@/components/remedy/RepositoryConnectionIssue";
import { useRepositoryConnectionIssues } from "@/hooks/use-repository-connection-issues";
import { useProviderInstances } from "@/hooks/use-credentials";
import { useRepositories, useUnbindRepository } from "@/hooks/use-repositories";
import { TeamPermissions, useTeamPermissions } from "@/hooks/use-team-management";
import { credentialOwnershipLabel } from "@/lib/credentialOrdering";

import { AddRepoModal } from "./add-repo-modal";
import { ConnectRemoteModal } from "./connect-remote-modal";
import { Ic } from "./icons";

/**
 * Repository list. Backed entirely by GET /api/repositories — the older `REPOS` mock is
 * gone. Tabs are pure client-side filters over the live list; provider-instance counts
 * come from joining each row to its ProviderInstanceSummary by id.
 *
 * URL-driven: `tab` and `query` are owned by the route (`_app.index.tsx`) and passed in
 * as props. Row clicks call `onOpenRepo(fullPath)` which the route turns into a navigation
 * to `/teams/{slug}/repositories/{fullPath}` — no internal modal/detail render lives here.
 */

const PROVIDER_INITIALS: Record<ProviderKind, string> = { GitHub: "GH", GitLab: "GL", Git: "G" };
const PROVIDER_LABEL: Record<ProviderKind, string> = { GitHub: "GitHub", GitLab: "GitLab", Git: "Git" };

interface ProviderMarkProps {
  provider: ProviderKind;
  size?: number;
}

export function ProviderMark({ provider, size = 30 }: ProviderMarkProps) {
  return (
    <div
      className="repo-mark"
      data-p={provider.toLowerCase()}
      style={{ width: size, height: size, borderRadius: size <= 24 ? 6 : 7 }}
    >
      <span className="pm-glyph" style={{ fontSize: size <= 24 ? 11 : 13 }}>{PROVIDER_INITIALS[provider]}</span>
    </div>
  );
}

/**
 * One repositories-table cell answering "whose credential is this repo bound through?".
 * Resolves to the credential's owner so the team can tell at a glance whether a repo runs
 * through the shared team-service identity (accent) or one person's personal sign-in (their
 * name). The tooltip carries the exact credential name; "—" means nothing is bound (a broken
 * or never-linked repo, or the credential was deleted). Shared by the team-level repository
 * list and the project detail list so both read identically.
 */
export function RepoCredentialCell({ credential }: { credential: CredentialSummary | undefined }) {
  if (!credential) return <span className="cred-none">—</span>;
  return (
    <span className="cred-tag" data-team={credential.ownership === "TeamService"} title={credential.displayName}>
      <Ic.Key size={13} />
      <span className="cred-tag-who">{credentialOwnershipLabel(credential)}</span>
    </span>
  );
}

export type Tab = "all" | "github" | "gitlab" | "git" | "archived";

interface RepositoryListPageProps {
  tab: Tab;
  query: string;
  onTabChange: (next: Tab) => void;
  onQueryChange: (next: string) => void;
  /** Receives the repository's provider-side `fullPath` (e.g. "acme/postboy.api"),
   *  NOT the UUID. The route layer URL-encodes it before threading into the URL,
   *  and the matching route resolves it back to a UUID for downstream API calls. */
  onOpenRepo: (repoFullPath: string) => void;
}

/**
 * Repository list page (URL-driven). The route owns `tab` + `query` as search-param
 * state so a `?tab=github&q=api` link is shareable. Row click hands the repo id back
 * to the route, which navigates to `/repos/{id}` — opening a repo is a real URL change,
 * not an in-place swap, so the browser's back button works and links are shareable.
 *
 * Modal state (add / connect) stays local — modals are ephemeral chrome with no need
 * for deep-linking; an `?modal=add` URL would be confusing to receive in a paste.
 */
export function RepositoryListPage({ tab, query, onTabChange, onQueryChange, onOpenRepo }: RepositoryListPageProps) {
  const [addOpen, setAddOpen] = useState(false);
  const [connectOpen, setConnectOpen] = useState(false);

  const repositories = useRepositories();
  const instances = useProviderInstances();
  const connection = useRepositoryConnectionIssues();
  const unbind = useUnbindRepository();
  const confirm = useConfirm();
  // Adding (a bind) and unbinding a repository take repos.manage on the server. Without it the controls are absent rather than
  // present-and-refused; the connection chip still says what is wrong, and the fix beside it offers only what the caller may do.
  const mayManageRepos = useTeamPermissions().can(TeamPermissions.ReposManage);

  const instanceById = useMemo(() => new Map((instances.data ?? []).map(i => [i.id, i])), [instances.data]);
  const credentialById = connection.credentialById;

  const askUnbind = async (r: RepositorySummary) => {
    const ok = await confirm({
      title: `Unbind ${r.fullPath}?`,
      message: "The repository will be removed from CodeSpace and its remote webhook deleted (best-effort). The repo on the provider isn't touched.",
      confirmLabel: "Unbind",
      destructive: true,
    });
    if (!ok) return;
    unbind.mutate({ repositoryId: r.id });   // team-level list → remove the repo entirely (all projects)
  };

  const rows = repositories.data ?? [];
  // How many of the team's repositories cannot authenticate right now — the number a person scanning the page needs before any row.
  const brokenCount = rows.filter(r => connection.issueOf(r) !== null).length;

  const filtered = rows.filter(r => {
    const instance = instanceById.get(r.providerInstanceId);
    if (!instance) return false;

    if (tab === "github" && instance.provider !== "GitHub") return false;
    if (tab === "gitlab" && instance.provider !== "GitLab") return false;
    if (tab === "git" && instance.provider !== "Git") return false;
    if (tab === "archived" && r.status !== "Paused") return false;
    if (tab !== "archived" && r.status === "Paused") return false;

    if (query && !r.name.toLowerCase().includes(query.toLowerCase()) && !r.fullPath.toLowerCase().includes(query.toLowerCase())) return false;
    return true;
  });

  const counts: Record<Tab, number> = {
    all: rows.filter(r => r.status !== "Paused").length,
    github: rows.filter(r => r.status !== "Paused" && instanceById.get(r.providerInstanceId)?.provider === "GitHub").length,
    gitlab: rows.filter(r => r.status !== "Paused" && instanceById.get(r.providerInstanceId)?.provider === "GitLab").length,
    git: rows.filter(r => r.status !== "Paused" && instanceById.get(r.providerInstanceId)?.provider === "Git").length,
    archived: rows.filter(r => r.status === "Paused").length,
  };

  const tabs: [Tab, string][] = [
    ["all", "All"],
    ["github", "GitHub"],
    ["gitlab", "GitLab"],
    ["git", "Self-hosted"],
    ["archived", "Archived"],
  ];

  return (
    <section className="ct">
      <div className="ct-head">
        <div className="ct-crumbs">
          <span className="cur">Repositories</span>
        </div>
        <div className="ct-title-row">
          <div>
            <h1 className="ct-title">Repositories</h1>
            <div className="ct-sub">
              Connect repositories from GitHub or GitLab via OAuth. Data is fetched live from the
              source — nothing is cloned or synced locally.
            </div>
          </div>
          <div className="ct-actions">
            <button className="btn" onClick={() => setConnectOpen(true)}>
              <Ic.Link size={14} /> Connect remote
            </button>
            {mayManageRepos && (
              <button className="btn btn-primary" onClick={() => setAddOpen(true)}>
                <Ic.Plus size={14} /> Add repository
              </button>
            )}
          </div>
        </div>
        <div className="ct-tabs">
          {tabs.map(([id, label]) => (
            <div
              key={id}
              className="ct-tab"
              data-active={tab === id}
              onClick={() => onTabChange(id)}
            >
              {label}
              <span className="ct-tab-c">{counts[id]}</span>
            </div>
          ))}
        </div>
      </div>

      <div className="ct-toolbar">
        <div className="ct-search">
          <Ic.Search size={13} />
          <input
            placeholder="Search repositories…"
            value={query}
            onChange={e => onQueryChange(e.target.value)}
          />
        </div>
        <div className="ct-spacer" />
        <button className="btn btn-ghost"><Ic.Sort size={13} /> Recently active</button>
        <button className="btn btn-icon"><Ic.More size={14} /></button>
      </div>

      <div className="ct-body">
        {brokenCount > 0 && (
          <div className="cn-banner cn-banner-warn" role="status" style={{ margin: "16px 16px 0" }}>
            <div className="cn-banner-h">{brokenCount === 1 ? "1 repository needs a credential" : `${brokenCount} repositories need a credential`}</div>
            <div className="cn-banner-p">Their credential was refused, disconnected or retired, so runs and webhooks through {brokenCount === 1 ? "it" : "them"} fail. Each one below says how to fix it.</div>
          </div>
        )}
        {repositories.isLoading && (
          <div className="ct-empty"><div className="ct-empty-h">Loading…</div></div>
        )}

        {repositories.error instanceof ApiError && (
          <div className="cn-banner cn-banner-err" style={{ margin: 16 }}>
            <div className="cn-banner-h">Couldn't load repositories</div>
            <div className="cn-banner-p">{repositories.error.message}</div>
          </div>
        )}

        {!repositories.isLoading && !repositories.error && (
          <table className="tbl">
            <thead>
              <tr>
                <th style={{ width: "40%" }}>Repository</th>
                <th>Source</th>
                <th>Credential</th>
                <th>Last activity</th>
                <th className="col-right" />
              </tr>
            </thead>
            <tbody>
              {filtered.map(r => {
                const instance = instanceById.get(r.providerInstanceId);
                const credential = r.credentialId ? credentialById.get(r.credentialId) : undefined;
                const issue = connection.issueOf(r);
                return (
                  <tr
                    key={r.id}
                    data-status={r.status.toLowerCase()}
                    data-issue={issue ? issue.kind : undefined}
                    onClick={() => onOpenRepo(r.fullPath)}
                  >
                    <td>
                      <div className="repo-cell">
                        {instance && <ProviderMark provider={instance.provider} />}
                        <div className="repo-info">
                          <div className="repo-name">
                            {r.name}
                            {issue ? <ConnectionIssueChip issue={issue} /> : <RepoStatusBadge status={r.status} lastError={r.lastError ?? null} />}
                          </div>
                          <div className="repo-path">
                            <span>{r.fullPath}</span>
                            <span>·</span>
                            <span className="repo-vis">
                              {r.visibility === "Private" ? <Ic.Lock size={10} />
                                : r.visibility === "Public" ? <Ic.Globe size={10} />
                                  : <Ic.Users size={10} />}
                              {r.visibility.toLowerCase()}
                            </span>
                          </div>
                          {/* A broken connection's way out — always visible (not hover-only) because a broken repo needs a clear,
                              discoverable fix, and shown for ANY repository whose credential is not healthy, not only the ones
                              already flipped to Error: a repository whose credential was refused or disconnected stays "Active" and
                              fails every run until someone looks. */}
                          {issue && <RepositoryConnectionFix repository={r} issue={issue} onConnectNew={() => setConnectOpen(true)} />}
                        </div>
                      </div>
                    </td>
                    <td>
                      {instance && (
                        <span className="src-tag">
                          <ProviderMark provider={instance.provider} size={20} />
                          <span className="src-tag-meta">
                            <span className="src-tag-name">{PROVIDER_LABEL[instance.provider]}</span>
                            <span className="src-tag-acct">{instance.displayName}</span>
                          </span>
                        </span>
                      )}
                    </td>
                    <td><RepoCredentialCell credential={credential} /></td>
                    <td className="synced">{formatRelative(r.lastEventDate ?? r.createdDate)}</td>
                    <td className="col-right">
                      <span className="row-act">
                        {/* Hover-only toolbar is for normal-state ops (Open / Unbind). The fix for a broken connection is not
                            here — see RepositoryConnectionFix on the row's name cell, which is always visible. */}
                        <button title="Open in provider" onClick={e => { e.stopPropagation(); window.open(r.webUrl, "_blank", "noopener"); }}>
                          <Ic.ArrowOut size={14} />
                        </button>
                        {mayManageRepos && (
                          <button
                            title="Unbind"
                            onClick={e => { e.stopPropagation(); void askUnbind(r); }}
                            disabled={unbind.isPending && unbind.variables?.repositoryId === r.id}
                          >
                            <Ic.X size={14} />
                          </button>
                        )}
                      </span>
                    </td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        )}

        {!repositories.isLoading && !repositories.error && filtered.length === 0 && (
          <div className="ct-empty">
            <div className="ct-empty-h">No repositories</div>
            <div>
              {rows.length === 0
                ? "Connect a Git host, then add your first repository."
                : "Try a different search or filter."}
            </div>
          </div>
        )}
      </div>

      {addOpen && <AddRepoModal onClose={() => setAddOpen(false)} />}
      {connectOpen && <ConnectRemoteModal onClose={() => setConnectOpen(false)} />}
    </section>
  );
}

function formatRelative(iso?: string | null) {
  if (!iso) return "never";
  const then = new Date(iso).getTime();
  const seconds = Math.max(0, Math.round((Date.now() - then) / 1000));

  if (seconds < 60) return "just now";
  if (seconds < 3600) return `${Math.floor(seconds / 60)}m ago`;
  if (seconds < 86400) return `${Math.floor(seconds / 3600)}h ago`;
  if (seconds < 604800) return `${Math.floor(seconds / 86400)}d ago`;
  return new Date(iso).toLocaleDateString();
}

/**
 * Inline status pill next to the repo name. Active = nothing shown. Other statuses get a
 * compact label chip. For Error specifically, the chip is PURELY informational here — the
 * action lives in RepositoryConnectionFix below the URL, which is always visible and
 * can't be missed. Keeping the chip + action separate stops the chip from over-promising
 * interactivity at every screen size.
 */
function RepoStatusBadge({ status, lastError }: { status: RepositorySummary["status"]; lastError: string | null }) {
  if (status === "Active") return null;

  const labelByStatus: Record<RepositorySummary["status"], string> = {
    Active: "active",
    Paused: "paused",
    Error: "needs new credential",
    Unreachable: "unreachable",
  };
  const className =
    status === "Error" ? "cn-status cn-status-warn"
      : status === "Unreachable" ? "cn-status cn-status-error"
        : "cn-status";
  const tooltip = lastError || labelByStatus[status];

  return (
    <span className={className} title={tooltip}>
      {status === "Error" || status === "Unreachable" ? <Ic.Triangle size={10} /> : null}
      {labelByStatus[status]}
    </span>
  );
}

export { PROVIDER_LABEL };
export type { RepositorySummary, ProviderInstanceSummary };
