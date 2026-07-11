import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";

import { projectsApi } from "@/api/projects";
import type { CreateProjectInput, UpdateProjectInput } from "@/api/types";

/**
 * Hooks for project CRUD. The list is the most common surface — the sidebar's primary
 * row navigates to it; the project-detail page reads a single id; create/update/delete
 * invalidate the list so cards update without a page refresh.
 */

const PROJECTS_KEY = ["projects"];

export function useProjects() {
  return useQuery({ queryKey: PROJECTS_KEY, queryFn: () => projectsApi.list() });
}

/** Read one project by ref — its GUID (legacy link) or team-unique slug (canonical clean URL). */
export function useProject(ref: string | undefined) {
  return useQuery({
    queryKey: ["project", ref],
    queryFn: () => projectsApi.get(ref!),
    enabled: !!ref,
  });
}

export function useCreateProject() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (input: CreateProjectInput) => projectsApi.create(input),
    onSuccess: () => qc.invalidateQueries({ queryKey: PROJECTS_KEY }),
  });
}

export function useUpdateProject() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ projectId, input }: { projectId: string; input: UpdateProjectInput }) =>
      projectsApi.update(projectId, input),
    onSuccess: (_d, vars) => {
      qc.invalidateQueries({ queryKey: PROJECTS_KEY });
      qc.invalidateQueries({ queryKey: ["project", vars.projectId] });
    },
  });
}

export function useDeleteProject() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (projectId: string) => projectsApi.remove(projectId),
    onSuccess: () => qc.invalidateQueries({ queryKey: PROJECTS_KEY }),
  });
}
