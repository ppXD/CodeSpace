import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";

import {
  projectsApi,
  projectVariablesApi,
  type CreateProjectInput,
  type UpdateProjectInput,
} from "@/api/projects";
import type { SetVariableInput } from "@/api/variables";

const PROJECTS_KEY = ["projects"];

const projectKey = (projectId: string) => ["projects", projectId];

const projectVariablesKey = (projectId: string) => ["project-variables", projectId];

export function useProjects() {
  return useQuery({
    queryKey: PROJECTS_KEY,
    queryFn: () => projectsApi.list(),
  });
}

export function useProject(projectId: string | null) {
  return useQuery({
    queryKey: projectKey(projectId ?? ""),
    queryFn: () => projectsApi.get(projectId!),
    enabled: projectId != null,
  });
}

export function useCreateProject() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (input: CreateProjectInput) => projectsApi.create(input),
    onSuccess: () => qc.invalidateQueries({ queryKey: PROJECTS_KEY }),
  });
}

export function useUpdateProject(projectId: string) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (input: UpdateProjectInput) => projectsApi.update(projectId, input),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: PROJECTS_KEY });
      qc.invalidateQueries({ queryKey: projectKey(projectId) });
    },
  });
}

export function useDeleteProject() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (projectId: string) => projectsApi.delete(projectId),
    onSuccess: () => qc.invalidateQueries({ queryKey: PROJECTS_KEY }),
  });
}

export function useProjectVariables(projectId: string | null) {
  return useQuery({
    queryKey: projectVariablesKey(projectId ?? ""),
    queryFn: () => projectVariablesApi.list(projectId!),
    enabled: projectId != null,
  });
}

export function useSetProjectVariable(projectId: string | null) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ name, input }: { name: string; input: SetVariableInput }) =>
      projectVariablesApi.set(projectId!, name, input),
    onSuccess: () => qc.invalidateQueries({ queryKey: projectVariablesKey(projectId ?? "") }),
  });
}

export function useDeleteProjectVariable(projectId: string | null) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (name: string) => projectVariablesApi.delete(projectId!, name),
    onSuccess: () => qc.invalidateQueries({ queryKey: projectVariablesKey(projectId ?? "") }),
  });
}
