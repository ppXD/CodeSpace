import { createFileRoute } from "@tanstack/react-router";

export const Route = createFileRoute("/_app/teams/$teamSlug/chat/")({
  component: ChatIndex,
});

function ChatIndex() {
  return (
    <div className="chat-main chat-main-placeholder">
      <div className="chat-empty">Select a conversation, or create a channel to start.</div>
    </div>
  );
}
