import { useEffect, useRef, useState, type ReactNode } from "react";
import { createPortal } from "react-dom";

import { ApiError } from "@/api/request";
import type { StorageProfileSummary } from "@/api/storage";
import type { StorageProfileRevisionMode, StorageRouteDetail, StorageRouteRevisionDetail, StorageRouteState, StorageRouteSummary } from "@/api/storageRoutes";
import { useAppendStorageRouteRevision, useCreateStorageRoute, useRoutedDataClasses, useSetStorageRouteState, useStorageRoute, useStorageRoutes } from "@/hooks/use-storage-routes";
import { TeamPermissions, useTeamPermissions } from "@/hooks/use-team-management";
import { StorageStep, type StorageStepState } from "./StorageStep";

export const DATA_CLASS_DEFINITION = "A data class is one kind of data this build writes — a runtime consumer asks the routing plane for it by name, so only a class this deployment reads can be routed at all.";

/**
 * What a route that has never been activated means, per data class.
 *
 * <p>The authority is one backend declaration: `WorkflowArtifactDataClass` implements
 * `IRoutedDataClassLocalFallback` and `AgentRunLogDataClass` deliberately does not, and that is what
 * turns "no route" and "route never activated" into a local write for the first and a refusal for the
 * second (`RoutedDestinationResolver.LocalApplies`). That flag is NOT carried on the
 * `RoutedDataClassDescriptor` this screen reads, so the table below is a second copy of it and can
 * drift. A key with no entry therefore claims nothing about where its bytes go, rather than guessing —
 * a class added later cannot silently inherit the wrong half of this.</p>
 */
const PRE_CUTOVER_BY_DATA_CLASS: Record<string, string> = {
  "workflow-artifact/v1": "While this route is Draft, workflow artifacts keep writing to local storage.",
  "agent-run-log/v1": "While this route is Draft, agent run log capture is unavailable — this class has no home outside the routing plane.",
};

function preCutoverNote(dataClassTypeKey: string): string {
  return PRE_CUTOVER_BY_DATA_CLASS[dataClassTypeKey]
    ?? "While this route is Draft it moves no bytes. What this data class does instead is decided by the runtime consumer that reads it.";
}

/**
 * Draft and Disabled are NOT two names for "off". `StorageRouteSnapshotResolver` reports Draft as
 * RouteNotActivated and Disabled/Retired as RouteNotActive, and only the first one lets a class keep a
 * home outside the routing plane — for every class, disabling an Active route makes its writes fail.
 */
const DISABLED_NOTE = "Disabling a route does not return writes to local storage. A route that has never been activated leaves its data class exactly as it was before the route existed; a route you disable after activating it makes that class's writes fail until it is Active again.";

