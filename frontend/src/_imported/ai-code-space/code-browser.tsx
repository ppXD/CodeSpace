import { useMemo, useState, type ReactNode } from "react";
import ReactMarkdown from "react-markdown";
import rehypeRaw from "rehype-raw";
import rehypeSanitize, { defaultSchema } from "rehype-sanitize";
import remarkGfm from "remark-gfm";

import { ApiError } from "@/api/request";
import type { RemoteBranch, RemoteCommitSummary, RemoteFileContent, RemoteLanguage, RemoteRelease, RemoteRepositoryStats, RemoteTreeEntry, RepositoryDetail } from "@/api/types";
import { useProviderInstances } from "@/hooks/use-credentials";
import {
  useRenderMarkdown,
  useRepository,
  useRepositoryBranches,
  useRepositoryFile,
  useRepositoryLanguages,
  useRepositoryLatestCommit,
  useRepositoryLatestRelease,
  useRepositoryStats,
  useRepositoryTree,
  useRepositoryTreeCommits,
} from "@/hooks/use-repositories";
import { buildBreadcrumbs, formatBytes, formatCount, isMarkdownName, languageColor, parentPath, pickLicense, pickReadme, relativeTime, sortTreeEntries } from "@/lib/codeTree";
import { prepareProviderHtml } from "@/lib/providerHtml";
import { blobUrl, resolveReadmeUrl } from "@/lib/repoUrls";

import { Ic } from "./icons";

interface CodeBrowserBodyProps {
  repoId: string;
}

/**
 * The "Code" tab — GitHub/GitLab-style. The repo name + Public/Private + Star/Fork live in the page
 * header (RepoDetailHeader) above the tab strip; this body is the rest: a toolbar (branch · N branches ·
 * N tags · search · Code-with-clone), the file tree + viewer, and a right rail (About + Languages).
 *
 * Branch / path / open-file / filter are local state. Switching branch or navigating resets the filter.
 */
