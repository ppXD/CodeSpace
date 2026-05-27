import { useEffect, useMemo, useState } from "react";
import { createPortal } from "react-dom";

import { ApiError } from "@/api/request";
import type { CredentialSummary, ProviderInstanceSummary, RemoteRepository } from "@/api/types";
import { ACCESSIBLE_REPOS_PAGE_SIZE, useAccessibleRepositoriesForPicker, useCredentials, useProviderInstances } from "@/hooks/use-credentials";
import { useBindRepositoriesBulk, useRepositories } from "@/hooks/use-repositories";

import { ConnectRemoteModal } from "./connect-remote-modal";
import { Ic } from "./icons";
import { Pager } from "./pager";

/**
 * Add repository modal. Three steps:
 *   1. "credential" — list the team's active credentials, user picks the one whose
 *                     visibility on the provider matches the repos they want to add.
 *   2. "picker"     — live-fetch accessible repos for that credential, multi-select with
 *                     already-bound filter, show count + visibility per row.
 *   3. "result"     — show per-item success/failure from BindRepositoriesBulkCommand.
 *
 * Nothing is mocked — every list, every action is a real backend call.
 */

interface AddRepoModalProps {
  onClose: () => void;
  /**
   * Phase 3.0 — pre-fill the target CodeSpace Project for the bulk-bind. When set,
   * the modal still runs the full credential → picker flow, but the resulting
   * binds land under this project rather than the team's lazily-created Default.
   * Provided by the project-detail page's "+ Add repository" button so the user
   * doesn't have to choose the project again after they've already navigated
   * into it.
   */
  presetProjectId?: string;
}

type Step = "credential" | "picker" | "result";

