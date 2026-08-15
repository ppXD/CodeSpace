import { useQuery } from "@tanstack/react-query";

import { storageApi } from "@/api/storage";

export const STORAGE_PROVIDER_MODULES_KEY = ["storage", "provider-modules"] as const;

/** Installed provider types are build metadata, so they remain fresh until this deployment changes. */
export function useStorageProviderModules() {
  return useQuery({
    queryKey: STORAGE_PROVIDER_MODULES_KEY,
    queryFn: () => storageApi.listProviderModules(),
    staleTime: Infinity,
  });
}
