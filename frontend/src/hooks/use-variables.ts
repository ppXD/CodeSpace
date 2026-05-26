import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";

import { teamVariablesApi, workflowVariablesApi, type SetVariableInput } from "@/api/variables";

/**
 * Hooks for unified variable CRUD. One query key per scope so mutations in one scope
 * don't invalidate the other. Plaintext is NEVER returned for Secret rows on the list
 * path — by backend contract, not by hook configuration.
 */

const TEAM_VARIABLES_KEY = ["team-variables"];

export function useTeamVariables() {
  return useQuery({
    queryKey: TEAM_VARIABLES_KEY,
    queryFn: () => teamVariablesApi.list(),
  });
}

export function useSetTeamVariable() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ name, input }: { name: string; input: SetVariableInput }) =>
      teamVariablesApi.set(name, input),
    onSuccess: () => qc.invalidateQueries({ queryKey: TEAM_VARIABLES_KEY }),
  });
}

export function useDeleteTeamVariable() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (name: string) => teamVariablesApi.delete(name),
    onSuccess: () => qc.invalidateQueries({ queryKey: TEAM_VARIABLES_KEY }),
  });
}

export function useWorkflowVariables(workflowId: string | null) {
  return useQuery({
    queryKey: ["workflow-variables", workflowId],
    queryFn: () => workflowVariablesApi.list(workflowId!),
    enabled: workflowId != null,
  });
}

export function useSetWorkflowVariable(workflowId: string | null) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ name, input }: { name: string; input: SetVariableInput }) =>
      workflowVariablesApi.set(workflowId!, name, input),
    onSuccess: () => qc.invalidateQueries({ queryKey: ["workflow-variables", workflowId] }),
  });
}

export function useDeleteWorkflowVariable(workflowId: string | null) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (name: string) => workflowVariablesApi.delete(workflowId!, name),
    onSuccess: () => qc.invalidateQueries({ queryKey: ["workflow-variables", workflowId] }),
  });
}
