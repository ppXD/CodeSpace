import { useState } from "react";
import ReactMarkdown from "react-markdown";
import remarkGfm from "remark-gfm";

import { ApiError } from "@/api/request";
import type { RemoteBranch, RemoteCommitSummary, RemoteFileContent, RemoteLanguage, RemoteRepositoryStats, RemoteTreeEntry } from "@/api/types";
import {
  useRepository,
  useRepositoryBranches,
  useRepositoryFile,
  useRepositoryLanguages,
  useRepositoryLatestCommit,
  useRepositoryStats,
  useRepositoryTree,
  useRepositoryTreeCommits,
} from "@/hooks/use-repositories";
import { buildBreadcrumbs, formatBytes, formatCount, isMarkdownName, languageColor, parentPath, pickReadme, relativeTime, sortTreeEntries } from "@/lib/codeTree";

import { Ic } from "./icons";

interface CodeBrowserBodyProps {
  repoId: string;
}

/**
 * The "Code" tab — a GitHub/GitLab-style, branch-scoped source browser. A two-column layout: the file
 * tree + viewer on the left, a stats sidebar (stars/forks/counts/storage) and Languages bar on the right.
 * Above the file list sits the latest-commit bar, and each row carries its own last-commit + time.
 *
 * Branch / path / open-file are local state (URL stays clean, like the PR list). Switching branch resets
 * to the root because a deep path may not exist on the other branch.
 */
export function CodeBrowserBody({ repoId }: CodeBrowserBodyProps) {
  const repository = useRepository(repoId);
  const branches = useRepositoryBranches(repoId);
  const stats = useRepositoryStats(repoId);
  const languages = useRepositoryLanguages(repoId);

  const [branch, setBranch] = useState<string | null>(null);
  const [path, setPath] = useState("");
  const [file, setFile] = useState<string | null>(null);

  const repo = repository.data;
  const ref = branch ?? repo?.defaultBranch ?? null;
  const inTree = file === null;

  const tree = useRepositoryTree(repoId, path, ref, repo != null && inTree);
  const fileContent = useRepositoryFile(repoId, file, ref, file !== null);

  // Sorted once: drives the list, the README lookup, and the per-entry commit fetch.
  const entries = inTree && tree.data ? sortTreeEntries(tree.data) : [];
  const entryPaths = entries.map(e => e.path);

  const latestCommit = useRepositoryLatestCommit(repoId, path, ref, repo != null && inTree);
  const treeCommits = useRepositoryTreeCommits(repoId, entryPaths, ref, entryPaths.length > 0);

  const readmeEntry = path === "" && inTree && tree.data ? pickReadme(tree.data) : null;
  const readme = useRepositoryFile(repoId, readmeEntry?.path ?? null, ref, readmeEntry != null);

  if (!repo) return null;

  const goToRoot = () => { setFile(null); setPath(""); };
  const goToCrumb = (crumbPath: string) => { setFile(null); setPath(crumbPath); };
  const goUp = () => { setFile(null); setPath(parentPath(path)); };
  const openEntry = (entry: RemoteTreeEntry) => {
    if (entry.type === "Directory") { setFile(null); setPath(entry.path); }
    else setFile(entry.path);
  };
  const changeBranch = (name: string) => { setBranch(name); setPath(""); setFile(null); };

  const crumbs = buildBreadcrumbs(file ?? path);

  return (
    <div className="cb">
      <div className="cb-main">
        <div className="cb-bar">
          <BranchPicker branches={branches.data ?? []} current={ref} loading={branches.isLoading} onPick={changeBranch} />

          <nav className="cb-crumbs" aria-label="Path">
            <button className="cb-crumb cb-crumb-root" onClick={goToRoot} disabled={crumbs.length === 0}>
              <Ic.Repo size={13} /> {repo.name}
            </button>
            {crumbs.map((c, i) => {
              const isLast = i === crumbs.length - 1;
              return (
                <span key={c.path} className="cb-crumb-seg">
                  <span className="cb-crumb-sep">/</span>
                  {isLast
                    ? <span className="cb-crumb cb-crumb-cur">{c.name}</span>
                    : <button className="cb-crumb" onClick={() => goToCrumb(c.path)}>{c.name}</button>}
                </span>
              );
            })}
          </nav>
        </div>

        {file !== null
          ? <FileView content={fileContent.data} isLoading={fileContent.isLoading} error={fileContent.error} webUrl={repo.webUrl} />
          : (
            <>
              <LatestCommitBar commit={latestCommit.data} totalCommits={stats.data?.commitCount} />
              <TreeList
                entries={entries}
                commits={treeCommits.data}
                isLoading={tree.isLoading}
                error={tree.error}
                showUp={path !== ""}
                onUp={goUp}
                onOpen={openEntry}
              />
              {readmeEntry && <ReadmeCard name={readmeEntry.name} content={readme.data} isLoading={readme.isLoading} />}
            </>
          )}
      </div>

      <aside className="cb-side">
        <StatsPanel stats={stats.data} />
        <LanguagesPanel languages={languages.data} />
      </aside>
    </div>
  );
}

