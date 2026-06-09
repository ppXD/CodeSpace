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
 * The LICENSE file in a tree level, if any — LICENSE / LICENCE / COPYING / UNLICENSE, optionally with a
 * .md/.txt/.markdown extension. Prefers the shortest name when several match. Files only (never a dir).
 */
export function pickLicense(entries: RemoteTreeEntry[]): RemoteTreeEntry | null {
  const re = /^(licen[sc]e|copying|unlicense)(\.(md|markdown|txt))?$/i;
  const found = entries.filter(e => e.type !== "Directory" && re.test(e.name.trim()));
  if (found.length === 0) return null;

  found.sort((a, b) => a.name.length - b.name.length || a.name.localeCompare(b.name));
  return found[0];
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

/**
 * GitHub-style relative time — "just now", "3 hours ago", "2 weeks ago", "2 years ago". Unlike the PR
 * view's after-a-week date fallback, this rolls all the way up to years because file last-commits are
 * often old. <c>now</c> is injectable so tests are deterministic. Empty/invalid input ⇒ "".
 */
export function relativeTime(iso: string | null | undefined, now: number = Date.now()): string {
  if (!iso) return "";

  const t = new Date(iso).getTime();
  if (Number.isNaN(t)) return "";

  const sec = Math.floor((now - t) / 1000);
  if (sec < 60) return "just now";

  const min = Math.floor(sec / 60);
  if (min < 60) return pluralAgo(min, "minute");
  const hr = Math.floor(min / 60);
  if (hr < 24) return pluralAgo(hr, "hour");
  const day = Math.floor(hr / 24);
  if (day < 7) return pluralAgo(day, "day");
  const wk = Math.floor(day / 7);
  if (wk < 5) return pluralAgo(wk, "week");
  const mo = Math.floor(day / 30);
  if (mo < 12) return pluralAgo(mo, "month");

  return pluralAgo(Math.floor(day / 365), "year");
}

function pluralAgo(n: number, unit: string): string {
  return `${n} ${unit}${n === 1 ? "" : "s"} ago`;
}

/** Thousands-separated integer for the stats panel ("1,128"). */
export function formatCount(n: number): string {
  return n.toLocaleString("en-US");
}

// Linguist-ish colors for the Languages bar; unknown languages get a stable generated hue so the bar
// never has a blank segment.
const LANGUAGE_COLORS: Record<string, string> = {
  "c#": "#178600", javascript: "#f1e05a", typescript: "#3178c6", python: "#3572a5", java: "#b07219",
  go: "#00add8", rust: "#dea584", ruby: "#701516", php: "#4f5d95", "c++": "#f34b7d", c: "#555555",
  shell: "#89e051", powershell: "#012456", html: "#e34c26", css: "#563d7c", scss: "#c6538c",
  vue: "#41b883", svelte: "#ff3e00", kotlin: "#a97bff", swift: "#f05138", "objective-c": "#438eff",
  dart: "#00b4ab", scala: "#c22d40", elixir: "#6e4a7e", clojure: "#db5855", haskell: "#5e5086",
  lua: "#000080", "jupyter notebook": "#da5b0b", dockerfile: "#384d54", makefile: "#427819",
  json: "#292929", yaml: "#cb171e", markdown: "#083fa1", sql: "#e38c00",
};

/** Color for a language's bar/legend swatch — known linguist color, else a deterministic generated hue. */
export function languageColor(name: string): string {
  return LANGUAGE_COLORS[name.trim().toLowerCase()] ?? generatedColor(name);
}

function generatedColor(name: string): string {
  let hash = 0;
  for (let i = 0; i < name.length; i++) hash = (hash * 31 + name.charCodeAt(i)) | 0;
  return `hsl(${Math.abs(hash) % 360} 45% 55%)`;
}
