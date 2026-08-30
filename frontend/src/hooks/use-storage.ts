import { useInfiniteQuery, useMutation, useQuery, useQueryClient } from "@tanstack/react-query";

import { ApiError } from "@/api/request";
import { storageApi } from "@/api/storage";
import type { AppendStorageCredentialRevisionInput, AppendStorageProfileRevisionInput, CreateStorageCredentialInput, CreateStorageProfileInput, ProbeStorageProfileInput, RevokeStorageCredentialInput, SetStorageProfileStateInput } from "@/api/storage";
import { refreshStorageRouteQueries } from "@/hooks/use-storage-routes";

export const STORAGE_PROVIDER_MODULES_KEY = ["storage", "provider-modules"] as const;
export const STORAGE_CREDENTIALS_KEY = ["storage", "credentials"] as const;
export const STORAGE_PROFILES_KEY = ["storage", "profiles"] as const;
export const storageProfileKey = (profileId: string) => ["storage", "profiles", profileId] as const;
export const STORAGE_PLACEMENT_INTEGRITY_KEY = ["storage", "placements", "integrity"] as const;

/**
 * How often an open storage page re-asks for what the background sweeps observed. One minute is far inside the
 * fifteen-minute detection promise and costs two small team-scoped queries per tick on a settings page.
 */
export const STORAGE_OBSERVATION_POLL_MS = 60_000;

/**
 * What became of the bytes already stored. Genuinely refetched on an interval — staleTime alone never fires a
 * request on an open page (this app disables refetch-on-focus globally), and this data changes without anything on
 * the page being touched: a sweep confirms or demotes placements while nobody is looking.
 */
export function usePlacementIntegrity() {
  return useQuery({
    queryKey: STORAGE_PLACEMENT_INTEGRITY_KEY,
    queryFn: ({ signal }) => storageApi.getPlacementIntegrity(signal),
    staleTime: 60_000,
    refetchInterval: STORAGE_OBSERVATION_POLL_MS,
  });
}

/** Installed provider types are build metadata, so they remain fresh until this deployment changes. */
export function useStorageProviderModules() {
  return useQuery({
    queryKey: STORAGE_PROVIDER_MODULES_KEY,
    queryFn: () => storageApi.listProviderModules(),
    staleTime: Infinity,
  });
}

/**
 * Profiles carry the health badge, and health is written by background sweeps — the destination probe runs every
 * fifteen minutes against a ten-minute staleness window, so "red within fifteen minutes" is only true end to end if
 * an OPEN page actually asks again. Nothing else would: refetch-on-focus is disabled app-wide, and staleTime never
 * initiates a request by itself.
 */
export function useStorageProfiles() {
  return useInfiniteQuery({
    queryKey: STORAGE_PROFILES_KEY,
    queryFn: ({ pageParam, signal }) => storageApi.listProfilePage(pageParam, 50, signal),
    initialPageParam: null as string | null,
    getNextPageParam: (lastPage) => lastPage.nextCursor ?? undefined,
    select: (data) => data.pages.flatMap((page) => page.items),
    refetchInterval: STORAGE_OBSERVATION_POLL_MS,
  });
}

export function useStorageCredentials() {
  return useInfiniteQuery({
    queryKey: STORAGE_CREDENTIALS_KEY,
    queryFn: ({ pageParam, signal }) => storageApi.listCredentialPage(pageParam, 50, signal),
    initialPageParam: null as string | null,
    getNextPageParam: (lastPage) => lastPage.nextCursor ?? undefined,
    select: (data) => data.pages.flatMap((page) => page.items),
  });
}

export function useCreateStorageCredential() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (input: CreateStorageCredentialInput) => storageApi.createCredential(input),
    gcTime: 0,
    onSuccess: () => queryClient.invalidateQueries({ queryKey: STORAGE_CREDENTIALS_KEY, exact: true }),
  });
}

export function useAppendStorageCredentialRevision() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: ({ credentialId, input }: { credentialId: string; input: AppendStorageCredentialRevisionInput }) => storageApi.appendCredentialRevision(credentialId, input),
    gcTime: 0,
    onSuccess: () => queryClient.invalidateQueries({ queryKey: STORAGE_CREDENTIALS_KEY, exact: true }),
    onError: (error) => refreshCredentialsAfterConflict(queryClient, error),
  });
}

export function useRevokeStorageCredential() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: ({ credentialId, input }: { credentialId: string; input: RevokeStorageCredentialInput }) => storageApi.revokeCredential(credentialId, input),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: STORAGE_CREDENTIALS_KEY, exact: true }),
    onError: (error) => refreshCredentialsAfterConflict(queryClient, error),
  });
}

export function useStorageProfile(profileId: string | null) {
  return useQuery({
    queryKey: storageProfileKey(profileId ?? "none"),
    queryFn: () => storageApi.getProfile(profileId!),
    enabled: profileId != null,
  });
}

export function useCreateStorageProfile() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (input: CreateStorageProfileInput) => storageApi.createProfile(input),
    onSuccess: async (created) => {
      queryClient.setQueryData(storageProfileKey(created.id), created);
      await Promise.all([
        queryClient.invalidateQueries({ queryKey: STORAGE_PROFILES_KEY, exact: true }),
        refreshStorageRouteQueries(queryClient),
      ]);
    },
  });
}

export function useAppendStorageProfileRevision() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: ({ profileId, input }: { profileId: string; input: AppendStorageProfileRevisionInput }) => storageApi.appendProfileRevision(profileId, input),
    onSuccess: async (updated) => {
      queryClient.setQueryData(storageProfileKey(updated.id), updated);
      await Promise.all([
        queryClient.invalidateQueries({ queryKey: STORAGE_PROFILES_KEY, exact: true }),
        refreshStorageRouteQueries(queryClient),
      ]);
    },
    onError: (error, variables) => refreshAfterConflict(queryClient, error, variables.profileId),
  });
}

export function useSetStorageProfileState() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: ({ profileId, input }: { profileId: string; input: SetStorageProfileStateInput }) => storageApi.setProfileState(profileId, input),
    onSuccess: async (updated) => {
      queryClient.setQueryData(storageProfileKey(updated.id), updated);
      await Promise.all([
        queryClient.invalidateQueries({ queryKey: STORAGE_PROFILES_KEY, exact: true }),
        refreshStorageRouteQueries(queryClient),
      ]);
    },
    onError: (error, variables) => refreshAfterConflict(queryClient, error, variables.profileId),
  });
}

export function useProbeStorageProfile() {
  return useMutation({
    mutationFn: ({ profileId, input, signal }: { profileId: string; input: ProbeStorageProfileInput; signal: AbortSignal }) => storageApi.probeProfile(profileId, input, signal),
    gcTime: 0,
  });
}

function refreshAfterConflict(queryClient: ReturnType<typeof useQueryClient>, error: unknown, profileId: string) {
  if (!(error instanceof ApiError) || error.status !== 409) return;
  return Promise.all([
    queryClient.invalidateQueries({ queryKey: STORAGE_PROFILES_KEY, exact: true }),
    queryClient.invalidateQueries({ queryKey: storageProfileKey(profileId), exact: true }),
    refreshStorageRouteQueries(queryClient),
  ]);
}

function refreshCredentialsAfterConflict(queryClient: ReturnType<typeof useQueryClient>, error: unknown) {
  if (!(error instanceof ApiError) || error.status !== 409) return;
  return queryClient.invalidateQueries({ queryKey: STORAGE_CREDENTIALS_KEY, exact: true });
}
