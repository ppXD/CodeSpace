import { Outlet, createFileRoute, redirect } from "@tanstack/react-router";
import { useState } from "react";

import { isAuthenticated } from "@/api/auth";
import { Ic } from "@/_imported/ai-code-space/icons";
import { Sidebar } from "@/_imported/ai-code-space/sidebar";
import { ChatConversationView } from "@/components/chat/ChatConversationView";
import { ChatDockProvider, useChatDock } from "@/components/chat/ChatDockContext";
import { ChatRail } from "@/components/chat/ChatRail";

import "@/styles/ai-code-space.css";

/**
 * Authenticated-shell layout. Sits above every URL the operator browses through so the sidebar
 * + collapse chrome stay mounted across navigations.
 *
 * Chat lives here, not in a route, so it's always available:
 *   - <ChatRail> is the persistent right column (Home / Channels / Members tabs);
 *   - <ChatConversationView> opens the selected conversation OVER the centre content (the page
 *     stays mounted underneath, so closing is instant and never a navigation).
 *
 * Auth gate runs `beforeLoad` so unauthenticated users never see this shell.
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

function AppShell() {
  const [sidebarCollapsed, setSidebarCollapsed] = useState(false);
  const { isOpen: chatOpen } = useChatDock();

  return (
    <div className="acs-root">
      <div
        className="app"
        data-sidebar={sidebarCollapsed ? "collapsed" : "expanded"}
        data-chat-open={chatOpen}
        data-density="regular"
      >
        <Sidebar />

        <div className="content-col">
          <Outlet />
          <ChatConversationView />
        </div>

        <ChatRail />

        <div className="float-chrome float-l">
          <button
            className="chrome-btn"
            onClick={() => setSidebarCollapsed(c => !c)}
            title={sidebarCollapsed ? "Expand sidebar" : "Collapse sidebar"}
          >
            {sidebarCollapsed ? <Ic.ChevronRight size={14} /> : <Ic.ChevronLeft size={14} />}
          </button>
        </div>
      </div>
    </div>
  );
}
