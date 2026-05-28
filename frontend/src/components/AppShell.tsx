import { Outlet } from "@tanstack/react-router";
import { useState } from "react";

import { Ic } from "@/_imported/ai-code-space/icons";
import { Sidebar } from "@/_imported/ai-code-space/sidebar";
import { ChatConversationView } from "@/components/chat/ChatConversationView";
import { useChatDock } from "@/components/chat/ChatDockContext";
import { ChatRail } from "@/components/chat/ChatRail";

/**
 * Authenticated shell chrome: the sidebar, the routed page (in the content column), and chat —
 * the persistent right rail plus the conversation view that overlays the content on demand.
 * Rendered inside <ChatDockProvider> (see _app.tsx) so it can read the dock's open state to size
 * the grid + show the floating reopen button.
 */
export function AppShell() {
  const [sidebarCollapsed, setSidebarCollapsed] = useState(false);
  const { isOpen: chatOpen, open: openChat } = useChatDock();

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

        {/* When the chat rail is closed it collapses to this floating icon pinned top-right —
            the single entry point to reopen it (the rail's own header › closes it). */}
        {!chatOpen && (
          <div className="float-chrome float-r">
            <button className="chrome-btn" onClick={openChat} title="Open chat">
              <Ic.Chat size={14} />
            </button>
          </div>
        )}
      </div>
    </div>
  );
}
