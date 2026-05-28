import { createFileRoute } from "@tanstack/react-router";

import { MessagePane } from "@/components/chat/MessagePane";

export const Route = createFileRoute("/_app/teams/$teamSlug/chat/$conversationId")({
  component: ChatConversation,
});

function ChatConversation() {
  const { conversationId } = Route.useParams();
  // Keyed on the id so switching conversations remounts the pane — no stale scroll position
  // or read-cursor effect bleeding from the previous conversation.
  return <MessagePane key={conversationId} conversationId={conversationId} />;
}
