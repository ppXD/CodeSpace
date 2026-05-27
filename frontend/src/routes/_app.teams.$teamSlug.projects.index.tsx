import { createFileRoute, useNavigate } from "@tanstack/react-router";
import { useEffect, useMemo, useState } from "react";

import { ApiError } from "@/api/request";
import { slugifyProjectName } from "@/api/projects";
import type { CredentialSummary, RemoteRepository } from "@/api/types";
import { useCredentials, useProviderInstances, useAccessibleRepositoriesForPicker } from "@/hooks/use-credentials";
import { useCreateProject, useProjects } from "@/hooks/use-projects";
import { useBindRepositoriesBulk, useRepositories } from "@/hooks/use-repositories";
// ConnectRemoteModal is now invoked from inside the Import-from-repository flow
// (see ImportStep below) where it's contextually needed, rather than as a page-level
// action on the Projects header. Connecting a remote is an auth-setup task, not a
// project-management task — surfacing it here forced an unrelated affordance onto
// every project list view, even for teams that already have credentials wired up.
import { ConnectRemoteModal } from "@/_imported/ai-code-space/connect-remote-modal";
import { Ic } from "@/_imported/ai-code-space/icons";
import { Pager } from "@/_imported/ai-code-space/pager";
import { ProviderMark } from "@/_imported/ai-code-space/content";

export const Route = createFileRoute("/_app/teams/$teamSlug/projects/")({
  validateSearch: (raw: Record<string, unknown>): { q?: string } => {
    if (typeof raw.q === "string" && raw.q.length > 0) return { q: raw.q };
    return {};
  },
  component: ProjectsListPage,
});

/**
 * Team's project list. Same `.ct-*` chrome + `.tbl` table density as the
 * Repositories list, on purpose — Projects are the new primary nav and should
 * feel like the same kind of page operators already know. Toolbar carries a
 * search input + the "+ Add project" action; clicking it opens the two-step
 * AddProjectModal (Empty vs Import from repository).
 */
