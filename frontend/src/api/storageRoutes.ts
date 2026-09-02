import { fetchJson } from "./request";
import type { StoragePage } from "./storage";

export const STORAGE_ROUTE_PAGE_SIZE = 50;
export const STORAGE_ROUTE_REVISION_PAGE_SIZE = 25;

export type StorageRouteState = "Draft" | "Active" | "Disabled" | "Retired";
export type StorageProfileRevisionMode = "CurrentAtWrite" | "Pinned";

/** One versioned data class this deployment can route. Discovery metadata — never a route target. */
export interface RoutedDataClass {
  typeKey: string;
  displayName: string;
  /** True when this class has a durable home outside the routing plane, so leaving it unrouted stores it somewhere rather than losing it. */
  hasLocalFallback: boolean;
}

export interface StorageRouteSummary {
  id: string;
  dataClassTypeKey: string;
  state: StorageRouteState;
  currentRevision: number;
  xmin: number;
  storageProfileId: string;
  storageProfileStableName: string;
  profileRevisionMode: StorageProfileRevisionMode;
  pinnedProfileRevision: number | null;
  createdDate: string;
  lastModifiedDate: string;
}

export interface StorageRouteRevisionDetail {
  id: string;
  revision: number;
  storageProfileId: string;
  storageProfileStableName: string;
  profileRevisionMode: StorageProfileRevisionMode;
  pinnedProfileRevision: number | null;
  createdDate: string;
  createdBy: string;
}

export interface StorageRouteDetail {
  id: string;
  dataClassTypeKey: string;
  state: StorageRouteState;
  currentRevision: number;
  xmin: number;
  createdDate: string;
  createdBy: string;
  lastModifiedDate: string;
  lastModifiedBy: string;
  currentTarget: StorageRouteRevisionDetail;
  revisionPage: StoragePage<StorageRouteRevisionDetail>;
}

export interface CreateStorageRouteInput {
  dataClassTypeKey: string;
  storageProfileId: string;
  profileRevisionMode: StorageProfileRevisionMode;
  pinnedProfileRevision: number | null;
}

export interface AppendStorageRouteRevisionInput {
  expectedXmin: number;
  expectedCurrentRevision: number;
  storageProfileId: string;
  profileRevisionMode: StorageProfileRevisionMode;
  pinnedProfileRevision: number | null;
}

export interface SetStorageRouteStateInput {
  expectedXmin: number;
  expectedCurrentRevision: number;
  state: Exclude<StorageRouteState, "Draft">;
}

export const storageRouteApi = {
  listDataClasses: async (signal?: AbortSignal) => parseDataClasses(await fetchJson<unknown>("/api/storage/data-classes", { signal })),
  listPage: async (cursor: string | null, limit = STORAGE_ROUTE_PAGE_SIZE, signal?: AbortSignal) => {
    const query = new URLSearchParams({ limit: String(limit) });
    if (cursor) query.set("cursor", cursor);
    return parseRoutePage(await fetchJson<unknown>(`/api/storage/routes/page?${query}`, { signal }));
  },
  get: async (routeId: string, revisionCursor: string | null, revisionLimit = STORAGE_ROUTE_REVISION_PAGE_SIZE, signal?: AbortSignal) => {
    const query = new URLSearchParams({ revisionLimit: String(revisionLimit) });
    if (revisionCursor) query.set("revisionCursor", revisionCursor);
    return requireRouteIdentity(parseRouteDetail(await fetchJson<unknown>(`/api/storage/routes/${encodeURIComponent(routeId)}?${query}`, { signal }), revisionCursor == null), routeId);
  },
  create: async (input: CreateStorageRouteInput) => parseRouteDetail(await fetchJson<unknown>("/api/storage/routes", {
    method: "POST",
    body: JSON.stringify(input),
  })),
  appendRevision: async (routeId: string, input: AppendStorageRouteRevisionInput) => requireRouteIdentity(parseRouteDetail(await fetchJson<unknown>(`/api/storage/routes/${encodeURIComponent(routeId)}/revisions`, {
    method: "POST",
    body: JSON.stringify(input),
  }), true), routeId),
  setState: async (routeId: string, input: SetStorageRouteStateInput) => requireRouteIdentity(parseRouteDetail(await fetchJson<unknown>(`/api/storage/routes/${encodeURIComponent(routeId)}/state`, {
    method: "PUT",
    body: JSON.stringify(input),
  }), true), routeId),
};

function parseDataClasses(value: unknown): RoutedDataClass[] {
  if (!Array.isArray(value)) throw new Error("The routable data class list is invalid.");
  return value.map((item) => {
    const dataClass = record(item, "routable data class");
    return {
      typeKey: dataClassTypeKey(dataClass.typeKey),
      displayName: string(dataClass.displayName, "routable data class name"),
      hasLocalFallback: dataClass.hasLocalFallback === true,
    };
  });
}

function parseRoutePage(value: unknown): StoragePage<StorageRouteSummary> {
  const page = record(value, "storage route page");
  if (!Array.isArray(page.items)) throw new Error("The storage route page is invalid.");
  return { items: page.items.map(parseRouteSummary), nextCursor: nullableString(page.nextCursor, "storage route cursor") };
}