export function AddRepoModal({ onClose, presetProjectId }: AddRepoModalProps) {
  const [step, setStep] = useState<Step>("credential");
  const [picked, setPicked] = useState<CredentialSummary | null>(null);

  // Stacked ConnectRemoteModal — opened from the inline "+ Connect new remote"
  // affordance in CredentialStep. The nested modal renders alongside this one
  // (both share .mdl z-index 81; later-DOM wins the stacking order), and on
  // close React Query refetches the credentials list so any new credential
  // appears in the picker without a manual invalidation step.
  const [connectOpen, setConnectOpen] = useState(false);

  // Search box value (raw, every keystroke) vs the debounced value we actually
  // send to the backend. 300ms strikes the usual balance between responsiveness
  // and not firing a network call per character on a slow-typing user.
  const [query, setQuery] = useState("");
  const [debouncedQuery, setDebouncedQuery] = useState("");
  const [page, setPage] = useState(1);

  // Selection lives as a full Map<externalId, RemoteRepository> so it survives
  // page/search navigation — once the user has ticked a repo on page 1, paging
  // to page 5 doesn't lose the selection (the row's data isn't in the current
  // page anymore, so a Set<id> alone would force a re-lookup at submit time
  // and the ResultStep wouldn't be able to render the friendly path).
  const [selected, setSelected] = useState<Map<string, RemoteRepository>>(new Map());

  const credentials = useCredentials();
  const instances = useProviderInstances();
  const existing = useRepositories(picked?.providerInstanceId);
  const bind = useBindRepositoriesBulk();

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => { if (e.key === "Escape") onClose(); };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [onClose]);

  // Debounce the search box → backend query. Also resets to page 1 whenever the
  // term lands (typing then deleting back to the same string IS still a stable
  // query and stays on whatever page the user was on).
  useEffect(() => {
    const handle = window.setTimeout(() => {
      setDebouncedQuery(query);
      setPage(1);
    }, 300);
    return () => window.clearTimeout(handle);
  }, [query]);

  const instanceById = useMemo(() => new Map((instances.data ?? []).map(i => [i.id, i])), [instances.data]);

  const activeCredentials = useMemo(
    () => (credentials.data ?? []).filter(c => c.status === "Active"),
    [credentials.data],
  );

  const boundFullPaths = useMemo(
    () => new Set((existing.data ?? []).map(r => r.fullPath)),
    [existing.data],
  );

  const pickedInstance = picked ? instanceById.get(picked.providerInstanceId) : null;

  // Provider-adaptive: GitHub gets eager-fetch + client-side filter, GitLab gets
  // native server-side search. The hook returns the same shape either way so the
  // modal doesn't have to branch — see useAccessibleRepositoriesForPicker for the
  // why behind the split (GitHub has no full-visibility search API).
  const accessible = useAccessibleRepositoriesForPicker(picked?.id ?? null, pickedInstance?.provider ?? null, page, debouncedQuery);

  const choose = (cred: CredentialSummary) => {
    setPicked(cred);
    setStep("picker");
    setQuery("");
    setDebouncedQuery("");
    setPage(1);
    setSelected(new Map());
  };

  const backToCredential = () => {
    setPicked(null);
    setStep("credential");
    setSelected(new Map());
    bind.reset();
  };

  const toggleRepo = (repo: RemoteRepository) => {
    if (boundFullPaths.has(repo.fullPath)) return;
    setSelected(prev => {
      const next = new Map(prev);
      if (next.has(repo.externalId)) next.delete(repo.externalId);
      else next.set(repo.externalId, repo);
      return next;
    });
  };

  const submit = async () => {
    if (!picked || !pickedInstance || selected.size === 0) return;

    await bind.mutateAsync({
      providerInstanceId: pickedInstance.id,
      credentialId: picked.id,
      projectIdentifiers: Array.from(selected.keys()),
      // When the operator invoked this modal from a project-detail page, all of
      // these bulk-binds drop into that project. Without a preset id the backend
      // falls back to the team's lazily-created Default project.
      projectId: presetProjectId,
    });

    setStep("result");
  };

  return createPortal(
    <>
      {/* Backdrop is non-interactive — clicking outside the modal must not close it
          (too easy to misfire when the user reaches for an input). Only the X icon,
          the Cancel button, or Escape closes the modal. */}
      <div className="mdl-mask" />
      <div className="mdl" role="dialog" aria-modal="true">
        {step === "credential" && <CredentialStep credentials={activeCredentials} instances={instanceById} loading={credentials.isLoading || instances.isLoading} error={credentials.error ?? instances.error} onPick={choose} onClose={onClose} onOpenConnect={() => setConnectOpen(true)} />}

        {step === "picker" && picked && pickedInstance && (
          <PickerStep
            credential={picked}
            instance={pickedInstance}
            page={page}
            totalPages={accessible.totalPages}
            onPageChange={setPage}
            pageItems={accessible.pageItems}
            totalCount={accessible.totalCount}
            loadedCount={accessible.loadedCount}
            isLoading={accessible.isLoading}
            isRefetching={accessible.isRefetching}
            isFullyLoaded={accessible.isFullyLoaded}
            error={accessible.error}
            boundFullPaths={boundFullPaths}
            query={query}
            onQueryChange={setQuery}
            isSearchPending={query !== debouncedQuery}
            selected={selected}
            onToggle={toggleRepo}
            onBack={backToCredential}
            onClose={onClose}
            onSubmit={submit}
            submitting={bind.isPending}
            submitError={bind.error instanceof Error ? bind.error.message : null}
          />
        )}

        {step === "result" && bind.data && <ResultStep result={bind.data} selectedLookup={selected} onDone={onClose} />}
      </div>
      {/* Render ConnectRemoteModal as a sibling of the main .mdl (NOT inside it)
          so its own .mdl-mask + .mdl pair stack cleanly on top. Both modals share
          .mdl z-index 81 and the later DOM wins → the inner overlay appears above
          the AddRepo modal until dismissed. */}
      {connectOpen && <ConnectRemoteModal onClose={() => setConnectOpen(false)} />}
    </>,
    document.body,
  );
}