/** Team-scoped, versioned data-class routing. The control plane does not expose provider config or credentials. */
export function StorageRouteSettings({ profiles, state = "active" }: { profiles: StorageProfileSummary[]; state?: StorageStepState }) {
  const routes = useStorageRoutes();
  const mayManage = useTeamPermissions().can(TeamPermissions.StorageManage);
  const [createOpen, setCreateOpen] = useState(false);
  const [managedRoute, setManagedRoute] = useState<Pick<StorageRouteSummary, "id" | "dataClassTypeKey"> | null>(null);
  const rows = routes.data ?? [];
  const activeProfiles = profiles.filter((profile) => profile.state === "Active");
  const activeRoutes = rows.filter((route) => route.state === "Active");
  const routeError = errorMessage(routes.error);
  const activatable = rows.find((route) => route.state !== "Retired" && route.state !== "Active");
  const manage = (route: Pick<StorageRouteSummary, "id" | "dataClassTypeKey">) => setManagedRoute({ id: route.id, dataClassTypeKey: route.dataClassTypeKey });

  return (
    <StorageStep
      step="route"
      title="Data routing"
      titleId="storage-routes-title"
      state={state}
      precondition="a storage profile is Active — a route may only target one"
      line={routeLine(rows, activeRoutes, routes.isLoading, routeError)}
      action={mayManage ? (
        <>
          {/* The blocker is a route nobody cut over, so activating it — not creating another — is the
              accented act. Creating one for a second data class stays available beside it. */}
          {activeRoutes.length === 0 && activatable && (
            <button type="button" className={state === "active" ? "btn btn-primary" : "btn"} onClick={() => manage(activatable)}>Activate {activatable.dataClassTypeKey}</button>
          )}
          <button type="button" className={state === "active" && (activeRoutes.length > 0 || !activatable) ? "btn btn-primary" : "btn"} disabled={activeProfiles.length === 0 || routes.isLoading || routeError != null} onClick={() => setCreateOpen(true)}>Create data route</button>
        </>
      ) : undefined}
    >
      {routes.isLoading && <LoadingMessage>Loading data routes…</LoadingMessage>}
      {routeError && <ErrorBanner title="Couldn't load data routes" message={routeError} />}
      {!routes.isLoading && !routeError && rows.length === 0 && (
        <div className="stg-hint">{DATA_CLASS_DEFINITION} A new route is born Draft and never routes bytes; set it Active to cut over.</div>
      )}
      {!routes.isLoading && !routeError && rows.length > 0 && (
        <>
          <div className="cn-list" role="list" aria-label="Storage data routes">
            {rows.map((route) => <StorageRouteRow key={route.id} route={route} onManage={mayManage ? () => manage(route) : undefined} />)}
          </div>
          {routes.hasNextPage && (
            <button type="button" className="btn" disabled={routes.isFetchingNextPage} onClick={() => routes.fetchNextPage()}>
              {routes.isFetchingNextPage ? "Loading more data routes…" : "Load more data routes"}
            </button>
          )}
        </>
      )}

      {createOpen && <CreateStorageRouteDialog profiles={activeProfiles} onClose={() => setCreateOpen(false)} />}
      {managedRoute && <ManageStorageRouteDialog routeId={managedRoute.id} dataClassTypeKey={managedRoute.dataClassTypeKey} profiles={activeProfiles} onClose={() => setManagedRoute(null)} />}
    </StorageStep>
  );
}

/** The one line the routing step shows: which data classes this team actually routes right now. */
function routeLine(all: StorageRouteSummary[], active: StorageRouteSummary[], loading: boolean, error: string | null): string {
  if (error) return "The data routes could not be read.";
  if (loading) return "Loading data routes…";
  if (all.length === 0) return "No data routes configured";
  if (active.length === 0) return `${all.length} route${all.length === 1 ? "" : "s"}, none Active — no data class is routed yet`;
  if (all.length === 1) return `${active[0].dataClassTypeKey} → ${active[0].storageProfileStableName}`;
  return `${all.length} routes, ${active.length} Active`;
}

function StorageRouteRow({ route, onManage }: { route: StorageRouteSummary; onManage?: () => void }) {
  return (
    <div className="cn-row" role="listitem">
      <div className="cn-row-head">
        <div className="cn-mark">DR</div>
        <div className="cn-meta" style={{ flex: 1 }}>
          <div className="cn-name">
            {route.dataClassTypeKey}
            <span className={stateClass(route.state)}>{route.state}</span>
            <span className="cn-status">Revision {route.currentRevision}</span>
          </div>
          <div className="cn-sub">{route.storageProfileStableName} · {selectionLabel(route.profileRevisionMode, route.pinnedProfileRevision)}</div>
        </div>
        {onManage && <button type="button" className="btn" aria-label={`Manage ${route.dataClassTypeKey}`} onClick={onManage}>Manage</button>}
      </div>
    </div>
  );
}

