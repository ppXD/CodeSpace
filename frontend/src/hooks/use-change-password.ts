import { useMutation, useQueryClient } from "@tanstack/react-query";

import { authApi, type ChangePasswordRequest } from "@/api/auth";

export function useChangePassword() {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: (input: ChangePasswordRequest) => authApi.changePassword(input),
    onSuccess: (response) => {
      // Refresh the /me cache so the rotation flag clears immediately — without this,
      // the shell's existing query data still says passwordMustChange=true and would
      // re-redirect on the next render.
      queryClient.setQueryData(["me"], response.user);
    },
  });
}
