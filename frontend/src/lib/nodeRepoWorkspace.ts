/**
 * Pure adapters bracketing the unified repository-workspace picker (RepositoryWorkspacePicker).
 *
 * The agent.run / agent.supervisor engine persists the workspace as a SCALAR primary repo id
 * (`repositoryId`) plus a separate `relatedRepositories` array — the exact shape the backend fold
 * (AgentWorkspaceAuthoring → WorkspaceSpec.FromAuthoredRepos) reads. The picker, mirroring the ad-hoc
 * Launch composer, presents ONE flat list where row 0 is the primary. These two functions round-trip
 * between the two representations so the on-disk config stays byte-identical:
 *
 *   read:  { repositoryId, relatedRepositories } → Row[]   (row 0 = primary)
 *   write: Row[] → { repositoryId, relatedRepositories }   (row 0 → scalar primary; rest → related)
 *
 * The primary carries no alias/access in the persisted shape — the fold hardcodes the primary to alias
 * "repo" + Access=Write, and its real read/write is the run-level autonomyLevel — so write() drops row 0's
 * alias/access. An empty related list emits `undefined` (the key drops → single-repo byte-identical).
 */

export interface WorkspaceRepoRow {
  repositoryId: string;
  /** Mount-folder alias — meaningful for related rows only (row 0 is the root, no alias). */
  alias: string;
  access: "read" | "write";
}

export interface RelatedRepoWire {
  repositoryId: string;
  alias?: string;
  access?: "read" | "write";
}

export interface WorkspaceReposEmit {
  repositoryId: string | undefined;
  relatedRepositories: RelatedRepoWire[] | undefined;
}

/** Persisted inputs → picker rows. Row 0 is the primary (shown writable/root); the rest are related. */
export function readWorkspaceRepos(repositoryId: unknown, relatedRepositories: unknown): WorkspaceRepoRow[] {
  const rows: WorkspaceRepoRow[] = [];

  if (typeof repositoryId === "string" && repositoryId !== "") {
    rows.push({ repositoryId, alias: "", access: "write" });
  }

  if (Array.isArray(relatedRepositories)) {
    for (const v of relatedRepositories) {
      if (typeof v !== "object" || v === null) continue;
      const o = v as Record<string, unknown>;
      rows.push({
        repositoryId: typeof o.repositoryId === "string" ? o.repositoryId : "",
        alias: typeof o.alias === "string" ? o.alias : "",
        access: o.access === "write" ? "write" : "read",
      });
    }
  }

  return rows;
}

/**
 * Picker rows → persisted inputs. Row 0 becomes the scalar primary `repositoryId` (its alias/access are
 * dropped — the primary has no slot for them). Rows 1+ become `relatedRepositories` ({repositoryId, access,
 * alias?}), blank alias omitted. An empty related list ⇒ undefined so the key drops (single-repo byte-identical).
 */
export function writeWorkspaceRepos(rows: WorkspaceRepoRow[]): WorkspaceReposEmit {
  const primary = rows[0];

  const related: RelatedRepoWire[] = rows.slice(1).map((r) => ({
    repositoryId: r.repositoryId,
    access: r.access,
    ...(r.alias.trim() !== "" ? { alias: r.alias.trim() } : {}),
  }));

  return {
    repositoryId: primary?.repositoryId || undefined,
    relatedRepositories: related.length > 0 ? related : undefined,
  };
}