function CreateStorageRouteDialog({ profiles, onClose }: { profiles: StorageProfileSummary[]; onClose: () => void }) {
  const create = useCreateStorageRoute();
  const dataClasses = useRoutedDataClasses();
  const routable = dataClasses.data ?? [];
  const [chosenDataClass, setChosenDataClass] = useState("");
  const [profileId, setProfileId] = useState(profiles[0]?.id ?? "");
  const [mode, setMode] = useState<StorageProfileRevisionMode>("CurrentAtWrite");
  const [pinnedRevision, setPinnedRevision] = useState(profiles[0]?.currentRevision ?? 1);
  const [actionError, setActionError] = useState<string | null>(null);
  const selectedProfile = profiles.find((profile) => profile.id === profileId);
  // Only a class this deployment reads can be routed, so the key is chosen from the catalog rather than typed.
  const dataClassTypeKey = routable.some((dataClass) => dataClass.typeKey === chosenDataClass) ? chosenDataClass : routable[0]?.typeKey ?? "";
  const validPinnedRevision = mode === "CurrentAtWrite" || selectedProfile != null && Number.isSafeInteger(pinnedRevision) && pinnedRevision > 0 && pinnedRevision <= selectedProfile.currentRevision;
  const canSubmit = dataClassTypeKey !== "" && selectedProfile != null && validPinnedRevision && !create.isPending;

  const chooseProfile = (id: string) => {
    const profile = profiles.find((candidate) => candidate.id === id);
    setProfileId(id);
    setPinnedRevision(profile?.currentRevision ?? 1);
    setActionError(null);
  };

  const submit = () => {
    if (!canSubmit || !selectedProfile) return;
    setActionError(null);
    create.mutate({
      dataClassTypeKey,
      storageProfileId: selectedProfile.id,
      profileRevisionMode: mode,
      pinnedProfileRevision: mode === "Pinned" ? pinnedRevision : null,
    }, {
      onSuccess: onClose,
      onError: (error) => setActionError(routeMutationErrorMessage(error, "Couldn't create the data route.")),
    });
  };

  return (
    <RouteModal label="Create data route" title="Create data route" subtitle="Creates revision 1 in Draft state. A Draft route never routes bytes; set it Active to cut over. The versioned data-class identity cannot be renamed." onClose={onClose}>
      <div className="mdl-body">
        <div className="wf-form">
          <div className="wf-form-row">
            <label className="wf-form-label" htmlFor="storage-route-data-class">Data class</label>
            <select id="storage-route-data-class" className="wf-form-input" value={dataClassTypeKey} onChange={(event) => setChosenDataClass(event.target.value)} disabled={routable.length === 0} autoFocus>
              {routable.map((dataClass) => <option key={dataClass.typeKey} value={dataClass.typeKey}>{dataClass.displayName} · {dataClass.typeKey}</option>)}
            </select>
            <span className="wf-form-help">{DATA_CLASS_DEFINITION}</span>
            {dataClassTypeKey !== "" && <span className="wf-form-help">{preCutoverNote(dataClassTypeKey)}</span>}
          </div>
          {dataClasses.isLoading && <LoadingMessage>Loading routable data classes…</LoadingMessage>}
          {dataClasses.error && <ErrorBanner title="Couldn't load routable data classes" message={errorMessage(dataClasses.error) ?? "The routable data classes could not be loaded."} />}
          {!dataClasses.isLoading && !dataClasses.error && routable.length === 0 && <Notice title="No routable data class">This deployment reads no routed data class, so a data route cannot be created.</Notice>}
          <ProfileSelectionFields profiles={profiles} profileId={profileId} mode={mode} pinnedRevision={pinnedRevision} idPrefix="storage-route-create" profileLabel="Storage profile" onProfileChange={chooseProfile} onModeChange={setMode} onPinnedRevisionChange={setPinnedRevision} />
          {actionError && <ErrorBanner title="Data route wasn't created" message={actionError} />}
        </div>
      </div>
      <div className="mdl-foot">
        <button type="button" className="btn btn-ghost" onClick={onClose}>Cancel</button>
        <button type="button" className="btn btn-primary" disabled={!canSubmit} onClick={submit}>{create.isPending ? "Creating…" : "Create Draft"}</button>
      </div>
    </RouteModal>
  );
}

function ManageStorageRouteDialog({ routeId, dataClassTypeKey, profiles, onClose }: { routeId: string; dataClassTypeKey: string; profiles: StorageProfileSummary[]; onClose: () => void }) {
  const route = useStorageRoute(routeId);
  const [actionError, setActionError] = useState<string | null>(null);
  const label = route.data?.dataClassTypeKey ?? dataClassTypeKey;

  return (
    <RouteModal label={`Manage data route ${label}`} title={label} subtitle="Append-only targets and optimistic lifecycle controls. Retired is terminal." onClose={onClose}>
      <div className="mdl-body">
        {route.isLoading && <LoadingMessage>Loading data route…</LoadingMessage>}
        {route.error && (
          <>
            <ErrorBanner title="Couldn't load data route" message={errorMessage(route.error) ?? "The data route could not be loaded."} />
            <button type="button" className="btn" onClick={() => route.refetch()}>Retry</button>
          </>
        )}
        {actionError && <ErrorBanner title="Data route wasn't changed" message={actionError} />}
        {route.data && (
          <StorageRouteEditor
            key={`${route.data.xmin}:${route.data.currentRevision}:${route.data.state}`}
            detail={route.data}
            profiles={profiles}
            hasMoreRevisions={route.hasNextPage}
            loadingMoreRevisions={route.isFetchingNextPage}
            onLoadMoreRevisions={() => route.fetchNextPage()}
            onActionError={setActionError}
          />
        )}
      </div>
      <div className="mdl-foot">
        <span className="mdl-foot-info">Route state takes effect on the next write</span>
        <button type="button" className="btn" onClick={onClose}>Close</button>
      </div>
    </RouteModal>
  );
}

