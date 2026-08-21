import { useQuery } from "@tanstack/react-query";

import { workflowRunViewMetadataApi } from "@/api/workflowRunViewMetadataApi";
import { isRunActive } from "@/hooks/use-workflows";

export const WORKFLOW_RUN_VIEW_METADATA_POLL_MS = 2000;

/** Bounded body-blind canvas metadata. Disabled surfaces issue no request. */
export function useWorkflowRunViewMetadata(runId: string | null, enabled = true) {
  return useQuery({
    queryKey: ["workflow-run-view-metadata", runId, "LineageMerged"],
    queryFn: ({ signal }) => workflowRunViewMetadataApi.read(runId!, "LineageMerged", signal),
    enabled: enabled && runId !== null,
    refetchInterval: (query) => {
      const view = query.state.data;
      return view && isRunActive(view.status) ? WORKFLOW_RUN_VIEW_METADATA_POLL_MS : false;
    },
  });
}
