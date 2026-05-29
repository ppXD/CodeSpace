import type { RepositorySummary } from "@/api/types";

/**
 * How a remote repo (shown in the Add-repository picker) relates to what the team has already
 * connected:
 *   - "fresh"               — not connected; the credential you picked becomes its service identity.
 *   - "in-project"          — already linked to the target project; can't add it again.
 *   - "connected-elsewhere" — connected to the team via some credential, but not in THIS project.
 *                             Adding it re-uses that existing connection (and its single webhook);
 *                             the credential currently picked is irrelevant. Surfacing this is the
 *                             whole point — so the picked credential isn't silently ignored.
 *
 * A repository is a team-level connection with ONE credential + ONE webhook, shared across the N
 * projects it's linked to — so a second project's bind never re-picks the credential.
 */
export type RepoConnection =
  | { state: "fresh" }
  | { state: "in-project"; repo: RepositorySummary }
  | { state: "connected-elsewhere"; repo: RepositorySummary };

export function repoConnectionState(fullPath: string, connectedByFullPath: ReadonlyMap<string, RepositorySummary>, targetProjectId: string | undefined): RepoConnection {
  const repo = connectedByFullPath.get(fullPath);
  if (!repo) return { state: "fresh" };

  // Without a known target project we can't resolve "in this project" (the lazy Default has no id
  // here), so a connected repo reads as connected-elsewhere — never wrongly shown as un-addable.
  const inProject = targetProjectId != null && (repo.projects ?? []).some((p) => p.id === targetProjectId);
  return inProject ? { state: "in-project", repo } : { state: "connected-elsewhere", repo };
}
