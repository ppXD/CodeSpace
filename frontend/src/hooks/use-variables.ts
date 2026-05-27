import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";

import {
  projectVariablesApi,
  teamVariablesApi,
  workflowVariablesApi,
  type SetVariableInput,
} from "@/api/variables";

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

export function useProjectVariables(projectId: string | null) {
  return useQuery({
    queryKey: ["project-variables", projectId],
    queryFn: () => projectVariablesApi.list(projectId!),
    enabled: projectId != null,
  });
}

export function useSetProjectVariable(projectId: string | null) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ name, input }: { name: string; input: SetVariableInput }) =>
      projectVariablesApi.set(projectId!, name, input),
    // Three keys carry data that depends on a project's variable count, all
    // need refresh:
    //   1. ["project-variables", id] — the row list the panel renders
    //   2. ["projects"]              — Projects table column `activeVariableCount`
    //   3. ["project", id]           — Project detail page's tab badge reads
    //                                  `project.activeVariableCount` from this
    //                                  singular query (separate cache entry
    //                                  from the list).
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ["project-variables", projectId] });
      qc.invalidateQueries({ queryKey: ["projects"] });
      qc.invalidateQueries({ queryKey: ["project", projectId] });
    },
  });
}

export function useDeleteProjectVariable(projectId: string | null) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (name: string) => projectVariablesApi.delete(projectId!, name),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ["project-variables", projectId] });
      qc.invalidateQueries({ queryKey: ["projects"] });
      qc.invalidateQueries({ queryKey: ["project", projectId] });
    },
  });
}