interface StorageRouteEditorProps {
  detail: StorageRouteDetail;
  profiles: StorageProfileSummary[];
  hasMoreRevisions: boolean;
  loadingMoreRevisions: boolean;
  onLoadMoreRevisions: () => void;
  onActionError: (message: string | null) => void;
}

function StorageRouteEditor({ detail, profiles, hasMoreRevisions, loadingMoreRevisions, onLoadMoreRevisions, onActionError }: StorageRouteEditorProps) {
  const appendRevision = useAppendStorageRouteRevision();
  const setState = useSetStorageRouteState();
  const [profileId, setProfileId] = useState(profiles[0]?.id ?? "");
  const [mode, setMode] = useState<StorageProfileRevisionMode>("CurrentAtWrite");
  const [pinnedRevision, setPinnedRevision] = useState(profiles[0]?.currentRevision ?? 1);
  const [confirmRetire, setConfirmRetire] = useState(false);
  const selectedProfile = profiles.find((profile) => profile.id === profileId);
  const retired = detail.state === "Retired";
  const pending = appendRevision.isPending || setState.isPending;
  const validPinnedRevision = mode === "CurrentAtWrite" || selectedProfile != null && Number.isSafeInteger(pinnedRevision) && pinnedRevision > 0 && pinnedRevision <= selectedProfile.currentRevision;

  const chooseProfile = (id: string) => {
    const profile = profiles.find((candidate) => candidate.id === id);
    setProfileId(id);
    setPinnedRevision(profile?.currentRevision ?? 1);
    onActionError(null);
  };

  const append = () => {
    if (!selectedProfile || !validPinnedRevision || retired || pending) return;
    onActionError(null);
    appendRevision.mutate({
      routeId: detail.id,
      input: {
        expectedXmin: detail.xmin,
        expectedCurrentRevision: detail.currentRevision,
        storageProfileId: selectedProfile.id,
        profileRevisionMode: mode,
        pinnedProfileRevision: mode === "Pinned" ? pinnedRevision : null,
      },
    }, {
      onSuccess: () => onActionError(null),
      onError: (error) => onActionError(routeMutationErrorMessage(error, "Couldn't append the data route revision.")),
    });
  };

  const transition = (state: Exclude<StorageRouteState, "Draft">) => {
    if (retired || pending) return;
    onActionError(null);
    setState.mutate({ routeId: detail.id, input: { expectedXmin: detail.xmin, expectedCurrentRevision: detail.currentRevision, state } }, {
      onSuccess: () => onActionError(null),
      onError: (error) => onActionError(routeMutationErrorMessage(error, `Couldn't set the data route ${state.toLowerCase()}.`)),
    });
  };

  return (
    <>
      <div className="cn-banner">
        <div className="cn-banner-h">
          <span className={stateClass(detail.state)}>{detail.state}</span>
          <span style={{ marginLeft: 8 }}>Current revision {detail.currentRevision}</span>
        </div>
        <div className="cn-banner-p">Data class {detail.dataClassTypeKey}</div>
        {detail.state === "Draft" && <div className="cn-banner-p">{preCutoverNote(detail.dataClassTypeKey)}</div>}
      </div>

      <section aria-labelledby="storage-route-current-target" style={{ marginTop: 16 }}>
        <div className="wf-form-label" id="storage-route-current-target">Current target</div>
        <div className="cn-sub">{detail.currentTarget.storageProfileStableName} · {selectionLabel(detail.currentTarget.profileRevisionMode, detail.currentTarget.pinnedProfileRevision)}</div>
      </section>

      <section aria-labelledby="storage-route-revision-history" style={{ borderTop: "1px solid var(--line)", marginTop: 18, paddingTop: 16 }}>
        <div className="wf-form-label" id="storage-route-revision-history">Revision history</div>
        <div className="cn-list" role="list" aria-label="Data route revision history">
          {detail.revisionPage.items.map((revision) => <StorageRouteRevisionRow key={revision.id} revision={revision} />)}
        </div>
        {hasMoreRevisions && <button type="button" className="btn" disabled={loadingMoreRevisions} onClick={onLoadMoreRevisions}>{loadingMoreRevisions ? "Loading more route revisions…" : "Load more route revisions"}</button>}
      </section>

      <section aria-labelledby="storage-route-append" style={{ borderTop: "1px solid var(--line)", marginTop: 18, paddingTop: 16 }}>
        <div className="wf-form-label" id="storage-route-append">Append target revision</div>
        {profiles.length === 0 && <Notice title="No Active storage profile">Activate a profile before appending another route revision.</Notice>}
        {profiles.length > 0 && (
          <div className="wf-form" style={{ marginTop: 10 }}>
            <ProfileSelectionFields profiles={profiles} profileId={profileId} mode={mode} pinnedRevision={pinnedRevision} idPrefix="storage-route-revision" profileLabel="Revision storage profile" onProfileChange={chooseProfile} onModeChange={setMode} onPinnedRevisionChange={setPinnedRevision} />
            <button type="button" className="btn" disabled={!selectedProfile || !validPinnedRevision || retired || pending} onClick={append}>{appendRevision.isPending ? "Appending…" : "Append route revision"}</button>
          </div>
        )}
      </section>

      <section aria-labelledby="storage-route-state" style={{ borderTop: "1px solid var(--line)", marginTop: 18, paddingTop: 16 }}>
        <div className="wf-form-label" id="storage-route-state" style={{ marginBottom: 8 }}>Route state</div>
        <div className="wf-form-help" style={{ marginBottom: 10 }}>{DISABLED_NOTE}</div>
        <div style={{ display: "flex", flexWrap: "wrap", gap: 8 }}>
          <button type="button" className="btn" disabled={retired || detail.state === "Active" || pending} onClick={() => transition("Active")}>Set Active</button>
          <button type="button" className="btn" disabled={retired || detail.state === "Disabled" || pending} onClick={() => transition("Disabled")}>Set Disabled</button>
          <button type="button" className="btn btn-danger" disabled={retired || pending} onClick={() => setConfirmRetire(true)}>Retire route</button>
        </div>
      </section>

      {confirmRetire && <RouteRetireConfirmation dataClassTypeKey={detail.dataClassTypeKey} onCancel={() => setConfirmRetire(false)} onConfirm={() => { setConfirmRetire(false); transition("Retired"); }} />}
    </>
  );
}

