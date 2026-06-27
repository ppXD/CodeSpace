import { keepPreviousData, useMutation, useQuery, useQueryClient } from "@tanstack/react-query";

import { packsApi } from "@/api/packs";

/**
 * Pack data hooks. The Library page reads the team's packs (rail) + a selected pack's artifacts (detail);
 * preview + commit are imperative (triggered by Fetch / Import), so they're mutations, not cache-keyed queries.
 * A successful import invalidates the packs rail + the agents library so both reflect the new artifacts at once.
 */

/** The team's imported packs — the Library's source rail. Not keyed by team id (switching team clears the cache). */
export function usePacks() {
  return useQuery({
    queryKey: ["packs"],
    queryFn: () => packsApi.list(),
  });
}

/**
 * One pack's artifacts — the Library detail pane. Keyed by id; only enabled when a pack is selected.
 * keepPreviousData holds the prior pack's detail while the newly-selected one loads, so switching rail
 * items doesn't flash the whole pane to a loading state.
 */
export function usePack(packId: string | null) {
  return useQuery({
    queryKey: ["pack", packId],
    queryFn: () => packsApi.get(packId!),
    enabled: !!packId,
    placeholderData: keepPreviousData,
  });
}
export function usePreviewPack() {
  return useMutation({
    mutationFn: ({ url, reference }: { url: string; reference: string }) => packsApi.previewFromUrl(url, reference),
  });
}

export function useImportPack() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: ({ url, reference, sourcePaths }: { url: string; reference: string; sourcePaths: string[] }) => packsApi.importFromUrl(url, reference, sourcePaths),
    onSuccess: () => {
      // An import creates/updates a pack AND its artifacts — refresh the packs rail + every pack detail
      // (the just-imported pack is on the Library page the user is looking at) and the agents library.
      queryClient.invalidateQueries({ queryKey: ["packs"] });
      queryClient.invalidateQueries({ queryKey: ["pack"] });
      queryClient.invalidateQueries({ queryKey: ["agents"] });
    },
  });
}