// ── Credential step ──────────────────────────────────────────────────────────────

interface CredentialStepProps {
  credentials: CredentialSummary[];
  instances: Map<string, ProviderInstanceSummary>;
  loading: boolean;
  error: unknown;
  onPick: (c: CredentialSummary) => void;
  onClose: () => void;
  /** Opens ConnectRemoteModal stacked on top of this modal so the operator can add
   *  a new OAuth credential without losing the in-progress Add Repository flow. */
  onOpenConnect: () => void;
}

function CredentialStep({ credentials, instances, loading, error, onPick, onClose, onOpenConnect }: CredentialStepProps) {
  const showInlineAction = !loading && !(error instanceof Error) && credentials.length > 0;
  return (
    <>
      <div className="mdl-head">
        <div className="mdl-title-wrap">
          <div className="mdl-title">Add repository</div>
          <div className="mdl-sub">Pick the credential to read repositories with.</div>
        </div>
        <button className="mdl-x" onClick={onClose} title="Close"><Ic.X size={14} /></button>
      </div>

      <div className="mdl-body">
        {/* Top-of-body action — visible whenever the picker list is showing.
            Matches the AddProject → Import flow's affordance so both credential
            pickers feel the same. Lives here (not in .mdl-head) so it doesn't
            compete with the close X.
            Spacing math: `.mdl-body` already supplies `padding: 16px 22px`, so
            the body's own padding-top contributes 16px above this row. Mirror
            that exactly with marginBottom: 16 — total gap above and below the
            button is 16px each, symmetric. Adding ANY padding-top to this
            wrapper would stack on top of the body's 16px and make the top side
            visibly larger than the bottom. `.btn` (not `.btn-ghost`) gives the
            button a visible border + panel bg. */}
        {showInlineAction && (
          <div style={{ display: "flex", justifyContent: "flex-end", marginBottom: 16 }}>
            <button className="btn" onClick={onOpenConnect}>
              <Ic.Plus size={14} /> Connect new remote
            </button>
          </div>
        )}

        {loading && <div className="cn-loading"><Ic.Clock size={14} /> Loading…</div>}

        {error instanceof Error && !loading && (
          <div className="cn-banner cn-banner-err">
            <div className="cn-banner-h">Couldn't load connections</div>
            <div className="cn-banner-p">{error.message}</div>
          </div>
        )}

        {!loading && !error && credentials.length === 0 && (
          // Empty-state CTA — the previous copy pointed at a "Connect remote from
          // the toolbar" that no longer exists post Phase 3.0. Replace with a
          // primary action that opens ConnectRemoteModal directly.
          <div className="cn-empty">
            <div className="cn-empty-h">No connections yet</div>
            <div className="cn-empty-p" style={{ marginBottom: 12 }}>
              Connect a GitHub or GitLab account to start adding repositories.
            </div>
            <button className="btn btn-primary" onClick={onOpenConnect}>
              <Ic.Link size={14} /> Connect remote
            </button>
          </div>
        )}

        {!loading && !error && credentials.length > 0 && (
          <div className="cn-pv-list">
            {credentials.map(cred => {
              const instance = instances.get(cred.providerInstanceId);
              if (!instance) return null;
              const initials = providerInitials(instance.provider);
              return (
                <div key={cred.id} className="cn-pv-card" onClick={() => onPick(cred)}>
                  <div className="cn-pv-mark" data-p={instance.provider.toLowerCase()}>{initials}</div>
                  <div className="cn-pv-meta">
                    <div className="cn-pv-name">
                      {cred.displayName}
                      {/* Owner badge — only shown when we know who owns the credential and
                          the display name doesn't already include them. Avoids the "alice
                          alice's GitHub" double-print while still disambiguating shared
                          credentials whose display name was renamed without "alice's" in it. */}
                      {cred.ownerUserName && !cred.displayName.toLowerCase().includes(cred.ownerUserName.toLowerCase()) && (
                        <span className="cn-cred-owner">{cred.ownerUserName}</span>
                      )}
                    </div>
                    <div className="cn-pv-desc">{instance.displayName} · {instance.baseUrl}</div>
                  </div>
                  <Ic.ChevronRight size={16} className="cn-pv-arrow" />
                </div>
              );
            })}
          </div>
        )}
      </div>

      <div className="mdl-foot">
        {/* No foot-info here — the head's mdl-sub already explains the "we list
            what the connection can see" contract; repeating it next to the
            Cancel button just gets ellipsis-truncated on narrow modals. */}
        <button className="btn btn-ghost" onClick={onClose}>Cancel</button>
      </div>
    </>
  );
}

