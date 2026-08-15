import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";

import { ApiError } from "@/api/request";
import { storageApi } from "@/api/storage";
import type { AppendStorageProfileRevisionInput, CreateStorageProfileInput, SetStorageProfileStateInput } from "@/api/storage";

export const STORAGE_PROVIDER_MODULES_KEY = ["storage", "provider-modules"] as const;
export const STORAGE_PROFILES_KEY = ["storage", "profiles"] as const;
export const storageProfileKey = (profileId: string) => ["storage", "profiles", profileId] as const;

/** Installed provider types are build metadata, so they remain fresh until this deployment changes. */
export function useStorageProviderModules() {
  return useQuery({
    queryKey: STORAGE_PROVIDER_MODULES_KEY,
    queryFn: () => storageApi.listProviderModules(),
    staleTime: Infinity,
  });
}

export function useStorageProfiles() {
  return useQuery({ queryKey: STORAGE_PROFILES_KEY, queryFn: () => storageApi.listProfiles() });
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
    onSuccess: (created) => {
      queryClient.setQueryData(storageProfileKey(created.id), created);
      return queryClient.invalidateQueries({ queryKey: STORAGE_PROFILES_KEY, exact: true });
    },
  });
}

export function useAppendStorageProfileRevision() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: ({ profileId, input }: { profileId: string; input: AppendStorageProfileRevisionInput }) => storageApi.appendProfileRevision(profileId, input),
    onSuccess: (updated) => {
      queryClient.setQueryData(storageProfileKey(updated.id), updated);
      return queryClient.invalidateQueries({ queryKey: STORAGE_PROFILES_KEY, exact: true });
    },
    onError: (error, variables) => refreshAfterConflict(queryClient, error, variables.profileId),
  });
}

export function useSetStorageProfileState() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: ({ profileId, input }: { profileId: string; input: SetStorageProfileStateInput }) => storageApi.setProfileState(profileId, input),
    onSuccess: (updated) => {
      queryClient.setQueryData(storageProfileKey(updated.id), updated);
      return queryClient.invalidateQueries({ queryKey: STORAGE_PROFILES_KEY, exact: true });
    },
    onError: (error, variables) => refreshAfterConflict(queryClient, error, variables.profileId),
  });
}

function refreshAfterConflict(queryClient: ReturnType<typeof useQueryClient>, error: unknown, profileId: string) {
  if (!(error instanceof ApiError) || error.status !== 409) return;
  return Promise.all([
    queryClient.invalidateQueries({ queryKey: STORAGE_PROFILES_KEY, exact: true }),
    queryClient.invalidateQueries({ queryKey: storageProfileKey(profileId), exact: true }),
  ]);
}