function ProfileSelectionFields({ profiles, profileId, mode, pinnedRevision, idPrefix, profileLabel, onProfileChange, onModeChange, onPinnedRevisionChange }: { profiles: StorageProfileSummary[]; profileId: string; mode: StorageProfileRevisionMode; pinnedRevision: number; idPrefix: string; profileLabel: string; onProfileChange: (id: string) => void; onModeChange: (mode: StorageProfileRevisionMode) => void; onPinnedRevisionChange: (revision: number) => void }) {
  const selected = profiles.find((profile) => profile.id === profileId);
  return (
    <>
      <div className="wf-form-row">
        <label className="wf-form-label" htmlFor={`${idPrefix}-profile`}>{profileLabel}</label>
        <select id={`${idPrefix}-profile`} className="wf-form-input" value={profileId} onChange={(event) => onProfileChange(event.target.value)}>
          {profiles.map((profile) => <option key={profile.id} value={profile.id}>{profile.stableName} · current revision {profile.currentRevision}</option>)}
        </select>
      </div>
      <div className="wf-form-row">
        <label className="wf-form-label" htmlFor={`${idPrefix}-mode`}>Profile revision mode</label>
        <select id={`${idPrefix}-mode`} className="wf-form-input" value={mode} onChange={(event) => onModeChange(event.target.value === "Pinned" ? "Pinned" : "CurrentAtWrite")}>
          <option value="CurrentAtWrite">Current at write</option>
          <option value="Pinned">Pinned exact revision</option>
        </select>
      </div>
      {mode === "Pinned" && (
        <div className="wf-form-row">
          <label className="wf-form-label" htmlFor={`${idPrefix}-revision`}>Exact profile revision</label>
          <input id={`${idPrefix}-revision`} className="wf-form-input" type="number" min={1} max={selected?.currentRevision} step={1} value={pinnedRevision} onChange={(event) => onPinnedRevisionChange(Number(event.target.value))} />
          <span className="wf-form-help">Must identify an existing revision of the selected Active profile.</span>
        </div>
      )}
    </>
  );
}