// ── Picker step ──────────────────────────────────────────────────────────────────

interface PickerStepProps {
  credential: CredentialSummary;
  instance: ProviderInstanceSummary;
  page: number;
  /** Total pages when known (GitHub always, GitLab when GraphQL count succeeded).
   *  Null = open-ended pager (GitLab fallback when no count is available). */
  totalPages: number | null;
  onPageChange: (next: number) => void;
  pageItems: RemoteRepository[];
  /** Total matches across the full visible list. Null on GitLab when the count
   *  endpoint didn't respond — the foot summary then degrades gracefully. */
  totalCount: number | null;
  /** Eager-fetch progress counter (GitHub only). Drives the "Loaded N repos so
   *  far…" hint. 0 on GitLab (each page is a fresh server request). */
  loadedCount: number;
  isLoading: boolean;
  /** True when a user-initiated refetch is in flight (GitLab page change / search
   *  typing) and the previous result is still on screen — drives the dim affordance.
   *  Always false on GitHub since page/search are pure client-side over a cached list. */
  isRefetching: boolean;
  isFullyLoaded: boolean;
  error: unknown;
  boundFullPaths: Set<string>;
  query: string;
  onQueryChange: (next: string) => void;
  isSearchPending: boolean;
  selected: Map<string, RemoteRepository>;
  onToggle: (repo: RemoteRepository) => void;
  onBack: () => void;
  onClose: () => void;
  onSubmit: () => void;
  submitting: boolean;
  submitError: string | null;
}

