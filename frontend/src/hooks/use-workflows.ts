import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";

import { workflowsApi, type CreateWorkflowInput, type UpdateWorkflowInput } from "@/api/workflows";

/**
 * Hooks for the workflows engine surface. Same shape as the repository hooks —
 * list/get queries auto-invalidate on mutation, run-status polls every 4 s while a
 * run is non-terminal.
 */

export function useWorkflows() {
  return useQuery({
    queryKey: ["workflows"],
    queryFn: () => workflowsApi.list(),
  });
}

export function useWorkflow(workflowId: string | null) {
  return useQuery({
    queryKey: ["workflow", workflowId],
    queryFn: () => workflowsApi.get(workflowId!),
    enabled: workflowId != null,
  });
}

export function useCreateWorkflow() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (input: CreateWorkflowInput) => workflowsApi.create(input),
    onSuccess: () => qc.invalidateQueries({ queryKey: ["workflows"] }),
  });
}

export function useUpdateWorkflow() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ workflowId, input }: { workflowId: string; input: UpdateWorkflowInput }) =>
      workflowsApi.update(workflowId, input),
    onSuccess: (_, { workflowId }) => {
      qc.invalidateQueries({ queryKey: ["workflows"] });
      qc.invalidateQueries({ queryKey: ["workflow", workflowId] });
    },
  });
}

export function useDeleteWorkflow() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (workflowId: string) => workflowsApi.delete(workflowId),
    onSuccess: () => qc.invalidateQueries({ queryKey: ["workflows"] }),
  });
}

export function useSetWorkflowEnabled() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ workflowId, enabled }: { workflowId: string; enabled: boolean }) =>
      workflowsApi.setEnabled(workflowId, enabled),
    onSuccess: (_, { workflowId }) => {
      qc.invalidateQueries({ queryKey: ["workflows"] });
      qc.invalidateQueries({ queryKey: ["workflow", workflowId] });
    },
  });
}

export function useRunWorkflowManually() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ workflowId, payload }: { workflowId: string; payload?: unknown }) =>
      workflowsApi.runManually(workflowId, payload),
    onSuccess: (_, { workflowId }) => {
      qc.invalidateQueries({ queryKey: ["workflow-runs", workflowId] });
    },
  });
}

export function useWorkflowRuns(workflowId: string | null) {
  return useQuery({
    queryKey: ["workflow-runs", workflowId],
    queryFn: () => workflowsApi.listRuns(workflowId!),
    enabled: workflowId != null,
    // Auto-poll while at least one run is non-terminal — the dispatcher might still be
    // walking it. 4 s feels responsive without hammering. Stops as soon as everything is Success/Failure.
    refetchInterval: (q) => {
      const data = q.state.data;
      if (!data) return false;
      const anyActive = data.some((r) => r.status === "Pending" || r.status === "Running");
      return anyActive ? 4000 : false;
    },
  });
}

export function useWorkflowRun(runId: string | null) {
  return useQuery({
    queryKey: ["workflow-run", runId],
    queryFn: () => workflowsApi.getRun(runId!),
    enabled: runId != null,
    refetchInterval: (q) => {
      const data = q.state.data;
      if (!data) return false;
      return data.status === "Pending" || data.status === "Running" ? 2000 : false;
    },
  });
}

export function useReplayRun() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (runId: string) => workflowsApi.replayRun(runId),
    onSuccess: (_, originalRunId) => {
      // The original run hasn't changed, but its workflow's run list has a new entry.
      // We don't know the workflowId here without an extra query, so invalidate broadly.
      qc.invalidateQueries({ queryKey: ["workflow-runs"] });
      qc.invalidateQueries({ queryKey: ["workflow-run", originalRunId] });
    },
  });
}

export function useNodeManifests() {
  return useQuery({
    queryKey: ["node-manifests"],
    queryFn: () => workflowsApi.listNodeManifests(),
    staleTime: Infinity,   // Manifests don't change while the page is open.
  });
}

/**
 * Engine-injected sys.* variables (key + type + one-line description). Static per release,
 * so we cache with `staleTime: Infinity` — same lifetime model as node manifests. Replaces
 * the old hardcoded SYSTEM_VARIABLES constant; the canonical list now lives on the backend
 * (SystemScopeKeys.Descriptors) and adding a new key requires no frontend change.
 */
export function useSystemVariables() {
  return useQuery({
    queryKey: ["system-variables"],
    queryFn: () => workflowsApi.listSystemVariables(),
    staleTime: Infinity,
  });
}
