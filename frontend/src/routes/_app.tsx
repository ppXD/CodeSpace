import { createFileRoute, redirect } from "@tanstack/react-router";

import { isAuthenticated } from "@/api/auth";
import { AppShell } from "@/components/AppShell";
import { ChatDockProvider } from "@/components/chat/ChatDockContext";

import "@/styles/ai-code-space.css";

/**
 * Authenticated-shell layout. The auth gate runs `beforeLoad` so unauthenticated users never
 * see the shell. Everything renders inside <ChatDockProvider> so the persistent chat dock (rail
 * + centre conversation view, in <AppShell>) stays mounted across every route.
 */
export const Route = createFileRoute("/_app")({
  beforeLoad: () => {
    if (!isAuthenticated()) throw redirect({ to: "/signin" });
  },
  component: AppLayout,
});

function AppLayout() {
  return (
    <ChatDockProvider>
      <AppShell />
    </ChatDockProvider>
  );
}
