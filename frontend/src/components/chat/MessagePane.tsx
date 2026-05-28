import { useEffect, useMemo, useRef } from "react";

import { ApiError } from "@/api/request";
import { useConversation, useMarkRead, useMessages, usePostMessage } from "@/hooks/use-chat";
import { useMe } from "@/hooks/use-me";
import { useTeamMemberMap } from "@/hooks/use-team-members";

import { ChannelInvite } from "./ChannelInvite";
import { conversationTitle } from "./conversationTitle";
import { MessageComposer } from "./MessageComposer";
import { MessageItem } from "./MessageItem";

/**
 * The right pane: header + scrollable history + composer for one conversation. Message pages
 * arrive newest-first; we flatten + reverse to render oldest-at-top, and "Load older" walks
 * further back via the keyset cursor. Viewing the pane advances the read cursor to the newest
 * message; posting refetches so the new message lands at the bottom.
 */
export function MessagePane({ conversationId }: { conversationId: string }) {
  const conversation = useConversation(conversationId);
  const messagesQuery = useMessages(conversationId);
  const post = usePostMessage(conversationId);
  const markRead = useMarkRead(conversationId);
  const members = useTeamMemberMap();
  const me = useMe();

  const messages = useMemo(
    () => (messagesQuery.data?.pages.flatMap(p => p.messages) ?? []).slice().reverse(),
    [messagesQuery.data],
  );

  const newestId = messages.length > 0 ? messages[messages.length - 1].id : null;
  const title = conversation.data ? conversationTitle(conversation.data, members, me.data?.id ?? null) : "…";

  // Advance the read cursor to the newest message whenever it changes (open + new arrivals).
  const markReadMutate = markRead.mutate;
  useEffect(() => {
    if (newestId) markReadMutate(newestId);
  }, [newestId, markReadMutate]);

  // Keep the latest message in view on load + after posting. Optional-chained so the
  // happy-dom test environment (no layout engine) doesn't choke on scrollIntoView.
  const bottomRef = useRef<HTMLDivElement | null>(null);
  useEffect(() => {
    bottomRef.current?.scrollIntoView?.({ block: "end" });
  }, [newestId]);

  return (
    <div className="chat-main">
      <div className="chat-main-head">
        <div className="chat-main-head-info">
          <span className="chat-main-title">{title}</span>
          {conversation.data && conversation.data.kind !== "Direct" && (
            <span className="chat-main-sub">{conversation.data.memberCount} members</span>
          )}
        </div>
        {conversation.data && conversation.data.kind !== "Direct" && (
          <ChannelInvite conversationId={conversationId} memberUserIds={conversation.data.memberUserIds} />
        )}
      </div>

      <div className="chat-msgs">
        {messagesQuery.hasNextPage && (
          <div className="chat-loadmore">
            <button className="btn btn-ghost" onClick={() => messagesQuery.fetchNextPage()} disabled={messagesQuery.isFetchingNextPage}>
              {messagesQuery.isFetchingNextPage ? "Loading…" : "Load older messages"}
            </button>
          </div>
        )}

        {messagesQuery.isLoading && <div className="chat-empty">Loading…</div>}

        {messagesQuery.error instanceof ApiError && (
          <div className="chat-empty">Couldn't load messages: {messagesQuery.error.message}</div>
        )}

        {!messagesQuery.isLoading && !messagesQuery.error && messages.length === 0 && (
          <div className="chat-empty">No messages yet. Say hello.</div>
        )}

        {messages.map(m => (
          <MessageItem key={m.id} message={m} members={members} isMine={m.authorUserId === me.data?.id} />
        ))}

        <div ref={bottomRef} />
      </div>

      <MessageComposer onSend={(body) => post.mutate(body)} disabled={post.isPending} placeholder={`Message ${title}`} />
    </div>
  );
}
