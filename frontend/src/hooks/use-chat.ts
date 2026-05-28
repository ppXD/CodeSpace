import { useInfiniteQuery, useMutation, useQuery, useQueryClient } from "@tanstack/react-query";

import { chatApi } from "@/api/chat";

/**
 * Chat data hooks. Conversation list + a cursor-paginated message feed (newest-first pages;
 * "fetch next page" walks BACKWARD into history). Queries aren't keyed by team id — switching
 * team invalidates the whole cache (see useActiveTeam), so the X-Team-Id header change is enough.
 */

export function useConversations() {
  return useQuery({
    queryKey: ["chat-conversations"],
    queryFn: () => chatApi.listConversations(),
  });
}

export function useConversation(conversationId: string | null) {
  return useQuery({
    queryKey: ["chat-conversation", conversationId],
    queryFn: () => chatApi.getConversation(conversationId!),
    enabled: conversationId != null,
  });
}

export function useMessages(conversationId: string | null) {
  return useInfiniteQuery({
    queryKey: ["chat-messages", conversationId],
    queryFn: ({ pageParam }) => chatApi.listMessages(conversationId!, pageParam),
    initialPageParam: null as string | null,
    // "Next" page = the next OLDER slice. Stop when the server says there's no more history.
    getNextPageParam: (lastPage) => (lastPage.hasMore ? lastPage.nextBeforeId : undefined),
    enabled: conversationId != null,
  });
}

export function usePostMessage(conversationId: string) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (body: string) => chatApi.postMessage(conversationId, body),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ["chat-messages", conversationId] });
      qc.invalidateQueries({ queryKey: ["chat-conversations"] });
    },
  });
}

export function useMarkRead(conversationId: string) {
  return useMutation({
    mutationFn: (lastReadMessageId: string) => chatApi.markRead(conversationId, lastReadMessageId),
  });
}

export function useCreateChannel() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ name, slug }: { name: string; slug: string }) => chatApi.createChannel(name, slug),
    onSuccess: () => qc.invalidateQueries({ queryKey: ["chat-conversations"] }),
  });
}