function PickerStep({ credential, instance, page, totalPages, onPageChange, pageItems, totalCount, loadedCount, isLoading, isRefetching, isFullyLoaded, error, boundFullPaths, query, onQueryChange, isSearchPending, selected, onToggle, onBack, onClose, onSubmit, submitting, submitError }: PickerStepProps) {
  const initials = providerInitials(instance.provider);
  // When the provider gave us a real total, use it for hasNext; otherwise (GitLab
  // open-ended fallback) infer it from "this page came back full" — same heuristic
  // the PR list pager uses.
  const hasNextPage = totalPages != null
    ? page < totalPages
    : pageItems.length === ACCESSIBLE_REPOS_PAGE_SIZE;
  // First-load panel shows while no data has landed for the current credential
  // / page / search yet. Once rows are visible we render them and surface
  // any in-flight refetch via the hint above the list (NOT by dimming — the
  // dim effect was confusing operators).
  const showLoading = isLoading && pageItems.length === 0;

  return (
    <>
      <div className="mdl-head">
        <button className="mdl-back" onClick={onBack} title="Back" disabled={submitting}><Ic.ChevronLeft size={16} /></button>
        <div className="mdl-title-wrap">
          <div className="mdl-title">Add from {instance.displayName}</div>
          <div className="mdl-sub">Select one or more to add. We register a webhook on each.</div>
        </div>
        <button className="mdl-x" onClick={onClose} title="Close" disabled={submitting}><Ic.X size={14} /></button>
      </div>

      <div className="mdl-body">
        <div className="acct">
          <div className="pv-mark" data-p={instance.provider.toLowerCase()} style={{ width: 34, height: 34, borderRadius: 8, fontSize: 12 }}>{initials}</div>
          <div className="acct-meta">
            <div className="acct-label">Reading from</div>
            <div className="acct-val">{credential.displayName}</div>
            <div className="acct-sub">{instance.baseUrl}</div>
          </div>
          <span className="acct-switch" onClick={onBack}>Switch</span>
        </div>

        <div className="picker-search">
          <Ic.Search size={13} />
          <input autoFocus placeholder="Search repositories…" value={query} onChange={e => onQueryChange(e.target.value)} disabled={submitting} />
          {isSearchPending && <span className="picker-search-pending">typing…</span>}
        </div>

        {/* In-progress hint — visible while the eager-fetch loop is still loading
            more pages. The user can already act on what's loaded, but search and
            paging only see the prefix until this clears. */}
        {isLoading && loadedCount > 0 && (
          <div className="picker-search-hint">
            Loaded {loadedCount.toLocaleString()} repos so far · still loading the rest…
          </div>
        )}

        <div className="picker-list" data-stale={isRefetching}>
          {showLoading && <div className="cn-loading"><Ic.Clock size={14} /> Asking {instance.displayName}…</div>}

          {error instanceof ApiError && (
            <div className="cn-banner cn-banner-err">
              <div className="cn-banner-h">
                {error.code === "oauth_insufficient_scope" ? "Missing OAuth scope" : "Provider call failed"}
              </div>
              <div className="cn-banner-p">{error.message}</div>
              {/* Structured scope detail when the backend told us exactly what's missing. The
                  user needs to add the listed scope(s) on the provider side, then disconnect
                  and reconnect this credential — the consent screen will re-prompt. */}
              {error.code === "oauth_insufficient_scope" && Array.isArray((error.body as { missingScopes?: string[] })?.missingScopes) && (
                <div className="cn-banner-scopes">
                  {((error.body as { missingScopes: string[] }).missingScopes).map(s => <code key={s} className="cn-scope-chip">{s}</code>)}
                </div>
              )}
            </div>
          )}

          {!showLoading && !error && pageItems.map(r => {
            const already = boundFullPaths.has(r.fullPath);
            const checked = selected.has(r.externalId);
            const VisIcon = r.visibility === "Private" ? Ic.Lock
              : r.visibility === "Public" ? Ic.Globe : Ic.Users;

            return (
              <div key={r.externalId} className="picker-row" data-checked={checked} data-disabled={already} onClick={() => onToggle(r)}>
                <div className="picker-cb">{checked && <Ic.Check size={11} stroke="#fff" strokeWidth={2.2} />}</div>
                <div className="picker-meta">
                  <div className="picker-name">{r.name}</div>
                  <div className="picker-path">
                    <span>{r.fullPath}</span>
                    <span>·</span>
                    <span className="picker-vis"><VisIcon size={10} /> {r.visibility.toLowerCase()}</span>
                  </div>
                </div>
                {already && <span className="picker-added">Already added</span>}
              </div>
            );
          })}

          {!showLoading && !error && pageItems.length === 0 && (
            <div style={{ padding: 24, textAlign: "center", color: "var(--muted)", fontSize: 12.5 }}>
              {query
                ? isFullyLoaded
                  ? `No repositories match "${query}".`
                  : `No matches yet for "${query}" — still loading more…`
                : isFullyLoaded
                  ? "This token has no repos visible. Grant access on the provider side and try again."
                  : null}
            </div>
          )}
        </div>

        {(page > 1 || hasNextPage) && (
          <Pager
            current={page}
            totalPages={totalPages}
            hasNext={hasNextPage}
            loading={isLoading}
            onChange={onPageChange}
          />
        )}

        {submitError && (
          <div className="cn-state cn-state-err" style={{ marginTop: 12 }}>
            <span>{submitError}</span>
          </div>
        )}
      </div>

      <div className="mdl-foot">
        {/* Foot-info kept terse — "select to add" + "webhook registered on each"
            were redundant with the primary button label and the head subtitle.
            Now: just count + loading status, or selection count when picking. */}
        <div className="mdl-foot-info">
          {selected.size === 0
            ? totalCount != null && totalCount > 0
              ? `${totalCount.toLocaleString()} ${totalCount === 1 ? "repo" : "repos"}${isFullyLoaded ? "" : " · loading…"}`
              : isLoading
                ? "Loading…"
                : "No repos visible"
            : `${selected.size} selected`}
        </div>
        {/* No secondary Cancel button here — the .mdl-back in the head + X in the
            top-right already give two exit paths. A third button labelled Cancel that
            actually goes Back was confusing operators. */}
        <button className="btn btn-primary" disabled={selected.size === 0 || submitting} onClick={onSubmit}>
          {submitting
            ? <><Ic.Clock size={13} /> Adding…</>
            : <>Add {selected.size > 0 ? `${selected.size} ` : ""}{selected.size === 1 ? "repo" : "repos"}</>}
        </button>
      </div>
    </>
  );
}

