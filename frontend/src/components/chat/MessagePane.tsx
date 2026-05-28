import { Fragment, useEffect, useMemo, useRef, useState } from "react";

import { ApiError } from "@/api/request";
import { useConversation, useMarkRead, useMessages, usePostMessage } from "@/hooks/use-chat";
import { useMe } from "@/hooks/use-me";
import { useTeamMemberMap } from "@/hooks/use-team-members";
import { dayDividerLabel, firstUnreadId, isNewDay } from "@/lib/messageDividers";

import { ConversationMembers } from "./ConversationMembers";
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

  // Capture the read cursor the first time this conversation's summary loads — before the markRead
  // effect below (or a later window-focus refetch) advances it — so the "new messages" divider
  // stays anchored for the whole visit, the way Slack/Space keep it visible until you leave. This
  // is the render-phase "store info from previous renders" pattern: the guard makes it converge,
  // and a conversation switch reads through as null (no stale anchor) until the new summary loads.
  const [captured, setCaptured] = useState<{ id: string; cursor: string | null }>({ id: "", cursor: null });
  if (conversation.data && captured.id !== conversationId) {
    setCaptured({ id: conversationId, cursor: conversation.data.lastReadMessageId });
  }
  const capturedCursor = captured.id === conversationId ? captured.cursor : null;

  const unreadAnchorId = useMemo(
    () => firstUnreadId(messages, capturedCursor, me.data?.id ?? null),
    [messages, capturedCursor, me.data?.id],
  );

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
            <ConversationMembers conversationId={conversationId} memberUserIds={conversation.data.memberUserIds} />
          )}
        </div>
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

        {messages.map((m, i) => {
          const showDayDivider = i === 0 || isNewDay(messages[i - 1].createdDate, m.createdDate);

          return (
            <Fragment key={m.id}>
              {showDayDivider && (
                <div className="chat-divider" role="separator">
                  <span>{dayDividerLabel(m.createdDate)}</span>
                </div>
              )}
              {m.id === unreadAnchorId && (
                <div className="chat-divider chat-divider-unread" role="separator">
                  <span>New messages</span>
                </div>
              )}
              <MessageItem message={m} members={members} isMine={m.authorUserId === me.data?.id} />
            </Fragment>
          );
        })}

        <div ref={bottomRef} />
      </div>

      <MessageComposer onSend={(body) => post.mutate(body)} disabled={post.isPending} placeholder={`Message ${title}`} />
    </div>
  );
}
