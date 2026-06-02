import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";

import { identitiesApi } from "@/api/identities";

/** Query key for the caller's own linked provider identities (Model B). */
const MY_IDENTITIES = ["my-identities"] as const;

export function useMyProviderIdentities() {
  return useQuery({ queryKey: MY_IDENTITIES, queryFn: () => identitiesApi.listMine() });
}

export function useLinkIdentityByPat() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (input: { providerInstanceId: string; accessToken: string }) => identitiesApi.linkByPat(input),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: MY_IDENTITIES }),
  });
}

export function useUnlinkIdentity() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (identityId: string) => identitiesApi.unlink(identityId),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: MY_IDENTITIES }),
  });
}