// ── Branch picker ─────────────────────────────────────────────────────────────

interface BranchPickerProps {
  branches: RemoteBranch[];
  current: string | null;
  loading: boolean;
  onPick: (name: string) => void;
}

/** Warm-themed dropdown (a transparent fixed backdrop catches the outside click to close — no portal). */
function BranchPicker({ branches, current, loading, onPick }: BranchPickerProps) {
  const [open, setOpen] = useState(false);

  return (
    <div className="cb-branch">
      <button className="cb-branch-btn" onClick={() => setOpen(o => !o)} disabled={loading && branches.length === 0}>
        <Ic.Branch size={13} />
        <span className="cb-branch-name">{current ?? "…"}</span>
        <Ic.ChevronDown size={13} />
      </button>

      {open && (
        <>
          <div className="cb-branch-backdrop" onClick={() => setOpen(false)} />
          <div className="cb-branch-menu" role="listbox">
            {branches.length === 0
              ? <div className="cb-branch-empty">{loading ? "Loading branches…" : "No branches"}</div>
              : branches.map(b => (
                <button
                  key={b.name}
                  role="option"
                  aria-selected={b.name === current}
                  data-active={b.name === current}
                  className="cb-branch-opt"
                  onClick={() => { onPick(b.name); setOpen(false); }}
                >
                  <Ic.Branch size={12} />
                  <span className="cb-branch-opt-name">{b.name}</span>
                  {b.isDefault && <span className="cb-branch-default">default</span>}
                </button>
              ))}
          </div>
        </>
      )}
    </div>
  );
}

// ── Latest-commit header bar ────────────────────────────────────────────────

function LatestCommitBar({ commit, totalCommits }: { commit: RemoteCommitSummary | null | undefined; totalCommits?: number | null }) {
  if (!commit) return null;

  return (
    <div className="cb-commitbar">
      <span className="cb-commitbar-avatar">{(commit.authorName ?? "?").trim().charAt(0).toUpperCase() || "?"}</span>
      {commit.authorName && <span className="cb-commitbar-author">{commit.authorName}</span>}

      {commit.webUrl
        ? <a className="cb-commitbar-msg" href={commit.webUrl} target="_blank" rel="noopener noreferrer" title={commit.message}>{commit.message}</a>
        : <span className="cb-commitbar-msg" title={commit.message}>{commit.message}</span>}

      <span className="cb-commitbar-meta">
        <code>{commit.shortSha}</code>
        {commit.committedDate && <span>· {relativeTime(commit.committedDate)}</span>}
      </span>

      {totalCommits != null && (
        <span className="cb-commitbar-count"><Ic.Commit size={14} /> {formatCount(totalCommits)} commits</span>
      )}
    </div>
  );
}

// ── File tree (3 columns: name · last commit · time) ──────────────────────────

