import { useInfiniteQuery, useMutation, useQuery, useQueryClient, type QueryClient } from "@tanstack/react-query";

import { STORAGE_ROUTE_PAGE_SIZE, STORAGE_ROUTE_REVISION_PAGE_SIZE, storageRouteApi } from "@/api/storageRoutes";
import type { AppendStorageRouteRevisionInput, CreateStorageRouteInput, SetStorageRouteStateInput, StorageRouteDetail, StorageRouteRevisionDetail } from "@/api/storageRoutes";

export const STORAGE_ROUTES_KEY = ["storage", "routes", "list"] as const;
export const STORAGE_DATA_CLASSES_KEY = ["storage", "routes", "data-classes"] as const;
export const STORAGE_ROUTE_DETAILS_KEY = ["storage", "routes", "detail"] as const;
export const storageRouteKey = (routeId: string) => [...STORAGE_ROUTE_DETAILS_KEY, routeId] as const;

/** The routable data classes are build metadata, so they stay fresh until this deployment changes. */
export function useRoutedDataClasses() {
  return useQuery({
    queryKey: STORAGE_DATA_CLASSES_KEY,
    queryFn: ({ signal }) => storageRouteApi.listDataClasses(signal),
    staleTime: Infinity,
  });
}

export function useStorageRoutes() {
  return useInfiniteQuery({
    queryKey: STORAGE_ROUTES_KEY,
    queryFn: ({ pageParam, signal }) => storageRouteApi.listPage(pageParam, STORAGE_ROUTE_PAGE_SIZE, signal),
    initialPageParam: null as string | null,
    getNextPageParam: (lastPage) => lastPage.nextCursor ?? undefined,
    select: (data) => uniqueBy(data.pages.flatMap((page) => page.items), (route) => route.id),
  });
}

export function useStorageRoute(routeId: string | null) {
  return useInfiniteQuery({
    queryKey: storageRouteKey(routeId ?? "none"),
    queryFn: ({ pageParam, signal }) => storageRouteApi.get(routeId!, pageParam, STORAGE_ROUTE_REVISION_PAGE_SIZE, signal),
    enabled: routeId != null,
    initialPageParam: null as string | null,
    getNextPageParam: (lastPage) => lastPage.revisionPage.nextCursor ?? undefined,
    select: combineDetailPages,
  });
}

export function useCreateStorageRoute() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (input: CreateStorageRouteInput) => storageRouteApi.create(input),
    onSuccess: () => refreshStorageRoutes(queryClient),
    onError: () => refreshStorageRoutes(queryClient),
  });
}

export function useAppendStorageRouteRevision() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: ({ routeId, input }: { routeId: string; input: AppendStorageRouteRevisionInput }) => storageRouteApi.appendRevision(routeId, input),
    onSuccess: (_, variables) => refreshStorageRoutes(queryClient, variables.routeId),
    onError: (_, variables) => refreshStorageRoutes(queryClient, variables.routeId),
  });
}

export function useSetStorageRouteState() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: ({ routeId, input }: { routeId: string; input: SetStorageRouteStateInput }) => storageRouteApi.setState(routeId, input),
    onSuccess: (_, variables) => refreshStorageRoutes(queryClient, variables.routeId),
    onError: (_, variables) => refreshStorageRoutes(queryClient, variables.routeId),
  });
}

export function refreshStorageRouteQueries(queryClient: QueryClient) {
  return Promise.all([
    queryClient.invalidateQueries({ queryKey: STORAGE_ROUTES_KEY, exact: true }),
    queryClient.resetQueries({ queryKey: STORAGE_ROUTE_DETAILS_KEY }),
  ]);
}

function refreshStorageRoutes(queryClient: QueryClient, routeId?: string) {
  return Promise.all([
    queryClient.invalidateQueries({ queryKey: STORAGE_ROUTES_KEY, exact: true }),
    queryClient.resetQueries({ queryKey: routeId ? storageRouteKey(routeId) : STORAGE_ROUTE_DETAILS_KEY, exact: routeId != null }),
  ]);
}

function combineDetailPages(data: { pages: StorageRouteDetail[] }): StorageRouteDetail {
  const first = data.pages[0];
  if (!first) throw new Error("The storage route detail response is empty.");
  for (const page of data.pages.slice(1)) {
    if (page.id !== first.id || page.dataClassTypeKey !== first.dataClassTypeKey || page.xmin !== first.xmin || page.currentRevision !== first.currentRevision)
      throw new Error("The storage route changed while its revision history was loading. Refresh and try again.");
  }
  const revisions = uniqueRevisions(data.pages.flatMap((page) => page.revisionPage.items));
  return {
    ...first,
    revisionPage: {
      items: revisions.sort((left, right) => right.revision - left.revision),
      nextCursor: data.pages.at(-1)?.revisionPage.nextCursor ?? null,
    },
  };
}

function uniqueRevisions(revisions: StorageRouteRevisionDetail[]): StorageRouteRevisionDetail[] {
  const byRevision = new Map<number, StorageRouteRevisionDetail>();
  for (const revision of revisions) {
    const existing = byRevision.get(revision.revision);
    if (existing && existing.id !== revision.id) throw new Error("The storage route history contains conflicting revision identities.");
    if (!existing) byRevision.set(revision.revision, revision);
  }
  return [...byRevision.values()];
}

function uniqueBy<T>(values: T[], key: (value: T) => string): T[] {
  const seen = new Set<string>();
  return values.filter((value) => {
    const identity = key(value);
    if (seen.has(identity)) return false;
    seen.add(identity);
    return true;
  });
}
