/**
 * Pure helpers for the import-as-capability-install view — the repo label shown in the metadata rail and the
 * "Installing N agents + M skills" footer summary. Kept testable without rendering. No React.
 */

/** Short `owner/repo` label for a pasted pack URL (github/git), else the host+path tail. Blank → "". */
export function repoLabel(url: string): string {
  const trimmed = url.trim();
  if (!trimmed) return "";

  const stripped = trimmed.replace(/^https?:\/\//, "").replace(/\/+$/, "").replace(/\.git$/, "");
  const segments = stripped.split("/").filter(Boolean);

  return segments.length >= 3 ? segments.slice(-2).join("/") : stripped;
}

/** Footer summary of what a commit will add, from the per-kind selected counts. Empty → "Nothing selected". */
export function installSummary(selectedAgents: number, selectedSkills: number): string {
  const parts: string[] = [];
  if (selectedAgents > 0) parts.push(`${selectedAgents} ${selectedAgents === 1 ? "agent" : "agents"}`);
  if (selectedSkills > 0) parts.push(`${selectedSkills} ${selectedSkills === 1 ? "skill" : "skills"}`);

  return parts.length === 0 ? "Nothing selected" : `Installing ${parts.join(" + ")}`;
}