// ── Result step ──────────────────────────────────────────────────────────────────

interface ResultStepProps {
  result: { successCount: number; failureCount: number; items: { projectIdentifier: string; repositoryId?: string | null; error?: string | null }[] };
  /** Selection map captured at submit time — contains every repo the user picked,
   *  even those from pages no longer in the picker's current view. The lookup
   *  used to come from `accessible` (one page), which broke the friendly path
   *  rendering once selection spanned multiple pages. */
  selectedLookup: Map<string, RemoteRepository>;
  onDone: () => void;
}

function ResultStep({ result, selectedLookup, onDone }: ResultStepProps) {
  const anyFailures = result.failureCount > 0;

  return (
    <>
      <div className="mdl-head">
        <div className="mdl-title-wrap">
          <div className="mdl-title">
            {anyFailures ? `Added ${result.successCount} · ${result.failureCount} failed` : `Added ${result.successCount} ${result.successCount === 1 ? "repository" : "repositories"}`}
          </div>
          <div className="mdl-sub">
            {anyFailures
              ? "Some failed — successful ones are already in the list."
              : "Webhooks registered. Provider events now flow live."}
          </div>
        </div>
        <button className="mdl-x" onClick={onDone} title="Close"><Ic.X size={14} /></button>
      </div>

      <div className="mdl-body">
        <div className="cn-list">
          {result.items.map(item => {
            const repo = selectedLookup.get(item.projectIdentifier);
            const ok = item.repositoryId != null;
            return (
              <div key={item.projectIdentifier} className="cn-row">
                <div className={ok ? "cn-perm-ic" : "cn-perm-ic cn-perm-ic-err"}>
                  {ok ? <Ic.Check size={12} /> : <Ic.X size={12} />}
                </div>
                <div className="cn-meta">
                  <div className="cn-name" style={{ fontSize: 13 }}>{repo?.fullPath ?? item.projectIdentifier}</div>
                  {!ok && item.error && <div className="cn-sub" style={{ color: "var(--err)" }}>{item.error}</div>}
                  {ok && <div className="cn-sub">added</div>}
                </div>
              </div>
            );
          })}
        </div>
      </div>

      <div className="mdl-foot">
        <div className="mdl-foot-info">{anyFailures ? "Retry failed items after fixing the cause." : "All set."}</div>
        <button className="btn btn-primary" onClick={onDone}>Done</button>
      </div>
    </>
  );
}

// ── helpers ──────────────────────────────────────────────────────────────────────

function providerInitials(kind: ProviderInstanceSummary["provider"]) {
  if (kind === "GitHub") return "GH";
  if (kind === "GitLab") return "GL";
  return "G";
}