interface TreeListProps {
  entries: RemoteTreeEntry[];
  commits: Record<string, RemoteCommitSummary> | undefined;
  isLoading: boolean;
  error: unknown;
  showUp: boolean;
  onUp: () => void;
  onOpen: (entry: RemoteTreeEntry) => void;
}

function TreeList({ entries, commits, isLoading, error, showUp, onUp, onOpen }: TreeListProps) {
  if (error instanceof ApiError) return <SourceError message={error.message} />;
  if (isLoading && entries.length === 0) return <div className="cb-loading">Loading…</div>;

  return (
    <div className="cb-tree">
      {showUp && (
        <button className="cb-row cb-row-up" onClick={onUp}>
          <span className="cb-row-main"><Ic.Folder size={15} /><span className="cb-row-name">..</span></span>
          <span className="cb-row-commit" />
          <span className="cb-row-time" />
        </button>
      )}

      {entries.length === 0 && !showUp
        ? <div className="cb-empty">This folder is empty.</div>
        : entries.map(entry => {
          const isDir = entry.type === "Directory";
          const commit = commits?.[entry.path];
          return (
            <button key={entry.path} className="cb-row" data-kind={isDir ? "dir" : "file"} onClick={() => onOpen(entry)}>
              <span className="cb-row-main">
                {isDir ? <Ic.Folder size={15} /> : <Ic.File size={15} />}
                <span className="cb-row-name">{entry.name}</span>
              </span>
              <span className="cb-row-commit">
                {commit && (commit.webUrl
                  ? <a href={commit.webUrl} target="_blank" rel="noopener noreferrer" title={commit.message} onClick={e => e.stopPropagation()}>{commit.message}</a>
                  : <span title={commit.message}>{commit.message}</span>)}
              </span>
              <span className="cb-row-time">{commit?.committedDate ? relativeTime(commit.committedDate) : ""}</span>
            </button>
          );
        })}
    </div>
  );
}

// ── File viewer ─────────────────────────────────────────────────────────────

interface FileViewProps {
  content: RemoteFileContent | undefined;
  isLoading: boolean;
  error: unknown;
  webUrl: string;
}

function FileView({ content, isLoading, error, webUrl }: FileViewProps) {
  if (error instanceof ApiError) return <SourceError message={error.message} />;
  if (isLoading && !content) return <div className="cb-loading">Loading…</div>;
  if (!content) return null;

  if (content.isTruncated) {
    return (
      <div className="cb-file-notice">
        <Ic.File size={20} />
        <div className="cb-file-notice-h">File too large to display</div>
        <div className="cb-file-notice-p">{formatBytes(content.size)} — open it on the provider to view.</div>
        <button className="btn" style={{ marginTop: 12 }} onClick={() => window.open(webUrl, "_blank", "noopener")}>
          <Ic.ArrowOut size={13} /> View on provider
        </button>
      </div>
    );
  }

  if (content.isBinary) {
    return (
      <div className="cb-file-notice">
        <Ic.File size={20} />
        <div className="cb-file-notice-h">Binary file not shown</div>
        <div className="cb-file-notice-p">{formatBytes(content.size)}</div>
      </div>
    );
  }

  const text = content.text ?? "";

  if (isMarkdownName(content.name)) {
    return <div className="cb-file"><Markdown source={text} /></div>;
  }

  const lines = text.split("\n");

  return (
    <div className="cb-file">
      <div className="cb-code">
        {lines.map((line, i) => (
          <div className="cb-code-row" key={i}>
            <span className="cb-code-gutter">{i + 1}</span>
            <span className="cb-code-text">{line || " "}</span>
          </div>
        ))}
      </div>
    </div>
  );
}

// ── README card ─────────────────────────────────────────────────────────────

