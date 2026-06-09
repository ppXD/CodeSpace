import type { RemoteTreeEntry } from "@/api/types";

/**
 * Pure helpers behind the Code browser: tree ordering, breadcrumb derivation, parent-path
 * navigation, README detection, and size formatting. Kept SDK-free and side-effect-free so the
 * browser component stays a thin shell over tested logic.
 */

/**
 * Sort one tree level GitHub-style: directories first, then everything else (files / submodules /
 * symlinks), each group alphabetical and case-insensitive. Returns a new array (input untouched).
 */
export function sortTreeEntries(entries: RemoteTreeEntry[]): RemoteTreeEntry[] {
  const rank = (e: RemoteTreeEntry) => (e.type === "Directory" ? 0 : 1);
  return [...entries].sort((a, b) =>
    rank(a) - rank(b) || a.name.localeCompare(b.name, undefined, { sensitivity: "base" }));
}

export interface Crumb {
  name: string;
  /** Repo-root-relative path of this crumb. */
  path: string;
}

/**
 * Breadcrumb trail for a repo-root-relative path. "src/api/types.ts" →
 * [{src, "src"}, {api, "src/api"}, {types.ts, "src/api/types.ts"}]. Root ("" or "/") → [].
 * Empty / whitespace segments and leading/trailing slashes are ignored.
 */
export function buildBreadcrumbs(path: string): Crumb[] {
  const segs = path.split("/").map(s => s.trim()).filter(Boolean);

  const crumbs: Crumb[] = [];
  let acc = "";

  for (const seg of segs) {
    acc = acc ? `${acc}/${seg}` : seg;
    crumbs.push({ name: seg, path: acc });
  }

  return crumbs;
}

/** Parent folder of a path. "src/api/types.ts" → "src/api"; "src" → ""; "" → "". */
export function parentPath(path: string): string {
  const segs = path.split("/").filter(Boolean);
  segs.pop();
  return segs.join("/");
}

/** True for a README-style filename (README, README.md, readme.markdown, README.rst, README.txt …). */
export function isReadmeName(name: string): boolean {
  return /^readme(\.(md|markdown|mdown|mkd|rst|txt|adoc))?$/i.test(name.trim());
}

/** True when a filename is markdown — the viewer renders these rich instead of as source. */
export function isMarkdownName(name: string): boolean {
  return /\.(md|markdown|mdown|mkd)$/i.test(name.trim());
}

/**
 * The README entry in a tree level, if any. Considers only files (a "README" directory is ignored),
 * preferring a markdown README when several variants exist (README.md beats README.txt). Null when none.
 */
export function pickReadme(entries: RemoteTreeEntry[]): RemoteTreeEntry | null {
  const readmes = entries.filter(e => e.type !== "Directory" && isReadmeName(e.name));
  if (readmes.length === 0) return null;

  readmes.sort((a, b) => readmeRank(a.name) - readmeRank(b.name) || a.name.localeCompare(b.name));
  return readmes[0];
}

function readmeRank(name: string): number {
  const lower = name.toLowerCase();
  if (lower.endsWith(".md") || lower.endsWith(".markdown")) return 0;
  if (lower === "readme") return 1;
  return 2;
}

/** Human-readable byte size: 0 → "0 B", 1536 → "1.5 KB", 1048576 → "1.0 MB". Binary units (1024). */
export function formatBytes(bytes: number): string {
  if (!Number.isFinite(bytes) || bytes < 0) return "";
  if (bytes < 1024) return `${bytes} B`;

  const units = ["KB", "MB", "GB", "TB"];
  let value = bytes / 1024;
  let i = 0;

  while (value >= 1024 && i < units.length - 1) {
    value /= 1024;
    i++;
  }

  return `${value < 10 ? value.toFixed(1) : Math.round(value)} ${units[i]}`;
}
