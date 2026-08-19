import { useEffect, useRef, useState, type ReactNode } from "react";
import { createPortal } from "react-dom";

import { ApiError } from "@/api/request";
import type { StorageProfileSummary } from "@/api/storage";
import type { StorageProfileRevisionMode, StorageRouteDetail, StorageRouteRevisionDetail, StorageRouteState, StorageRouteSummary } from "@/api/storageRoutes";
import { useAppendStorageRouteRevision, useCreateStorageRoute, useRoutedDataClasses, useSetStorageRouteState, useStorageRoute, useStorageRoutes } from "@/hooks/use-storage-routes";

/** Team-scoped, versioned data-class routing. The control plane does not expose provider config or credentials. */
export function StorageRouteSettings({ profiles }: { profiles: StorageProfileSummary[] }) {
  const routes = useStorageRoutes();
  const [createOpen, setCreateOpen] = useState(false);
  const [managedRoute, setManagedRoute] = useState<Pick<StorageRouteSummary, "id" | "dataClassTypeKey"> | null>(null);
  const rows = routes.data ?? [];
  const activeProfiles = profiles.filter((profile) => profile.state === "Active");

  return (
    <section aria-labelledby="storage-routes-title" style={{ margin: 16 }}>
      <div className="cn-listhead">
        <div>
          <h3 className="cn-listhead-l" id="storage-routes-title">Data routing</h3>
          <div className="cn-listhead-c">Versioned data-class policy</div>
        </div>
        <button type="button" className="btn btn-primary" disabled={activeProfiles.length === 0 || routes.isLoading || routes.error != null} onClick={() => setCreateOpen(true)}>Create data route</button>
      </div>

      {activeProfiles.length === 0 && <Notice title="No Active storage profile">Activate a storage profile before creating or revising a data route.</Notice>}
      {routes.isLoading && <LoadingMessage>Loading data routes…</LoadingMessage>}
      {routes.error && <ErrorBanner title="Couldn't load data routes" message={errorMessage(routes.error) ?? "The data routes could not be loaded."} />}
      {!routes.isLoading && !routes.error && rows.length === 0 && (
        <div className="ct-empty">
          <div className="ct-empty-h">No data routes configured</div>
          <div className="ct-empty-p">Create an immutable, versioned data-class identity in Draft state. A Draft route never routes bytes; set it Active to cut over.</div>
        </div>
      )}
      {!routes.isLoading && !routes.error && rows.length > 0 && (
        <>
          <div className="cn-list" role="list" aria-label="Storage data routes">
            {rows.map((route) => <StorageRouteRow key={route.id} route={route} onManage={() => setManagedRoute({ id: route.id, dataClassTypeKey: route.dataClassTypeKey })} />)}
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
    </section>
  );
}

function StorageRouteRow({ route, onManage }: { route: StorageRouteSummary; onManage: () => void }) {
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
        <button type="button" className="btn" aria-label={`Manage ${route.dataClassTypeKey}`} onClick={onManage}>Manage</button>
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
            <span className="wf-form-help">Only a class this deployment reads can be routed. While the route is Draft, workflow artifacts keep local storage and agent run log capture is unavailable.</span>
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

function routeMutationErrorMessage(error: unknown, fallback: string): string {
  if (error instanceof ApiError && error.status === 409)
    return "This data route changed elsewhere. The latest route and history were reloaded; review the current target and try again.";
  return errorMessage(error) ?? fallback;
}

function errorMessage(error: unknown): string | null {
  if (error instanceof ApiError || error instanceof Error) return error.message;
  return error == null ? null : "An unexpected data routing error occurred.";
}
