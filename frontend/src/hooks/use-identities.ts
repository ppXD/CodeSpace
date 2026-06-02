import { useMutation } from "@tanstack/react-query";

import { identitiesApi } from "@/api/identities";

/**
 * Link the caller's own provider identity by PAT (Model B). The only identity hook left after the
 * standalone "Connected identities" page was removed — linking now happens reactively via the
 * ActorIdentityGate's modal (on a 428), and disconnect goes through Connect-remote → Personal
 * (revoking the credential cascades to the identity).
 */
export function useLinkIdentityByPat() {
  return useMutation({
    mutationFn: (input: { providerInstanceId: string; accessToken: string }) => identitiesApi.linkByPat(input),
  });
}