function StorageRouteRevisionRow({ revision }: { revision: StorageRouteRevisionDetail }) {
  return (
    <div className="cn-row" role="listitem">
      <div className="cn-name">Revision {revision.revision}</div>
      <div className="cn-sub">{revision.storageProfileStableName} · {selectionLabel(revision.profileRevisionMode, revision.pinnedProfileRevision)}</div>
    </div>
  );
}

function RouteRetireConfirmation({ dataClassTypeKey, onCancel, onConfirm }: { dataClassTypeKey: string; onCancel: () => void; onConfirm: () => void }) {
  const confirmRef = useRef<HTMLButtonElement>(null);
  useEffect(() => { confirmRef.current?.focus(); }, []);
  return createPortal(
    <>
      <div className="mdl-mask" aria-hidden="true" style={{ zIndex: 90 }} />
      <div className="mdl mdl-dialog" role="alertdialog" aria-modal="true" aria-label={`Retire ${dataClassTypeKey}?`} style={{ zIndex: 91 }}>
        <div className="mdl-dialog-head"><div className="mdl-dialog-title">Retire {dataClassTypeKey}?</div></div>
        <div className="mdl-dialog-body">Retirement is terminal. This data-class identity cannot receive revisions or change state afterward.</div>
        <div className="mdl-dialog-foot">
          <button type="button" className="btn" onClick={onCancel}>Cancel</button>
          <button ref={confirmRef} type="button" className="btn btn-danger" onClick={onConfirm}>Retire permanently</button>
        </div>
      </div>
    </>,
    document.body,
  );
}

function RouteModal({ label, title, subtitle, onClose, children }: { label: string; title: string; subtitle: string; onClose: () => void; children: ReactNode }) {
  return createPortal(
    <>
      <div className="mdl-mask" aria-hidden="true" />
      <div className="mdl" role="dialog" aria-modal="true" aria-label={label} style={{ width: 680, maxWidth: "94vw" }}>
        <div className="mdl-head">
          <div className="mdl-title-wrap"><div className="mdl-title">{title}</div><div className="mdl-sub">{subtitle}</div></div>
          <button type="button" className="mdl-x" aria-label="Close" onClick={onClose}>×</button>
        </div>
        {children}
      </div>
    </>,
    document.body,
  );
}

function Notice({ title, children }: { title: string; children: ReactNode }) {
  return <div className="cn-banner"><div className="cn-banner-h">{title}</div><div className="cn-banner-p">{children}</div></div>;
}

function ErrorBanner({ title, message }: { title: string; message: string }) {
  return <div className="cn-banner cn-banner-err" role="alert"><div className="cn-banner-h">{title}</div><div className="cn-banner-p">{message}</div></div>;
}

function LoadingMessage({ children }: { children: ReactNode }) {
  return <div className="ct-empty" role="status"><div className="ct-empty-h">{children}</div></div>;
}

function stateClass(state: StorageRouteState): string {
  if (state === "Active") return "cn-status cn-status-active";
  if (state === "Retired") return "cn-status cn-status-revoked";
  return "cn-status";
}

function selectionLabel(mode: StorageProfileRevisionMode, pinnedRevision: number | null): string {
  return mode === "Pinned" ? `pinned profile revision ${pinnedRevision}` : "current at write";
}

/**
 * Not every 409 is a concurrent edit: creating a second route for a data class the team already routes
 * answers "Storage route '…' already exists in this team.", which the old catch-all replaced with a
 * sentence that was both vaguer and untrue. The server's own words stand; only the reload note is ours.
 */
function routeMutationErrorMessage(error: unknown, fallback: string): string {
  if (!(error instanceof ApiError) || error.status !== 409) return errorMessage(error) ?? fallback;
  const reason = error.message.trim();
  return reason.length === 0 ? fallback : `${reason} The latest data was reloaded — review it before trying again.`;
}

function errorMessage(error: unknown): string | null {
  if (error instanceof ApiError || error instanceof Error) return error.message;
  return error == null ? null : "An unexpected data routing error occurred.";
}
