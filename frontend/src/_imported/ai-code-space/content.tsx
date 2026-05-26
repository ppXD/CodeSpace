import { useEffect, useMemo, useRef, useState } from "react";
import { createPortal } from "react-dom";

import { ApiError } from "@/api/request";
import type { CredentialSummary, ProviderInstanceSummary, ProviderKind, RepositorySummary } from "@/api/types";
import { useAlert, useConfirm } from "@/components/dialog";
import { useCredentials, useProviderInstances } from "@/hooks/use-credentials";
import { useRelinkRepositoryCredential, useRepositories, useUnbindRepository } from "@/hooks/use-repositories";

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
  const credentials = useCredentials();
  const unbind = useUnbindRepository();
  const confirm = useConfirm();

  const instanceById = useMemo(() => new Map((instances.data ?? []).map(i => [i.id, i])), [instances.data]);
  const allCredentials = credentials.data ?? [];

  const askUnbind = async (r: RepositorySummary) => {
    const ok = await confirm({
      title: `Unbind ${r.fullPath}?`,
      message: "The repository will be removed from CodeSpace and its remote webhook deleted (best-effort). The repo on the provider isn't touched.",
      confirmLabel: "Unbind",
      destructive: true,
    });
    if (!ok) return;
    unbind.mutate(r.id);
  };

  const rows = repositories.data ?? [];

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
          <span>Repositories</span>
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
            <button className="btn btn-primary" onClick={() => setAddOpen(true)}>
              <Ic.Plus size={14} /> Add repository
            </button>
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
                <th style={{ width: "46%" }}>Repository</th>
                <th>Source</th>
                <th>Last activity</th>
                <th className="col-right" />
              </tr>
            </thead>
            <tbody>
              {filtered.map(r => {
                const instance = instanceById.get(r.providerInstanceId);
                return (
                  <tr
                    key={r.id}
                    data-status={r.status.toLowerCase()}
                    onClick={() => onOpenRepo(r.fullPath)}
                  >
                    <td>
                      <div className="repo-cell">
                        {instance && <ProviderMark provider={instance.provider} />}
                        <div className="repo-info">
                          <div className="repo-name">
                            {r.name}
                            <RepoStatusBadge status={r.status} lastError={r.lastError ?? null} />
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
                          {/* Error-state CTA — always visible (not hover-only) because a broken
                              repo needs a clear, discoverable fix. The action sits on the row's
                              second line so it can't be missed even at a glance. */}
                          {r.status === "Error" && (
                            <RepoCredentialIssueAction
                              repository={r}
                              allCredentials={allCredentials}
                              onConnectNew={() => setConnectOpen(true)}
                            />
                          )}
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
                    <td className="synced">{formatRelative(r.lastEventDate ?? r.createdDate)}</td>
                    <td className="col-right">
                      <span className="row-act">
                        {/* Hover-only toolbar is for normal-state ops (Open / Unbind). The
                            re-link affordance was moved out — see RepoCredentialIssueAction
                            on the row's name cell, which is always visible on Error rows. */}
                        <button title="Open in provider" onClick={e => { e.stopPropagation(); window.open(r.webUrl, "_blank", "noopener"); }}>
                          <Ic.ArrowOut size={14} />
                        </button>
                        <button
                          title="Unbind"
                          onClick={e => { e.stopPropagation(); void askUnbind(r); }}
                          disabled={unbind.isPending && unbind.variables === r.id}
                        >
                          <Ic.X size={14} />
                        </button>
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
 * action lives in RepoCredentialIssueAction below the URL, which is always visible and
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

/**
 * Always-visible recovery affordance for Error-state rows. Sits inline on the row's
 * second line so the user sees it the instant the table loads — no hover discoverability
 * problem like the old toolbar-icon design had.
 *
 * Two click destinations depending on whether there's a candidate credential:
 *   • At least one active credential of the same provider → opens the picker popover.
 *   • Zero candidates → opens the picker with a "Connect new credential" CTA that
 *     opens the Providers modal (via onConnectNew). Avoids dead-ending the user.
 *
 * Portal'd popover with flip-up positioning, same recipe as the kebab in the provider
 * list — keeps the menu visible even on the last row near the table edge.
 */
function RepoCredentialIssueAction({
  repository,
  allCredentials,
  onConnectNew,
}: {
  repository: RepositorySummary;
  allCredentials: CredentialSummary[];
  onConnectNew: () => void;
}) {
  const [open, setOpen] = useState(false);
  const [pos, setPos] = useState<{ top: number; left: number } | null>(null);
  const triggerRef = useRef<HTMLButtonElement>(null);
  const relink = useRelinkRepositoryCredential();
  const alert = useAlert();

  const candidates = useMemo(() =>
    allCredentials.filter(c =>
      c.providerInstanceId === repository.providerInstanceId
      && c.status === "Active"
      && c.id !== repository.credentialId,
    ),
  [allCredentials, repository.providerInstanceId, repository.credentialId]);

  useEffect(() => {
    if (!open || !triggerRef.current) return;
    const POP_HEIGHT = Math.max(80, 48 + candidates.length * 36);
    const POP_GAP = 6;
    const rect = triggerRef.current.getBoundingClientRect();
    const spaceBelow = window.innerHeight - rect.bottom;
    const flipUp = spaceBelow < POP_HEIGHT + POP_GAP + 16;
    setPos({
      top: flipUp ? rect.top - POP_HEIGHT - POP_GAP : rect.bottom + POP_GAP,
      left: rect.left,
    });
  }, [open, candidates.length]);

  useEffect(() => {
    if (!open) return;
    const onClick = (e: MouseEvent) => {
      const t = e.target as HTMLElement;
      if (!t.closest(".relink-trigger") && !t.closest(".relink-pop")) setOpen(false);
    };
    const onScroll = () => setOpen(false);
    window.addEventListener("click", onClick);
    window.addEventListener("scroll", onScroll, true);
    return () => {
      window.removeEventListener("click", onClick);
      window.removeEventListener("scroll", onScroll, true);
    };
  }, [open]);

  const pick = async (credentialId: string) => {
    setOpen(false);
    try {
      await relink.mutateAsync({ repositoryId: repository.id, credentialId });
    } catch (err) {
      await alert({
        title: "Couldn't re-link credential",
        message: err instanceof Error ? err.message : "Unexpected error.",
        variant: "error",
      });
    }
  };

  const connectNew = () => {
    setOpen(false);
    onConnectNew();
  };

  const hasCandidates = candidates.length > 0;

  return (
    <div className="repo-fix">
      <button
        ref={triggerRef}
        className="repo-fix-cta relink-trigger"
        onClick={e => { e.stopPropagation(); setOpen(o => !o); }}
        disabled={relink.isPending}
      >
        <Ic.Link size={11} />
        {relink.isPending ? "Re-linking…" : hasCandidates ? "Re-link credential" : "Connect a credential to fix"}
      </button>
      {open && pos && createPortal(
        <div
          className="cn-row-pop relink-pop"
          style={{ position: "fixed", top: pos.top, left: pos.left, right: "auto" }}
          onClick={e => e.stopPropagation()}
        >
          <div className="relink-pop-head">
            {hasCandidates ? "Re-link to credential" : "No active credential"}
          </div>
          {hasCandidates ? (
            candidates.map(c => (
              <button key={c.id} className="sb-pop-item" onClick={() => void pick(c.id)}>
                <span className="relink-pop-name">{c.displayName}</span>
                {c.ownerUserName && <span className="relink-pop-owner">{c.ownerUserName}</span>}
              </button>
            ))
          ) : (
            <>
              <div className="relink-pop-empty">
                No other active credentials for this provider. Connect a new one — any team member
                can — then come back and pick it here.
              </div>
              <button className="sb-pop-item" onClick={connectNew}>
                <Ic.Plus size={12} /> Open Providers…
              </button>
            </>
          )}
        </div>,
        document.body,
      )}
    </div>
  );
}

export { PROVIDER_LABEL };
export type { RepositorySummary, ProviderInstanceSummary };
