import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";

import { repositoriesApi } from "@/api/repositories";

/**
 * The Webhook tab's reads and writes.
 *
 * <p>The list refetches on an interval because the thing it reports is moving without the user: a
 * retry lands as Pending and the dispatcher takes it to Enqueued a moment later, and the first
 * delivery flips the row to Delivering while the operator is still on the provider's screen pressing
 * Test. Step 5 of the manual instructions promises exactly that, so the page has to be watching.</p>
 */
const WEBHOOK_POLL_MS = 10_000;

export function useRepositoryWebhooks(repositoryId: string | null) {
  return useQuery({
    queryKey: ["repository", repositoryId, "webhooks"],
    queryFn: () => repositoriesApi.listWebhooks(repositoryId!),
    enabled: repositoryId != null,
    refetchInterval: WEBHOOK_POLL_MS,
  });
}

/**
 * Reveal is a mutation, not a query, so the secret is never in the query cache and never refetched
 * behind the operator's back — that is the whole reason the endpoint is separate.
 *
 * `gcTime: 0` is the other half, and it is not decoration. React Query keeps a FINISHED mutation's
 * `state.data` in its MutationCache for the default five minutes after the last observer unmounts, so
 * without it the plaintext outlives the panel that asked for it and sits in devtools — a copy nobody
 * asked to keep, of the one value that can forge a delivery this repository will accept.
 */
export function useRevealWebhookSecret(repositoryId: string | null) {
  return useMutation({ mutationFn: (webhookId: string) => repositoriesApi.revealWebhookSecret(repositoryId!, webhookId), gcTime: 0 });
}

export function useRetryWebhookRegistration(repositoryId: string | null) {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: (webhookId: string) => repositoriesApi.retryWebhookRegistration(repositoryId!, webhookId),
    // The response is the row as it stands mid-transaction (Pending, attempts 0); the dispatcher moves
    // it on immediately after the commit. Invalidating rather than writing the response into the cache
    // is what stops the row sitting at "Pending" until the next poll.
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ["repository", repositoryId, "webhooks"] }),
  });
}
