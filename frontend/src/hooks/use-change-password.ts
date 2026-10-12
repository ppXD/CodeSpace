import { useMutation, useQueryClient } from "@tanstack/react-query";

import { authApi, type ChangePasswordRequest } from "@/api/auth";
import { adoptSessionFrom } from "@/api/session";

export function useChangePassword() {
  const queryClient = useQueryClient();

  return useMutation({
    // Changing a password rotates the account's security stamp, which kills every token minted
    // before it — INCLUDING the one this request was made with. The server hands back a session under
    // the new stamp for exactly this (as the replacement cookie, and as the token to a bearer caller);
    // without settling on it, the next request 401s and the person who just did the right thing is
    // signed out for it. Settled inside mutationFn so react-query's kept result never holds the token,
    // and dropped with the page (gcTime 0), because the variables are both passwords.
    mutationFn: async (input: ChangePasswordRequest) => adoptSessionFrom(await authApi.changePassword(input)),
    gcTime: 0,
    onSuccess: (response) => {
      // Refresh the /me cache so the rotation flag clears immediately — without this,
      // the shell's existing query data still says passwordMustChange=true and would
      // re-redirect on the next render.
      queryClient.setQueryData(["me"], response.user);
    },
  });
}
