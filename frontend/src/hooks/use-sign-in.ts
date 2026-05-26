import { useMutation, useQueryClient } from "@tanstack/react-query";

import { authApi, storeJwt, type SignInRequest } from "@/api/auth";

const ACTIVE_TEAM_STORAGE_KEY = "codespace.activeTeamId";

export function useSignIn() {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: (input: SignInRequest) => authApi.signIn(input),
    onSuccess: (response) => {
      storeJwt(response.token);

      // Seed the active team so the very first request after redirect has the right
      // X-Team-Id header. The user can switch later via the sidebar. Skip this when
      // the user must rotate — they can't load anything team-scoped until they do.
      if (!response.user.passwordMustChange && response.user.teams.length > 0) {
        const stored = localStorage.getItem(ACTIVE_TEAM_STORAGE_KEY);
        const stillValid = stored && response.user.teams.some(t => t.id === stored);
        if (!stillValid) localStorage.setItem(ACTIVE_TEAM_STORAGE_KEY, response.user.teams[0].id);
      }

      // Prime the cache so the SPA shell doesn't fire a redundant /me on first paint.
      queryClient.setQueryData(["me"], response.user);
    },
  });
}
