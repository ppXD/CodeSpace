import { useState } from "react";
import ReactMarkdown from "react-markdown";
import remarkGfm from "remark-gfm";

import { ApiError } from "@/api/request";
import type { RemoteBranch, RemoteFileContent, RemoteTreeEntry } from "@/api/types";
import {
  useRepository,
  useRepositoryBranches,
  useRepositoryFile,
  useRepositoryTree,
} from "@/hooks/use-repositories";
import { buildBreadcrumbs, formatBytes, isMarkdownName, parentPath, pickReadme, sortTreeEntries } from "@/lib/codeTree";

import { Ic } from "./icons";

interface CodeBrowserBodyProps {
  repoId: string;
}

/**
 * The "Code" tab — a GitHub-style branch-scoped source browser. Three live reads, never cached
 * locally: branches (picker), one tree level (lazy folder drill-in), and a single file (viewer).
 *
 * URL is NOT the source of truth here — branch / path / open-file are local component state. That
 * keeps the tab self-contained (no extra route segments) and matches how the PR list owns its own
 * filter/page state. Switching branch resets to the root because a deep path may not exist on the
 * other branch; resetting is safer than 404-ing the tree.
 */
export function CodeBrowserBody({ repoId }: CodeBrowserBodyProps) {
  const repository = useRepository(repoId);
  const branches = useRepositoryBranches(repoId);

  const [branch, setBranch] = useState<string | null>(null);
  const [path, setPath] = useState("");
  const [file, setFile] = useState<string | null>(null);

  const repo = repository.data;
  // Effective ref: the user's pick, else the repo's default branch (known once the repo loads).
  const ref = branch ?? repo?.defaultBranch ?? null;

  const tree = useRepositoryTree(repoId, path, ref, repo != null && file === null);
  const fileContent = useRepositoryFile(repoId, file, ref, file !== null);

  // README is shown under the listing at the root only. Find it in the loaded tree level, then
  // fetch its content — gated so it never fires when there's no README or we're in a sub-folder.
  const readmeEntry = path === "" && file === null && tree.data ? pickReadme(tree.data) : null;
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
            <TreeList
              entries={tree.data}
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
  );
}

// ── Branch picker ─────────────────────────────────────────────────────────────

interface BranchPickerProps {
  branches: RemoteBranch[];
  current: string | null;
  loading: boolean;
  onPick: (name: string) => void;
}

/** Warm-themed dropdown (a backdrop catches the outside click to close — no portal needed). */
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

// ── Tree list ─────────────────────────────────────────────────────────────────

interface TreeListProps {
  entries: RemoteTreeEntry[] | undefined;
  isLoading: boolean;
  error: unknown;
  showUp: boolean;
  onUp: () => void;
  onOpen: (entry: RemoteTreeEntry) => void;
}

function TreeList({ entries, isLoading, error, showUp, onUp, onOpen }: TreeListProps) {
  if (error instanceof ApiError) return <SourceError message={error.message} />;
  if (isLoading && !entries) return <div className="cb-loading">Loading…</div>;
  if (!entries) return null;

  const sorted = sortTreeEntries(entries);

  return (
    <div className="cb-tree">
      {showUp && (
        <button className="cb-row cb-row-up" onClick={onUp}>
          <Ic.Folder size={15} />
          <span className="cb-row-name">..</span>
        </button>
      )}

      {sorted.length === 0 && !showUp
        ? <div className="cb-empty">This folder is empty.</div>
        : sorted.map(entry => {
          const isDir = entry.type === "Directory";
          return (
            <button key={entry.path} className="cb-row" data-kind={isDir ? "dir" : "file"} onClick={() => onOpen(entry)}>
              {isDir ? <Ic.Folder size={15} /> : <Ic.File size={15} />}
              <span className="cb-row-name">{entry.name}</span>
              {!isDir && entry.size != null && <span className="cb-row-size">{formatBytes(entry.size)}</span>}
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

  // Markdown files render rich (same renderer as the README card); everything else shows as source
  // with a line-number gutter.
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
            <span className="cb-code-text">{line || " "}</span>
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
      <div className="cb-readme-head">
        <Ic.Book size={14} /> {name}
      </div>
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