function ReadmeCard({ name, content, isLoading }: { name: string; content: RemoteFileContent | undefined; isLoading: boolean }) {
  return (
    <div className="cb-readme">
      <div className="cb-readme-head"><Ic.Book size={14} /> {name}</div>
      <div className="cb-readme-body">
        {isLoading && !content
          ? <div className="cb-loading">Loading…</div>
          : content?.text
            ? <Markdown source={content.text} />
            : <div className="cb-empty">Couldn't render this README.</div>}
      </div>
    </div>
  );
}

// ── Right rail: stats + languages ─────────────────────────────────────────────

function StatsPanel({ stats }: { stats: RemoteRepositoryStats | undefined }) {
  if (!stats) return null;

  const rows: Array<{ key: string; icon: React.ReactNode; value: string; label: string }> = [];
  if (stats.commitCount != null) rows.push({ key: "commits", icon: <Ic.Commit size={15} />, value: formatCount(stats.commitCount), label: "Commits" });
  if (stats.branchCount != null) rows.push({ key: "branches", icon: <Ic.Branch size={15} />, value: formatCount(stats.branchCount), label: "Branches" });
  if (stats.tagCount != null) rows.push({ key: "tags", icon: <Ic.Tag size={15} />, value: formatCount(stats.tagCount), label: "Tags" });
  if (stats.releaseCount != null) rows.push({ key: "releases", icon: <Ic.Release size={15} />, value: formatCount(stats.releaseCount), label: "Releases" });
  if (stats.storageBytes != null) rows.push({ key: "storage", icon: <Ic.Storage size={15} />, value: formatBytes(stats.storageBytes), label: "Storage" });

  const hasSocial = stats.stars != null || stats.forks != null;
  if (rows.length === 0 && !hasSocial) return null;

  return (
    <div className="cb-side-card">
      <div className="cb-side-title">About</div>

      {hasSocial && (
        <div className="cb-side-social">
          {stats.stars != null && <span className="cb-social-item"><Ic.Star size={14} /> {formatCount(stats.stars)} <span className="cb-social-label">stars</span></span>}
          {stats.forks != null && <span className="cb-social-item"><Ic.Fork size={14} /> {formatCount(stats.forks)} <span className="cb-social-label">forks</span></span>}
        </div>
      )}

      <div className="cb-stats">
        {rows.map(r => (
          <div className="cb-stat" key={r.key}>
            {r.icon}
            <b>{r.value}</b>
            <span className="cb-stat-label">{r.label}</span>
          </div>
        ))}
      </div>
    </div>
  );
}

function LanguagesPanel({ languages }: { languages: RemoteLanguage[] | undefined }) {
  if (!languages || languages.length === 0) return null;

  return (
    <div className="cb-side-card">
      <div className="cb-side-title">Languages</div>

      <div className="cb-lang-bar">
        {languages.map(l => (
          <span key={l.name} className="cb-lang-seg" style={{ width: `${l.percent}%`, background: languageColor(l.name) }} title={`${l.name} ${l.percent}%`} />
        ))}
      </div>

      <div className="cb-lang-legend">
        {languages.map(l => (
          <span key={l.name} className="cb-lang-item">
            <span className="cb-lang-dot" style={{ background: languageColor(l.name) }} />
            <b>{l.name}</b> <span className="cb-lang-pct">{l.percent}%</span>
          </span>
        ))}
      </div>
    </div>
  );
}

// ── Shared bits ─────────────────────────────────────────────────────────────

/** GitHub-flavored markdown with links opening in a new tab — mirrors the PR-body renderer. */
function Markdown({ source }: { source: string }) {
  return (
    <div className="prd-markdown">
      <ReactMarkdown
        remarkPlugins={[remarkGfm]}
        components={{
          a: ({ href, children, ...rest }) => (
            <a href={href} target="_blank" rel="noopener noreferrer" {...rest}>{children}</a>
          ),
        }}
      >
        {source}
      </ReactMarkdown>
    </div>
  );
}

function SourceError({ message }: { message: string }) {
  return (
    <div className="cn-banner cn-banner-err">
      <div className="cn-banner-h">Couldn't load source</div>
      <div className="cn-banner-p">{message}</div>
    </div>
  );
}