function parseRouteSummary(value: unknown): StorageRouteSummary {
  const route = record(value, "storage route");
  const currentRevision = positiveInteger(route.currentRevision, "storage route current revision");
  const selection = parseSelection(route);
  return {
    id: string(route.id, "storage route id"),
    dataClassTypeKey: dataClassTypeKey(route.dataClassTypeKey),
    state: routeState(route.state),
    currentRevision,
    xmin: nonNegativeInteger(route.xmin, "storage route xmin"),
    storageProfileId: string(route.storageProfileId, "storage profile id"),
    storageProfileStableName: string(route.storageProfileStableName, "storage profile stable name"),
    ...selection,
    createdDate: string(route.createdDate, "storage route created date"),
    lastModifiedDate: string(route.lastModifiedDate, "storage route modified date"),
  };
}

function parseRouteDetail(value: unknown, requireCurrentTargetInPage = true): StorageRouteDetail {
  const route = record(value, "storage route detail");
  const currentRevision = positiveInteger(route.currentRevision, "storage route current revision");
  const currentTarget = parseRouteRevision(route.currentTarget);
  if (currentTarget.revision !== currentRevision) throw new Error("The storage route current target does not match its current revision.");
  const revisionPage = parseRevisionPage(route.revisionPage);
  if (requireCurrentTargetInPage && !revisionPage.items.some((revision) => revision.id === currentTarget.id && revision.revision === currentTarget.revision))
    throw new Error("The storage route current target is missing from the first revision page.");
  return {
    id: string(route.id, "storage route id"),
    dataClassTypeKey: dataClassTypeKey(route.dataClassTypeKey),
    state: routeState(route.state),
    currentRevision,
    xmin: nonNegativeInteger(route.xmin, "storage route xmin"),
    createdDate: string(route.createdDate, "storage route created date"),
    createdBy: string(route.createdBy, "storage route creator"),
    lastModifiedDate: string(route.lastModifiedDate, "storage route modified date"),
    lastModifiedBy: string(route.lastModifiedBy, "storage route modifier"),
    currentTarget,
    revisionPage,
  };
}

function requireRouteIdentity(detail: StorageRouteDetail, expectedRouteId: string): StorageRouteDetail {
  if (detail.id !== expectedRouteId) throw new Error("The storage route response does not match the requested route.");
  return detail;
}

function parseRevisionPage(value: unknown): StoragePage<StorageRouteRevisionDetail> {
  const page = record(value, "storage route revision page");
  if (!Array.isArray(page.items)) throw new Error("The storage route revision page is invalid.");
  const items = page.items.map(parseRouteRevision);
  if (items.some((item, index) => index > 0 && items[index - 1].revision <= item.revision))
    throw new Error("The storage route revision page is not strictly descending.");
  return { items, nextCursor: nullableString(page.nextCursor, "storage route revision cursor") };
}

function parseRouteRevision(value: unknown): StorageRouteRevisionDetail {
  const revision = record(value, "storage route revision");
  return {
    id: string(revision.id, "storage route revision id"),
    revision: positiveInteger(revision.revision, "storage route revision"),
    storageProfileId: string(revision.storageProfileId, "storage profile id"),
    storageProfileStableName: string(revision.storageProfileStableName, "storage profile stable name"),
    ...parseSelection(revision),
    createdDate: string(revision.createdDate, "storage route revision created date"),
    createdBy: string(revision.createdBy, "storage route revision creator"),
  };
}

function parseSelection(value: Record<string, unknown>): Pick<StorageRouteRevisionDetail, "profileRevisionMode" | "pinnedProfileRevision"> {
  const profileRevisionMode = revisionMode(value.profileRevisionMode);
  const pinnedProfileRevision = nullablePositiveInteger(value.pinnedProfileRevision, "pinned storage profile revision");
  if (profileRevisionMode === "CurrentAtWrite" && pinnedProfileRevision != null)
    throw new Error("A current-at-write storage route cannot contain a pinned profile revision.");
  if (profileRevisionMode === "Pinned" && pinnedProfileRevision == null)
    throw new Error("A pinned storage route requires an exact profile revision.");
  return { profileRevisionMode, pinnedProfileRevision };
}

function routeState(value: unknown): StorageRouteState {
  if (value === "Draft" || value === "Active" || value === "Disabled" || value === "Retired") return value;
  throw new Error("The response contains an unsupported storage route state.");
}

function revisionMode(value: unknown): StorageProfileRevisionMode {
  if (value === "CurrentAtWrite" || value === "Pinned") return value;
  throw new Error("The response contains an unsupported storage profile revision mode.");
}

function dataClassTypeKey(value: unknown): string {
  const key = string(value, "storage data class type key");
  if (!/^[a-z0-9][a-z0-9.-]*\/v[1-9][0-9]*$/.test(key) || key.length > 128)
    throw new Error("The response contains an invalid storage data class type key.");
  return key;
}

function record(value: unknown, label: string): Record<string, unknown> {
  if (typeof value !== "object" || value == null || Array.isArray(value)) throw new Error(`The ${label} is invalid.`);
  return value as Record<string, unknown>;
}

function string(value: unknown, label: string): string {
  if (typeof value !== "string" || value.length === 0) throw new Error(`The ${label} is invalid.`);
  return value;
}

function nullableString(value: unknown, label: string): string | null {
  if (value === null) return null;
  return string(value, label);
}

function positiveInteger(value: unknown, label: string): number {
  if (typeof value !== "number" || !Number.isSafeInteger(value) || value <= 0) throw new Error(`The ${label} is invalid.`);
  return value;
}

function nullablePositiveInteger(value: unknown, label: string): number | null {
  if (value === null) return null;
  return positiveInteger(value, label);
}

function nonNegativeInteger(value: unknown, label: string): number {
  if (typeof value !== "number" || !Number.isSafeInteger(value) || value < 0) throw new Error(`The ${label} is invalid.`);
  return value;
}
