import { useMutation, useQueryClient } from "@tanstack/react-query";

import { packsApi } from "@/api/packs";

/**
 * Pack import hooks. Preview + commit are imperative (triggered by Fetch / Import), so they're mutations, not
 * cache-keyed queries. A successful import invalidates the agents library so the new personas appear on return.
 */
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
