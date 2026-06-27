import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";

import { packsApi } from "@/api/packs";

/**
 * Pack data hooks. The Library page reads the team's packs (rail) + a selected pack's artifacts (detail);
 * preview + commit are imperative (triggered by Fetch / Import), so they're mutations, not cache-keyed queries.
 * A successful import invalidates the agents library so the new personas appear on return.
 */

/** The team's imported packs — the Library's source rail. Not keyed by team id (switching team clears the cache). */
export function usePacks() {
  return useQuery({
    queryKey: ["packs"],
    queryFn: () => packsApi.list(),
  });
}

/** One pack's artifacts — the Library detail pane. Keyed by id; only enabled when a pack is selected. */
export function usePack(packId: string | null) {
  return useQuery({
    queryKey: ["pack", packId],
    queryFn: () => packsApi.get(packId!),
    enabled: !!packId,
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
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ["agents"] }),
  });
}
