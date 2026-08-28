import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";

import { storageDefaultProvidersApi } from "@/api/storageDefaultProviders";
import { storageDefaultsApi } from "@/api/storageDefaults";
import type { CreateStorageDefaultInput, SetStorageDefaultEnabledInput, UpdateStorageDefaultInput } from "@/api/storageDefaults";

export const STORAGE_DEFAULT_PROVIDERS_KEY = ["admin", "storage-defaults", "provider-modules"] as const;
export const STORAGE_DEFAULTS_KEY = ["admin", "storage-defaults"] as const;
export const storageDefaultKey = (defaultId: string) => ["admin", "storage-defaults", defaultId] as const;

export function useStorageDefaults() {
  return useQuery({
    queryKey: STORAGE_DEFAULTS_KEY,
    queryFn: ({ signal }) => storageDefaultsApi.list(signal),
  });
}

/** The list carries no configuration, so an editor has to fetch the one row it is about to change. */
export function useStorageDefault(defaultId: string | null) {
  return useQuery({
    queryKey: storageDefaultKey(defaultId ?? ""),
    queryFn: ({ signal }) => storageDefaultsApi.get(defaultId!, signal),
    enabled: defaultId != null,
  });
}

export function useCreateStorageDefault() {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: (input: CreateStorageDefaultInput) => storageDefaultsApi.create(input),
    onSuccess: () => void queryClient.invalidateQueries({ queryKey: STORAGE_DEFAULTS_KEY }),
  });
}

/**
 * Both edits carry the row's own xmin and revision, so a second operator editing the same template is
 * refused rather than silently overwritten. Refetched on EVERY settled outcome, not only success: a
 * rejected edit is exactly the case where this screen's copy is known to be stale.
 */
export function useUpdateStorageDefault(defaultId: string) {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: (input: UpdateStorageDefaultInput) => storageDefaultsApi.update(defaultId, input),
    onSettled: () => {
      void queryClient.invalidateQueries({ queryKey: STORAGE_DEFAULTS_KEY });
      void queryClient.invalidateQueries({ queryKey: storageDefaultKey(defaultId) });
    },
  });
}

export function useSetStorageDefaultEnabled(defaultId: string) {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: (input: SetStorageDefaultEnabledInput) => storageDefaultsApi.setEnabled(defaultId, input),
    onSettled: () => {
      void queryClient.invalidateQueries({ queryKey: STORAGE_DEFAULTS_KEY });
      void queryClient.invalidateQueries({ queryKey: storageDefaultKey(defaultId) });
    },
  });
}

/** Installed provider types are build metadata, so they stay fresh until this deployment changes. */
export function useStorageDefaultProviders() {
  return useQuery({
    queryKey: STORAGE_DEFAULT_PROVIDERS_KEY,
    queryFn: ({ signal }) => storageDefaultProvidersApi.list(signal),
    staleTime: Infinity,
  });
}

/** The classes a template may be authored for, from the deployment catalog rather than the team one. */
export function useStorageDefaultDataClasses() {
  return useQuery({
    queryKey: [...STORAGE_DEFAULT_PROVIDERS_KEY, "data-classes"] as const,
    queryFn: ({ signal }) => storageDefaultProvidersApi.listDataClasses(signal),
    staleTime: Infinity,
  });
}
