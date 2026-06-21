import { useMutation } from "@tanstack/react-query";

import { tasksApi, type LaunchTaskInput } from "@/api/tasks";

/**
 * Launch a task via `POST /api/tasks`. Returns the started run's id so the caller can navigate
 * to its phase tree. Stateless mutation — every surface (Overview launchpad, PR/issue/repo
 * headers, a global "New task") reuses it through the one `LaunchTaskModal`.
 */
export function useLaunchTask() {
  return useMutation({
    mutationFn: (input: LaunchTaskInput) => tasksApi.launch(input),
  });
}
