import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";

import { storageAdoptionApi } from "@/api/storageAdoptions";
import { STORAGE_PROFILES_KEY } from "@/hooks/use-storage";
import { refreshStorageRouteQueries } from "@/hooks/use-storage-routes";

export const STORAGE_ADOPTIONS_KEY = ["storage", "adoptions"] as const;

export function useStorageAdoptions() {
  return useQuery({
    queryKey: STORAGE_ADOPTIONS_KEY,
    queryFn: ({ signal }) => storageAdoptionApi.list(signal),
  });
}

/**
 * Adopting creates a profile and a route, so the two lists that render them are refreshed alongside
 * the adoption list itself. Invalidated on EVERY settled outcome, not only a successful one: a call
 * that answered `AlreadyAdopted` or `TeamOwnsRoute` learned that this screen's picture was stale,
 * which is exactly when it most needs to be refetched.
 */
export function useAdoptStorageDefault() {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: (dataClassTypeKey: string) => storageAdoptionApi.adopt(dataClassTypeKey),
    onSettled: () => {
      void queryClient.invalidateQueries({ queryKey: STORAGE_ADOPTIONS_KEY });
      void queryClient.invalidateQueries({ queryKey: STORAGE_PROFILES_KEY });
      refreshStorageRouteQueries(queryClient);
    },
  });
}
