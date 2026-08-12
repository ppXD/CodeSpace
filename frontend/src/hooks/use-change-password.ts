import { useMutation, useQueryClient } from "@tanstack/react-query";

import { authApi, storeJwt, type ChangePasswordRequest } from "@/api/auth";

export function useChangePassword() {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: (input: ChangePasswordRequest) => authApi.changePassword(input),
    onSuccess: (response) => {
      // Changing a password rotates the account's security stamp, which kills every token minted
      // before it — INCLUDING the one this request was made with. The server hands back a token under
      // the new stamp for exactly this; without storing it, the next request 401s and the person who
      // just did the right thing is signed out for it.
      storeJwt(response.token);

      // Refresh the /me cache so the rotation flag clears immediately — without this,
      // the shell's existing query data still says passwordMustChange=true and would
      // re-redirect on the next render.
      queryClient.setQueryData(["me"], response.user);
    },
  });
}
