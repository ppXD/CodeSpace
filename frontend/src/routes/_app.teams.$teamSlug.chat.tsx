import { Outlet, createFileRoute, useParams } from "@tanstack/react-router";

import { ConversationList } from "@/components/chat/ConversationList";

/**
 * Chat layout — the conversation rail stays mounted while the right pane swaps per
 * conversation (URL-driven, so each conversation is deep-linkable). The active id is read
 * loosely from the child route params for the rail's highlight.
 */
export const Route = createFileRoute("/_app/teams/$teamSlug/chat")({
  component: ChatLayout,
});

function ChatLayout() {
  const { teamSlug } = Route.useParams();
  const childParams = useParams({ strict: false }) as { conversationId?: string };

  return (
    <div className="chat">
      <ConversationList teamSlug={teamSlug} activeConversationId={childParams.conversationId ?? null} />
      <Outlet />
    </div>
  );
}
