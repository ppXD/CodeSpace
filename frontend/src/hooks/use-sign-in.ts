import { useMutation, useQueryClient } from "@tanstack/react-query";

import { authApi, type SignInRequest } from "@/api/auth";
import { adoptSessionFrom } from "@/api/session";

const ACTIVE_TEAM_STORAGE_KEY = "codespace.activeTeamId";

export function useSignIn() {
  const queryClient = useQueryClient();

  return useMutation({
    // The response set the HttpOnly session cookie; settle on it before the page moves on, so the first request after
    // the redirect is already signed in by it (or by the token, if this API predates the cookie). Settled HERE, so the
    // result react-query keeps never holds the token — and kept no longer than the page that asked: the variables are
    // the password.
    mutationFn: async (input: SignInRequest) => adoptSessionFrom(await authApi.signIn(input)),
    gcTime: 0,
    onSuccess: (response) => {
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