function ProjectsListPage() {
  const { teamSlug } = Route.useParams();
  const search = Route.useSearch();
  const query = search.q ?? "";
  const navigate = useNavigate();

  const projectsQuery = useProjects();
  const [addOpen, setAddOpen] = useState(false);

  const rows = projectsQuery.data ?? [];
  const filtered = query
    ? rows.filter(p =>
        p.name.toLowerCase().includes(query.toLowerCase()) ||
        p.slug.toLowerCase().includes(query.toLowerCase()))
    : rows;

  const setQuery = (next: string) =>
    navigate({
      to: "/teams/$teamSlug/projects",
      params: { teamSlug },
      search: next.length > 0 ? { q: next } : {},
    });

  return (
    <section className="ct">
      {/* paddingBottom on .ct-head: this page has no tabs strip below the title row,
          so without explicit bottom padding the Add-project button sits flush against
          the toolbar border. Mirrors the workflows list page comment. */}
      <div className="ct-head" style={{ paddingBottom: 14 }}>
        <div className="ct-crumbs">
          <span className="cur">Projects</span>
        </div>
        <div className="ct-title-row">
          <h1 className="ct-title">Projects</h1>
          <div className="ct-actions">
            <button className="btn btn-primary" onClick={() => setAddOpen(true)}>
              <Ic.Plus size={14} /> Add project
            </button>
          </div>
        </div>
      </div>

      <div className="ct-toolbar">
        <div className="ct-search">
          <Ic.Search size={13} />
          <input
            placeholder="Search projects…"
            value={query}
            onChange={e => setQuery(e.target.value)}
          />
        </div>
        <div className="ct-spacer" />
      </div>

      <div className="ct-body">
        {projectsQuery.isLoading && (
          <div className="ct-empty"><div className="ct-empty-h">Loading…</div></div>
        )}

        {projectsQuery.error instanceof ApiError && (
          <div className="cn-banner cn-banner-err" style={{ margin: 16 }}>
            <div className="cn-banner-h">Couldn't load projects</div>
            <div className="cn-banner-p">{projectsQuery.error.message}</div>
          </div>
        )}

        {!projectsQuery.isLoading && !projectsQuery.error && filtered.length === 0 && rows.length > 0 && (
          <div className="ct-empty">
            <div className="ct-empty-h">No projects match your search</div>
            <div className="ct-empty-p">Clear the filter or create a new project with the button above.</div>
          </div>
        )}

        {!projectsQuery.isLoading && !projectsQuery.error && rows.length === 0 && (
          <div className="ct-empty">
            <div className="ct-empty-h">No projects yet</div>
            <div className="ct-empty-p">
              A <code>default</code> project should already be auto-created for this team.
              If you're seeing this empty state, click <strong>Add project</strong> above to
              create one — pick "Import from repository" for a guided flow that creates
              the project and binds a repo in one go.
            </div>
          </div>
        )}

        {!projectsQuery.isLoading && !projectsQuery.error && filtered.length > 0 && (
          <table className="tbl">
            <thead>
              <tr>
                <th style={{ width: "50%" }}>Project</th>
                <th>Repositories</th>
                <th>Variables</th>
                <th>Created</th>
              </tr>
            </thead>
            <tbody>
              {filtered.map(p => (
                <tr
                  key={p.id}
                  onClick={() => navigate({
                    to: "/teams/$teamSlug/projects/$projectId",
                    params: { teamSlug, projectId: p.id },
                  })}
                >
                  <td>
                    <div className="repo-cell">
                      <div className="repo-mark" data-p="project" style={{ width: 30, height: 30, borderRadius: 7, display: "flex", alignItems: "center", justifyContent: "center" }}>
                        <Ic.Folder size={14} />
                      </div>
                      <div className="repo-info">
                        <div className="repo-name">{p.name}</div>
                        {/* slug stays at natural width (flex-item with no flex-grow),
                            description claims the remaining width via .repo-path-desc
                            and ellipsis-truncates so the row stays one line tall
                            regardless of description length. Hover reveals full text. */}
                        <div className="repo-path">
                          <span>{p.slug}</span>
                          {p.description && <>
                            <span>·</span>
                            <span className="repo-path-desc" title={p.description}>{p.description}</span>
                          </>}
                        </div>
                      </div>
                    </div>
                  </td>
                  <td>{p.activeRepositoryCount}</td>
                  <td>{p.activeVariableCount}</td>
                  <td className="synced">{formatRelative(p.createdDate)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </div>

      {addOpen && (
        <AddProjectModal
          onClose={() => setAddOpen(false)}
          onCreated={(projectId) => {
            setAddOpen(false);
            navigate({
              to: "/teams/$teamSlug/projects/$projectId",
              params: { teamSlug, projectId },
            });
          }}
        />
      )}
    </section>
  );
}

// ── Add Project modal — two-step (Empty | Import from repository) ──────────────────

type AddMode = "choose" | "empty" | "import";

interface AddProjectModalProps {
  onClose: () => void;
  onCreated: (projectId: string) => void;
}

/**
 * Two-step Add Project modal:
 *
 *   1. "choose" — two cards: Empty vs Import from repository. Pure layout, no
 *                 inputs yet, mirrors the Vercel "How would you like to start"
 *                 question so the operator's first decision is the high-level one.
 *   2a. "empty"  — minimal form: name + optional description. Slug is derived
 *                  server-side; preview shown live as the operator types.
 *   2b. "import" — reuses the existing credential + repo-picker steps from
 *                  AddRepoModal (same hooks), constrained to single-select.
 *                  Submit chains: POST /api/projects → POST /api/repositories/bind-bulk
 *                  with the new projectId. On error after project create we roll back
 *                  by deleting the just-created project so the operator doesn't end
 *                  up with an empty orphan.
 */
function AddProjectModal({ onClose, onCreated }: AddProjectModalProps) {
  const [mode, setMode] = useState<AddMode>("choose");

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => { if (e.key === "Escape") onClose(); };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [onClose]);

  return (
    <>
      <div className="mdl-mask" />
      <div className="mdl" role="dialog" aria-modal="true">
        {mode === "choose" && <ChooseStep onPickEmpty={() => setMode("empty")} onPickImport={() => setMode("import")} onClose={onClose} />}
        {mode === "empty" && <EmptyStep onBack={() => setMode("choose")} onClose={onClose} onCreated={onCreated} />}
        {mode === "import" && <ImportStep onBack={() => setMode("choose")} onClose={onClose} onCreated={onCreated} />}
      </div>
    </>
  );
}

function ChooseStep({ onPickEmpty, onPickImport, onClose }: { onPickEmpty: () => void; onPickImport: () => void; onClose: () => void }) {
  return (
    <>
      <div className="mdl-head">
        <div className="mdl-title-wrap">
          <div className="mdl-title">Add project</div>
          <div className="mdl-sub">How do you want to start?</div>
        </div>
        <button className="mdl-x" onClick={onClose} aria-label="Close"><Ic.X size={14} /></button>
      </div>
      <div className="mdl-body">
        <div className="add-choice-grid">
          <button className="add-choice" onClick={onPickEmpty}>
            <div className="add-choice-ic"><Ic.Folder size={18} /></div>
            <div className="add-choice-h">Empty project</div>
            <div className="add-choice-p">Start with just a name. Repositories and variables get added later.</div>
          </button>
          <button className="add-choice" onClick={onPickImport}>
            <div className="add-choice-ic"><Ic.Repo size={18} /></div>
            <div className="add-choice-h">Import from repository</div>
            <div className="add-choice-p">Pick a remote repo. We create the project and bind the repo in one step.</div>
          </button>
        </div>
      </div>
    </>
  );
}

function EmptyStep({ onBack, onClose, onCreated }: { onBack: () => void; onClose: () => void; onCreated: (projectId: string) => void }) {
  const [name, setName] = useState("");
  const [description, setDescription] = useState("");
  const [error, setError] = useState<string | null>(null);
  const create = useCreateProject();

  const slugPreview = useMemo(() => slugifyProjectName(name), [name]);
  const canSubmit = name.trim().length > 0 && slugPreview.length > 0 && !create.isPending;

  const submit = async () => {
    setError(null);
    try {
      const { id } = await create.mutateAsync({
        name: name.trim(),
        description: description.trim() || null,
      });
      onCreated(id);
    } catch (ex) {
      setError(ex instanceof Error ? ex.message : "Could not create project");
    }
  };

  return (
    <>
      <div className="mdl-head">
        <button className="mdl-back" onClick={onBack} title="Back" disabled={create.isPending}><Ic.ChevronLeft size={16} /></button>
        <div className="mdl-title-wrap">
          <div className="mdl-title">New project</div>
          <div className="mdl-sub">Name and optional description. The slug is derived from the name.</div>
        </div>
        <button className="mdl-x" onClick={onClose} aria-label="Close" disabled={create.isPending}><Ic.X size={14} /></button>
      </div>
      <div className="mdl-body">
        <div className="form-row">
          <label>Name</label>
          <input
            autoFocus
            placeholder="Acme Backend"
            value={name}
            onChange={e => setName(e.target.value)}
          />
          {name.trim().length > 0 && slugPreview.length > 0 && (
            <div className="form-hint">
              Variables resolve as <code>{`{{project.${slugPreview}.X}}`}</code>. The
              identifier is derived from the name and can't be changed later.
            </div>
          )}
          {name.trim().length > 0 && slugPreview.length === 0 && (
            <div className="form-hint err">
              This name doesn't produce a valid identifier. Add some letters or digits.
            </div>
          )}
        </div>
        <div className="form-row">
          <label>Description (optional)</label>
          <input
            placeholder="Short summary for the team"
            value={description}
            onChange={e => setDescription(e.target.value)}
          />
        </div>
        {error && <div className="cn-banner cn-banner-err"><div className="cn-banner-p">{error}</div></div>}
      </div>
      <div className="mdl-foot">
        {/* No foot-info — the form-hint under the Name input already covers
            slug derivation; repeating it next to the button just truncates. */}
        <div className="mdl-foot-info" />
        <button className="btn btn-primary" onClick={submit} disabled={!canSubmit}>
          {create.isPending ? "Creating…" : "Create project"}
        </button>
      </div>
    </>
  );
}

// ── Import step: lifted from AddRepoModal's credential + picker, single-select ─────

function ImportStep({ onBack, onClose, onCreated }: { onBack: () => void; onClose: () => void; onCreated: (projectId: string) => void }) {
  type Phase = "credential" | "picker" | "confirm";
  const [phase, setPhase] = useState<Phase>("credential");
  const [picked, setPicked] = useState<CredentialSummary | null>(null);
  const [chosenRepo, setChosenRepo] = useState<RemoteRepository | null>(null);

  // Lets the operator add a brand-new OAuth credential mid-import without dismissing
  // this modal first. When this opens, ConnectRemoteModal renders ON TOP of the
  // AddProject modal (both share .mdl z-index 81, later-DOM wins). When it closes,
  // React Query auto-refreshes `credentials` so the new row appears in the picker.
  const [connectOpen, setConnectOpen] = useState(false);

  const [query, setQuery] = useState("");
  const [debouncedQuery, setDebouncedQuery] = useState("");
  const [page, setPage] = useState(1);

  useEffect(() => {
    const t = window.setTimeout(() => { setDebouncedQuery(query); setPage(1); }, 300);
    return () => window.clearTimeout(t);
  }, [query]);

  const credentials = useCredentials();
  const instances = useProviderInstances();
  const existingRepos = useRepositories(picked?.providerInstanceId);
  const instanceById = useMemo(() => new Map((instances.data ?? []).map(i => [i.id, i])), [instances.data]);
  const activeCredentials = useMemo(() => (credentials.data ?? []).filter(c => c.status === "Active"), [credentials.data]);
  const boundFullPaths = useMemo(() => new Set((existingRepos.data ?? []).map(r => r.fullPath)), [existingRepos.data]);
  const pickedInstance = picked ? instanceById.get(picked.providerInstanceId) : null;

  const accessible = useAccessibleRepositoriesForPicker(picked?.id ?? null, pickedInstance?.provider ?? null, page, debouncedQuery);

  const chooseCredential = (cred: CredentialSummary) => {
    setPicked(cred);
    setPhase("picker");
  };

  const chooseRepo = (repo: RemoteRepository) => {
    setChosenRepo(repo);
    setPhase("confirm");
  };

  if (phase === "credential") {
    const showInlineAction = !credentials.isLoading && !instances.isLoading && activeCredentials.length > 0;
    return (
      <>
        <div className="mdl-head">
          <button className="mdl-back" onClick={onBack} title="Back"><Ic.ChevronLeft size={16} /></button>
          <div className="mdl-title-wrap">
            <div className="mdl-title">Import from repository</div>
            <div className="mdl-sub">Pick the credential to browse repositories with.</div>
          </div>
          <button className="mdl-x" onClick={onClose} aria-label="Close"><Ic.X size={14} /></button>
        </div>
        <div className="mdl-body">
          {/* Top-of-body action — visible whenever the picker list is showing.
              Lives here (not in .mdl-head) so it doesn't compete with the back/X
              chrome and stays aligned with the credential list below.
              Balanced 10px vertical padding gives the button equal breathing room
              from the modal head above and the credential list below — without
              it the button hugged the body's top edge and felt loose at the
              bottom. The `.btn` (not `.btn-ghost`) gives it a visible border +
              panel bg so it reads as a deliberate action, not a stray link. */}
          {showInlineAction && (
            <div style={{ display: "flex", justifyContent: "flex-end", padding: "10px 0" }}>
              <button className="btn" onClick={() => setConnectOpen(true)}>
                <Ic.Plus size={14} /> Connect new remote
              </button>
            </div>
          )}
          {credentials.isLoading || instances.isLoading ? (
            <div className="ct-empty"><div className="ct-empty-h">Loading credentials…</div></div>
          ) : activeCredentials.length === 0 ? (
            // Empty-state CTA — the previous copy pointed at a "sidebar → Providers"
            // nav row that Phase 3.0 removed. Replace with a primary action that
            // opens ConnectRemoteModal stacked on top of this one.
            <div className="ct-empty">
              <div className="ct-empty-h">No active credentials yet</div>
              <div className="ct-empty-p" style={{ marginBottom: 12 }}>
                Connect a GitHub or GitLab account to start importing repositories.
              </div>
              <button className="btn btn-primary" onClick={() => setConnectOpen(true)}>
                <Ic.Link size={14} /> Connect remote
              </button>
            </div>
          ) : (
            <div className="cred-list">
              {activeCredentials.map(c => {
                const inst = instanceById.get(c.providerInstanceId);
                if (!inst) return null;
                return (
                  <button key={c.id} className="cred-row" onClick={() => chooseCredential(c)}>
                    <ProviderMark provider={inst.provider} size={26} />
                    <div className="cred-row-meta">
                      <div className="cred-row-name">{c.displayName ?? inst.displayName}</div>
                      <div className="cred-row-sub">{inst.displayName} · {c.authType}</div>
                    </div>
                    <Ic.ChevronRight size={14} />
                  </button>
                );
              })}
            </div>
          )}
        </div>
        {/* Stacked on top of the AddProject modal (.mdl z-index 81, later DOM wins).
            On close, React Query's useCredentials cache refetches naturally so the
            new credential appears in the list without any manual invalidation. */}
        {connectOpen && <ConnectRemoteModal onClose={() => setConnectOpen(false)} />}
      </>
    );
  }

  if (phase === "picker" && picked && pickedInstance) {
    // Search debounce: while the operator is still typing, query !== debouncedQuery.
    // Surface this as a "typing…" indicator so the (unchanged) list below doesn't
    // confuse them — they SEE the input updating but the list is intentionally a
    // beat behind. Same affordance the AddRepoModal picker uses.
    const isSearchPending = query !== debouncedQuery;
    // hasNextPage mirrors the AddRepoModal heuristic: trust totalPages when known
    // (GitHub always, GitLab via GraphQL count), otherwise infer "more available"
    // from "this page came back full" — same pattern the PR list pager uses.
    const hasNextPage = accessible.totalPages != null
      ? page < accessible.totalPages
      : accessible.pageItems.length > 0;
    // First-load panel only when nothing is on screen yet. Once rows are visible
    // we render them and let .pick-list[data-stale] dim the previous page during
    // an in-flight refetch — dimming the empty state on cold-load was confusing.
    const showFirstLoadPanel = accessible.isLoading && accessible.pageItems.length === 0;

    return (
      <>
        <div className="mdl-head">
          <button className="mdl-back" onClick={() => { setPicked(null); setPhase("credential"); }} title="Back"><Ic.ChevronLeft size={16} /></button>
          <div className="mdl-title-wrap">
            <div className="mdl-title">Pick a repository</div>
            <div className="mdl-sub">{picked.displayName ?? pickedInstance.displayName} · {pickedInstance.provider}</div>
          </div>
          <button className="mdl-x" onClick={onClose} aria-label="Close"><Ic.X size={14} /></button>
        </div>
        <div className="mdl-body">
          <div className="picker-search">
            <Ic.Search size={13} />
            <input placeholder="Search repositories…" value={query} onChange={e => setQuery(e.target.value)} autoFocus />
            {isSearchPending && <span className="picker-search-pending">typing…</span>}
          </div>

          {/* Eager-fetch progress hint — visible while the loop is still loading
              pages in the background. The operator can already act on what's
              loaded; search + paging only see the prefix until this clears. */}
          {accessible.isLoading && accessible.loadedCount > 0 && (
            <div className="picker-search-hint">
              Loaded {accessible.loadedCount.toLocaleString()} repos so far · still loading the rest…
            </div>
          )}

          {accessible.error && (
            <div className="cn-banner cn-banner-err">
              <div className="cn-banner-p">{accessible.error instanceof Error ? accessible.error.message : "Could not load repositories."}</div>
            </div>
          )}

          {showFirstLoadPanel && <div className="ct-empty"><div className="ct-empty-h">Loading…</div></div>}

          {!showFirstLoadPanel && !accessible.error && (
            <div className="pick-list" data-stale={accessible.isRefetching}>
              {accessible.pageItems.map((repo: RemoteRepository) => {
                const alreadyBound = boundFullPaths.has(repo.fullPath);
                return (
                  <button
                    key={repo.externalId}
                    className="pick-row"
                    disabled={alreadyBound}
                    onClick={() => !alreadyBound && chooseRepo(repo)}
                  >
                    <ProviderMark provider={pickedInstance.provider} size={22} />
                    <div className="pick-row-meta">
                      <div className="pick-row-name">{repo.name}</div>
                      <div className="pick-row-sub">{repo.fullPath} · {repo.visibility.toLowerCase()}</div>
                    </div>
                    {alreadyBound ? <span className="pick-row-tag">already bound</span> : <Ic.ChevronRight size={14} />}
                  </button>
                );
              })}

              {accessible.pageItems.length === 0 && (
                <div style={{ padding: 24, textAlign: "center", color: "var(--muted)", fontSize: 12.5 }}>
                  {debouncedQuery
                    ? accessible.isFullyLoaded
                      ? `No repositories match "${debouncedQuery}".`
                      : `No matches yet for "${debouncedQuery}" — still loading more…`
                    : accessible.isFullyLoaded
                      ? "This token has no repos visible. Grant access on the provider side and try again."
                      : null}
                </div>
              )}
            </div>
          )}

          {(page > 1 || hasNextPage) && (
            <Pager
              current={page}
              totalPages={accessible.totalPages}
              hasNext={hasNextPage}
              loading={accessible.isLoading}
              onChange={setPage}
            />
          )}
        </div>
        <div className="mdl-foot">
          {/* Single-button step — keep info short so it never collides with
              future trailing actions. The Pager + hints inside the body
              already carry the load/search context; this is just a count. */}
          <div className="mdl-foot-info">
            {accessible.totalCount != null
              ? `${accessible.totalCount.toLocaleString()} repo${accessible.totalCount === 1 ? "" : "s"}${accessible.isFullyLoaded ? "" : " · loading…"}`
              : accessible.isLoading
                ? "Loading…"
                : `${accessible.loadedCount} loaded`}
          </div>
        </div>
      </>
    );
  }

  if (phase === "confirm" && picked && pickedInstance && chosenRepo) {
    return (
      <ConfirmImportStep
        credential={picked}
        providerInstanceId={pickedInstance.id}
        repo={chosenRepo}
        onBack={() => setPhase("picker")}
        onClose={onClose}
        onCreated={onCreated}
      />
    );
  }

  return null;
}

function ConfirmImportStep({ credential, providerInstanceId, repo, onBack, onClose, onCreated }: { credential: CredentialSummary; providerInstanceId: string; repo: RemoteRepository; onBack: () => void; onClose: () => void; onCreated: (projectId: string) => void }) {
  // Auto-derive project name from repo name on first render; the operator can edit.
  const [name, setName] = useState(repo.name);
  const [description, setDescription] = useState(repo.description ?? "");
  const [error, setError] = useState<string | null>(null);
  const [submitting, setSubmitting] = useState(false);

  const slugPreview = useMemo(() => slugifyProjectName(name), [name]);
  const canSubmit = name.trim().length > 0 && slugPreview.length > 0 && !submitting;

  const create = useCreateProject();
  const bind = useBindRepositoriesBulk();

  const submit = async () => {
    setError(null);
    setSubmitting(true);
    try {
      // Two-call chain. Create first — if it throws on slug collision, we never
      // touch the repo binding. Bind after we have the new projectId.
      const { id: projectId } = await create.mutateAsync({
        name: name.trim(),
        description: description.trim() || null,
      });

      try {
        await bind.mutateAsync({
          providerInstanceId,
          credentialId: credential.id,
          projectIdentifiers: [repo.externalId],
          projectId,
        });
      } catch (bindErr) {
        // Project created but bind failed. Surface the error so the operator can
        // decide; the empty project still exists and is reachable from the list.
        // We deliberately DON'T delete it because bind failures often mean a
        // transient OAuth problem the operator wants to retry rather than start over.
        setError(`Project created but repository bind failed: ${bindErr instanceof Error ? bindErr.message : "unknown error"}. The empty project is in your list; retry "Add repository" from its detail page.`);
        setSubmitting(false);
        onCreated(projectId);
        return;
      }

      onCreated(projectId);
    } catch (ex) {
      setError(ex instanceof Error ? ex.message : "Could not create project");
      setSubmitting(false);
    }
  };

  return (
    <>
      <div className="mdl-head">
        <button className="mdl-back" onClick={onBack} title="Back" disabled={submitting}><Ic.ChevronLeft size={16} /></button>
        <div className="mdl-title-wrap">
          <div className="mdl-title">Create from repository</div>
          <div className="mdl-sub">{repo.fullPath}</div>
        </div>
        <button className="mdl-x" onClick={onClose} aria-label="Close" disabled={submitting}><Ic.X size={14} /></button>
      </div>
      <div className="mdl-body">
        <div className="form-row">
          <label>Project name</label>
          <input autoFocus value={name} onChange={e => setName(e.target.value)} />
          {slugPreview.length > 0 && (
            <div className="form-hint">
              Will be referenced in workflows as <code>{`{{project.${slugPreview}.X}}`}</code>.
            </div>
          )}
        </div>
        <div className="form-row">
          <label>Description (optional)</label>
          <input value={description} onChange={e => setDescription(e.target.value)} placeholder="Short summary for the team" />
        </div>
        <div className="form-row">
          <label>Repository to bind</label>
          <div className="confirm-repo">
            <Ic.Repo size={14} />
            <span>{repo.fullPath}</span>
            <span className="confirm-repo-sub">{repo.visibility.toLowerCase()} · {repo.defaultBranch}</span>
          </div>
        </div>
        {error && <div className="cn-banner cn-banner-err"><div className="cn-banner-p">{error}</div></div>}
      </div>
      <div className="mdl-foot">
        {/* No foot-info — the action button is wide ("Create project + bind
            repo") and prose next to it gets ellipsis-truncated. The repo path
            in the head's subtitle gives enough context. */}
        <div className="mdl-foot-info" />
        <button className="btn btn-primary" onClick={submit} disabled={!canSubmit}>
          {submitting ? "Creating…" : "Create project + bind repo"}
        </button>
      </div>
    </>
  );
}

/** Same shape as RepositoryListPage's lastActivity formatter — local copy so the
 *  page is self-contained; factor out to a shared util when a third caller appears. */
function formatRelative(iso: string): string {
  const ts = new Date(iso).getTime();
  if (!Number.isFinite(ts)) return "—";
  const seconds = Math.max(1, Math.floor((Date.now() - ts) / 1000));
  if (seconds < 60) return `${seconds}s ago`;
  const m = Math.floor(seconds / 60);
  if (m < 60) return `${m}m ago`;
  const h = Math.floor(m / 60);
  if (h < 24) return `${h}h ago`;
  const d = Math.floor(h / 24);
  if (d < 30) return `${d}d ago`;
  const mo = Math.floor(d / 30);
  if (mo < 12) return `${mo}mo ago`;
  return `${Math.floor(mo / 12)}y ago`;
}