export function CodeBrowserBody({ repoId }: CodeBrowserBodyProps) {
  const repository = useRepository(repoId);
  const branches = useRepositoryBranches(repoId);
  const stats = useRepositoryStats(repoId);
  const languages = useRepositoryLanguages(repoId);
  const latestRelease = useRepositoryLatestRelease(repoId);
  const instances = useProviderInstances();

  const [branch, setBranch] = useState<string | null>(null);
  const [path, setPath] = useState("");
  const [file, setFile] = useState<string | null>(null);
  const [query, setQuery] = useState("");

  const repo = repository.data;
  const ref = branch ?? repo?.defaultBranch ?? null;
  const inTree = file === null;

  const tree = useRepositoryTree(repoId, path, ref, repo != null && inTree);
  const fileContent = useRepositoryFile(repoId, file, ref, file !== null);

  const entries = inTree && tree.data ? sortTreeEntries(tree.data) : [];
  const entryPaths = entries.map(e => e.path);

  const latestCommit = useRepositoryLatestCommit(repoId, path, ref, repo != null && inTree);
  const treeCommits = useRepositoryTreeCommits(repoId, entryPaths, ref, entryPaths.length > 0);

  const readmeEntry = path === "" && inTree && tree.data ? pickReadme(tree.data) : null;
  const licenseEntry = path === "" && inTree && tree.data ? pickLicense(tree.data) : null;

  if (!repo) return null;

  const filter = query.trim().toLowerCase();
  const visibleEntries = filter ? entries.filter(e => e.name.toLowerCase().includes(filter)) : entries;

  const goToRoot = () => { setFile(null); setPath(""); setQuery(""); };
  const goToCrumb = (crumbPath: string) => { setFile(null); setPath(crumbPath); setQuery(""); };
  const goUp = () => { setFile(null); setPath(parentPath(path)); setQuery(""); };
  const openEntry = (entry: RemoteTreeEntry) => {
    setQuery("");
    if (entry.type === "Directory") { setFile(null); setPath(entry.path); }
    else setFile(entry.path);
  };
  const changeBranch = (name: string) => { setBranch(name); setPath(""); setFile(null); setQuery(""); };

  const crumbs = buildBreadcrumbs(file ?? path);

  return (
    <div className="cb">
      <div className="cb-main">
        <div className="cb-toolbar">
          <div className="cb-toolbar-l">
            <BranchPicker branches={branches.data ?? []} current={ref} loading={branches.isLoading} onPick={changeBranch} />
            {stats.data?.branchCount != null && (
              <span className="cb-count"><Ic.Branch size={13} /> <b>{formatCount(stats.data.branchCount)}</b> branches</span>
            )}
            {stats.data?.tagCount != null && (
              <span className="cb-count"><Ic.Tag size={13} /> <b>{formatCount(stats.data.tagCount)}</b> tags</span>
            )}
          </div>

          <div className="cb-toolbar-r">
            <div className="cb-search">
              <Ic.Search size={14} />
              <input value={query} onChange={e => setQuery(e.target.value)} placeholder="Go to file" spellCheck={false} />
            </div>
            <CloneMenu repo={repo} />
          </div>
        </div>

        {crumbs.length > 0 && (
          <nav className="cb-path" aria-label="Path">
            <button className="cb-crumb cb-crumb-root" onClick={goToRoot}><Ic.Repo size={13} /> {repo.name}</button>
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
        )}

        {file !== null
          ? <FileView repoId={repoId} content={fileContent.data} isLoading={fileContent.isLoading} error={fileContent.error} webUrl={repo.webUrl} gitRef={ref ?? repo.defaultBranch} />
          : (
            <>
              {!filter && <LatestCommitBar commit={latestCommit.data} totalCommits={stats.data?.commitCount} />}
              <TreeList
                entries={visibleEntries}
                commits={treeCommits.data}
                isLoading={tree.isLoading}
                error={tree.error}
                showUp={path !== "" && !filter}
                onUp={goUp}
                onOpen={openEntry}
                emptyLabel={filter ? "No files match." : "This folder is empty."}
              />
              {(readmeEntry || licenseEntry) && !filter && (
                <DocTabs repoId={repoId} gitRef={ref ?? repo.defaultBranch} webUrl={repo.webUrl} readmeEntry={readmeEntry} licenseEntry={licenseEntry} />
              )}
            </>
          )}
      </div>

      <aside className="cb-side">
        <AboutCard description={repo.description} />
        <RepoStatsCard stats={stats.data} />
        <ReleasesCard
          release={latestRelease.data}
          releaseCount={stats.data?.releaseCount ?? null}
          releasesUrl={buildReleasesUrl(repo.webUrl, instances.data?.find(i => i.id === repo.providerInstanceId)?.provider)}
        />
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
  const [query, setQuery] = useState("");

  const q = query.trim().toLowerCase();
  const filtered = q ? branches.filter(b => b.name.toLowerCase().includes(q)) : branches;

  const toggle = () => setOpen(o => { if (o) setQuery(""); return !o; });

  return (
    <div className="cb-branch">
      <button className="cb-branch-btn" onClick={toggle} disabled={loading && branches.length === 0}>
        <Ic.Branch size={13} />
        <span className="cb-branch-name">{current ?? "…"}</span>
        <Ic.ChevronDown size={13} />
      </button>

      {open && (
        <>
          <div className="cb-branch-backdrop" onClick={() => { setOpen(false); setQuery(""); }} />
          <div className="cb-branch-menu" role="listbox">
            {branches.length > 8 && (
              <div className="cb-branch-search">
                <Ic.Search size={13} />
                <input autoFocus value={query} onChange={e => setQuery(e.target.value)} placeholder="Find a branch…" spellCheck={false} />
              </div>
            )}
            {filtered.length === 0
              ? <div className="cb-branch-empty">{loading && branches.length === 0 ? "Loading branches…" : "No branches"}</div>
              : filtered.map(b => (
                <button
                  key={b.name}
                  role="option"
                  aria-selected={b.name === current}
                  data-active={b.name === current}
                  className="cb-branch-opt"
                  onClick={() => { onPick(b.name); setOpen(false); setQuery(""); }}
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

// ── Code button → clone dropdown (SSH / HTTPS) ────────────────────────────────

function CloneMenu({ repo }: { repo: RepositoryDetail }) {
  const [open, setOpen] = useState(false);

  return (
    <div className="cb-clone">
      <button className="cb-code-btn" onClick={() => setOpen(o => !o)}>
        <Ic.Code size={14} /> Code <Ic.ChevronDown size={13} />
      </button>

      {open && (
        <>
          <div className="cb-branch-backdrop" onClick={() => setOpen(false)} />
          <div className="cb-clone-menu">
            <div className="cb-clone-title">Clone</div>
            {repo.cloneUrlSsh && <CloneRow label="Clone with SSH" url={repo.cloneUrlSsh} />}
            {repo.cloneUrlHttps && <CloneRow label="Clone with HTTPS" url={repo.cloneUrlHttps} />}
            {!repo.cloneUrlSsh && !repo.cloneUrlHttps && <div className="cb-branch-empty">No clone URL available</div>}
            <button className="cb-clone-open" onClick={() => { window.open(repo.webUrl, "_blank", "noopener"); setOpen(false); }}>
              <Ic.ArrowOut size={13} /> Open on provider
            </button>
          </div>
        </>
      )}
    </div>
  );
}

function CloneRow({ label, url }: { label: string; url: string }) {
  const [copied, setCopied] = useState(false);

  const copy = () => {
    navigator.clipboard?.writeText(url).then(() => {
      setCopied(true);
      setTimeout(() => setCopied(false), 1500);
    }).catch(() => { /* clipboard blocked — the field is still selectable for manual copy */ });
  };

  return (
    <div className="cb-clone-block">
      <div className="cb-clone-label">{label}</div>
      <div className="cb-clone-field">
        <input readOnly value={url} onFocus={e => e.currentTarget.select()} spellCheck={false} />
        <button className="cb-clone-copy" onClick={copy} title="Copy">{copied ? <Ic.Check size={13} /> : <Ic.Copy size={13} />}</button>
      </div>
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
  emptyLabel: string;
}

function TreeList({ entries, commits, isLoading, error, showUp, onUp, onOpen, emptyLabel }: TreeListProps) {
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
        ? <div className="cb-empty">{emptyLabel}</div>
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
  repoId: string;
  content: RemoteFileContent | undefined;
  isLoading: boolean;
  error: unknown;
  webUrl: string;
  gitRef: string;
}

function FileView({ repoId, content, isLoading, error, webUrl, gitRef }: FileViewProps) {
  if (error instanceof ApiError) return <SourceError message={error.message} />;
  if (isLoading && !content) return <div className="cb-loading">Loading…</div>;
  if (!content) return null;

  return (
    <div className="cb-file">
      <FileHeader content={content} webUrl={webUrl} gitRef={gitRef} />
      <div className="cb-file-body">
        <FileBody repoId={repoId} content={content} webUrl={webUrl} gitRef={gitRef} />
      </div>
    </div>
  );
}

/** Action bar above the file content: name + size/lines + copy-contents · copy-path · open-on-provider. */
function FileHeader({ content, webUrl, gitRef }: { content: RemoteFileContent; webUrl: string; gitRef: string }) {
  const renderable = content.text != null && !content.isBinary && !content.isTruncated;
  const lineCount = renderable ? content.text!.split("\n").length : null;

  return (
    <div className="cb-file-head">
      <span className="cb-file-name"><Ic.File size={14} /> {content.name}</span>
      <span className="cb-file-meta">{lineCount != null && <>{lineCount.toLocaleString()} lines · </>}{formatBytes(content.size)}</span>
      <div className="cb-file-actions">
        {renderable && <CopyIconButton text={content.text!} title="Copy file contents" />}
        <CopyIconButton text={content.path} title="Copy path" idleIcon={<Ic.Link size={14} />} />
        <button className="cb-file-act" title="Open on provider" onClick={() => window.open(blobUrl(webUrl, gitRef, content.path), "_blank", "noopener")}>
          <Ic.ArrowOut size={14} />
        </button>
      </div>
    </div>
  );
}

function FileBody({ repoId, content, webUrl, gitRef }: { repoId: string; content: RemoteFileContent; webUrl: string; gitRef: string }) {
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

  if (isMarkdownName(content.name)) return <MarkdownView repoId={repoId} source={text} webUrl={webUrl} gitRef={gitRef} dir={parentPath(content.path)} />;

  const lines = text.split("\n");

  return (
    <div className="cb-code">
      {lines.map((line, i) => (
        <div className="cb-code-row" key={i}>
          <span className="cb-code-gutter">{i + 1}</span>
          <span className="cb-code-text">{line || " "}</span>
        </div>
      ))}
    </div>
  );
}

/** Icon button that copies text to the clipboard and flips to a check for a moment. */
function CopyIconButton({ text, title, idleIcon }: { text: string; title: string; idleIcon?: ReactNode }) {
  const [copied, setCopied] = useState(false);

  const copy = () => {
    navigator.clipboard?.writeText(text).then(() => {
      setCopied(true);
      setTimeout(() => setCopied(false), 1500);
    }).catch(() => { /* clipboard blocked — no-op */ });
  };

  return (
    <button className="cb-file-act" title={title} onClick={copy}>
      {copied ? <Ic.Check size={14} /> : (idleIcon ?? <Ic.Copy size={14} />)}
    </button>
  );
}

// ── README card ─────────────────────────────────────────────────────────────

/** README / License tabbed card under the root listing — mirrors GitHub's "README · MIT license" tabs. */
function DocTabs({ repoId, gitRef, webUrl, readmeEntry, licenseEntry }: {
  repoId: string;
  gitRef: string;
  webUrl: string;
  readmeEntry: RemoteTreeEntry | null;
  licenseEntry: RemoteTreeEntry | null;
}) {
  const [tab, setTab] = useState<"readme" | "license">(readmeEntry ? "readme" : "license");

  // Fetch only the active tab's file — the license is lazy, loaded when its tab is first opened.
  const readme = useRepositoryFile(repoId, readmeEntry?.path ?? null, gitRef, tab === "readme" && readmeEntry != null);
  const license = useRepositoryFile(repoId, licenseEntry?.path ?? null, gitRef, tab === "license" && licenseEntry != null);

  const active = tab === "readme" ? { entry: readmeEntry, query: readme } : { entry: licenseEntry, query: license };

  return (
    <div className="cb-readme">
      <div className="cb-doc-tabs">
        {readmeEntry && (
          <button className="cb-doc-tab" data-active={tab === "readme"} onClick={() => setTab("readme")}>
            <Ic.Book size={14} /> {readmeEntry.name}
          </button>
        )}
        {licenseEntry && (
          <button className="cb-doc-tab" data-active={tab === "license"} onClick={() => setTab("license")}>
            <Ic.Scale size={14} /> {licenseEntry.name}
          </button>
        )}
      </div>
      <div className="cb-readme-body">
        <DocContent repoId={repoId} entry={active.entry} content={active.query.data} isLoading={active.query.isLoading} webUrl={webUrl} gitRef={gitRef} />
      </div>
    </div>
  );
}

function DocContent({ repoId, entry, content, isLoading, webUrl, gitRef }: {
  repoId: string;
  entry: RemoteTreeEntry | null;
  content: RemoteFileContent | undefined;
  isLoading: boolean;
  webUrl: string;
  gitRef: string;
}) {
  if (!entry) return null;
  if (isLoading && !content) return <div className="cb-loading">Loading…</div>;
  if (!content?.text) return <div className="cb-empty">Couldn't render {entry.name}.</div>;

  return isMarkdownName(entry.name)
    ? <MarkdownView repoId={repoId} source={content.text} webUrl={webUrl} gitRef={gitRef} dir={parentPath(entry.path)} />
    : <pre className="cb-doc-text">{content.text}</pre>;
}

// ── Right rail: About · Stats · Releases · Languages ──────────────────────────

/** GitLab "Project information"-style summary: icon · bold count · label, one row each. Rows whose
 *  number the provider didn't supply are omitted; the whole card hides when none are available. */
function RepoStatsCard({ stats }: { stats?: RemoteRepositoryStats }) {
  const rows = [
    stats?.commitCount != null && { icon: <Ic.Commit size={15} />, value: formatCount(stats.commitCount), label: "Commits" },
    stats?.branchCount != null && { icon: <Ic.Branch size={15} />, value: formatCount(stats.branchCount), label: "Branches" },
    stats?.tagCount != null && { icon: <Ic.Tag size={15} />, value: formatCount(stats.tagCount), label: "Tags" },
    stats?.storageBytes != null && { icon: <Ic.Storage size={15} />, value: formatBytes(stats.storageBytes), label: "Storage" },
    stats?.releaseCount != null && { icon: <Ic.Release size={15} />, value: formatCount(stats.releaseCount), label: "Releases" },
  ].filter(Boolean) as Array<{ icon: ReactNode; value: string; label: string }>;

  if (rows.length === 0) return null;

  return (
    <div className="cb-side-card">
      <div className="cb-stat-list">
        {rows.map(r => (
          <div key={r.label} className="cb-stat-row">
            <span className="cb-stat-ic">{r.icon}</span>
            <b className="cb-stat-val">{r.value}</b>
            <span className="cb-stat-label">{r.label}</span>
          </div>
        ))}
      </div>
    </div>
  );
}

/** GitHub-style Releases box: total count, the latest release (tag · Latest/Pre-release badge · date),
 *  and a "+N releases" link to the provider's releases page. Hidden entirely when the repo has none. */
function ReleasesCard({ release, releaseCount, releasesUrl }: { release?: RemoteRelease | null; releaseCount: number | null; releasesUrl: string }) {
  if ((releaseCount == null || releaseCount === 0) && !release) return null;

  const openReleases = () => window.open(releasesUrl, "_blank", "noopener");
  const remaining = releaseCount != null ? Math.max(0, releaseCount - 1) : 0;

  return (
    <div className="cb-side-card">
      <div className="cb-side-title cb-side-title-row">
        <span>Releases</span>
        {releaseCount != null && releaseCount > 0 && <span className="cb-side-badge">{formatCount(releaseCount)}</span>}
      </div>

      {release ? (
        <>
          <button className="cb-release" onClick={() => window.open(release.webUrl, "_blank", "noopener")} title={`Open ${release.tagName} on the provider`}>
            <Ic.Tag size={16} className="cb-release-ic" />
            <span className="cb-release-main">
              <span className="cb-release-tag">
                {release.tagName}
                <span className="cb-release-badge" data-pre={release.isPrerelease}>{release.isPrerelease ? "Pre-release" : "Latest"}</span>
              </span>
              {release.publishedDate && <span className="cb-release-date">{relativeTime(release.publishedDate)}</span>}
            </span>
          </button>
          {remaining > 0 && (
            <button className="cb-release-more" onClick={openReleases}>+ {formatCount(remaining)} release{remaining === 1 ? "" : "s"}</button>
          )}
        </>
      ) : (
        <button className="cb-release-more" onClick={openReleases}>View all releases</button>
      )}
    </div>
  );
}

/** Releases-list page on the provider — GitLab nests it under `/-/`, GitHub doesn't. */
function buildReleasesUrl(repoWebUrl: string, provider?: string): string {
  const base = repoWebUrl.replace(/\/$/, "");
  return provider === "GitLab" ? `${base}/-/releases` : `${base}/releases`;
}

function AboutCard({ description }: { description?: string | null }) {
  const hasDescription = !!description && description.trim().length > 0;

  return (
    <div className="cb-side-card">
      <div className="cb-side-title">About</div>
      {hasDescription
        ? <p className="cb-about-desc">{description}</p>
        : <p className="cb-about-empty">No description provided.</p>}
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

/**
 * Sanitize schema for rendering a README's embedded HTML safely. Starts from rehype-sanitize's
 * GitHub-based default (which already strips <script>, event handlers, and javascript: URLs) and adds
 * back the cosmetic bits real READMEs rely on: `align` for centering, image sizing, and <picture>/<source>.
 */
const sanitizeSchema = {
  ...defaultSchema,
  tagNames: [...(defaultSchema.tagNames ?? []), "picture", "source"],
  attributes: {
    ...defaultSchema.attributes,
    "*": [...(defaultSchema.attributes?.["*"] ?? []), "align"],
    img: [...(defaultSchema.attributes?.img ?? []), "width", "height", "align", "loading"],
    source: ["srcSet", "srcset", "media", "type", "sizes"],
    div: [...(defaultSchema.attributes?.div ?? []), "align"],
    p: [...(defaultSchema.attributes?.p ?? []), "align"],
    h1: [...(defaultSchema.attributes?.h1 ?? []), "align"],
    h2: [...(defaultSchema.attributes?.h2 ?? []), "align"],
    h3: [...(defaultSchema.attributes?.h3 ?? []), "align"],
  },
};

/**
 * Hybrid markdown render: ask the repo's provider to render it (so refs / relative links resolve exactly
 * as on the provider's site), falling back to the client-side <Markdown> while that's in flight or when
 * the provider can't render it (no capability, insufficient scope, error). Either way the user sees a
 * rendered README — the provider path is a fidelity upgrade, never a hard dependency.
 */
function MarkdownView({ repoId, source, webUrl, gitRef, dir }: { repoId: string; source: string; webUrl: string; gitRef: string; dir: string }) {
  const rendered = useRenderMarkdown(repoId, source);
  const html = rendered.data?.html;

  if (html) return <ProviderHtml html={html} webUrl={webUrl} gitRef={gitRef} dir={dir} />;

  return <Markdown source={source} webUrl={webUrl} gitRef={gitRef} dir={dir} />;
}

/** Provider-rendered README HTML, post-processed (relative URLs resolved against the repo, unsafe bits stripped) then embedded. */
function ProviderHtml({ html, webUrl, gitRef, dir }: { html: string; webUrl: string; gitRef: string; dir: string }) {
  const safe = useMemo(() => prepareProviderHtml(html, webUrl, gitRef, dir), [html, webUrl, gitRef, dir]);

  return <div className="prd-markdown" dangerouslySetInnerHTML={{ __html: safe }} />;
}

/**
 * GitHub-flavored markdown that renders a README like the provider does: embedded HTML (logo, badges,
 * centered blocks) via rehype-raw, kept XSS-safe by rehype-sanitize, with relative image/link paths
 * resolved against the repo (images → raw URL, links → the provider's blob view). Links open in a new tab.
 */
function Markdown({ source, webUrl, gitRef, dir }: { source: string; webUrl: string; gitRef: string; dir: string }) {
  return (
    <div className="prd-markdown">
      <ReactMarkdown
        remarkPlugins={[remarkGfm]}
        rehypePlugins={[rehypeRaw, [rehypeSanitize, sanitizeSchema]]}
        urlTransform={(url, key) => resolveReadmeUrl(url, webUrl, gitRef, dir, key === "src" || key === "srcSet" || key === "poster")}
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
