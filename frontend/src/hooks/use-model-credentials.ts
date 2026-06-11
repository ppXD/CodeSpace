import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";

import { modelCredentialsApi, type AddModelCredentialInput, type UpdateModelCredentialInput } from "@/api/modelCredentials";

const MODEL_CREDENTIALS_KEY = ["model-credentials"] as const;

/** The team's model credentials (secret-free summaries). Optional provider filter; the broader key prefix
 *  means every mutation below refreshes both the filtered and unfiltered views. */
export function useModelCredentials(provider?: string) {
  return useQuery({
    queryKey: provider ? [...MODEL_CREDENTIALS_KEY, provider] : MODEL_CREDENTIALS_KEY,
    queryFn: () => modelCredentialsApi.list(provider),
  });
}

export function useAddModelCredential() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (input: AddModelCredentialInput) => modelCredentialsApi.add(input),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: MODEL_CREDENTIALS_KEY }),
  });
}

export function useUpdateModelCredential() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: ({ id, input }: { id: string; input: UpdateModelCredentialInput }) => modelCredentialsApi.update(id, input),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: MODEL_CREDENTIALS_KEY }),
  });
}

export function useRevokeModelCredential() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (id: string) => modelCredentialsApi.revoke(id),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: MODEL_CREDENTIALS_KEY }),
  });
}
