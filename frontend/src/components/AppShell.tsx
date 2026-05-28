import { Outlet } from "@tanstack/react-router";
import { useState, type CSSProperties } from "react";

import { Ic } from "@/_imported/ai-code-space/icons";
import { Sidebar } from "@/_imported/ai-code-space/sidebar";
import { ChatConversationView } from "@/components/chat/ChatConversationView";
import { useChatDock } from "@/components/chat/ChatDockContext";
import { ChatRail } from "@/components/chat/ChatRail";

/**
 * Authenticated shell chrome: the sidebar, the routed page (content column), and chat — the
 * persistent right rail plus the conversation panel, which is its own resizable column BETWEEN
 * the content and the rail (so it sits beside your code, not over it). Rendered inside
 * <ChatDockProvider> (see _app.tsx) so it can size the grid + show the floating reopen button.
 *
 * Grid columns: sidebar | content (minmax 0 1fr, shrinks) | conversation (--chat-conv-w, 0 when
 * no conversation is open) | rail (--chat-dock-w, 0 when chat is closed).
 */
export function AppShell() {
  const [sidebarCollapsed, setSidebarCollapsed] = useState(false);
  const { isOpen: chatOpen, open: openChat, activeConversationId, conversationWidth } = useChatDock();

  const convOpen = chatOpen && activeConversationId != null;
  const gridVars = { "--chat-conv-w": convOpen ? `${conversationWidth}px` : "0px" } as CSSProperties;

  return (
    <div className="acs-root">
      <div
        className="app"
        data-sidebar={sidebarCollapsed ? "collapsed" : "expanded"}
        data-chat-open={chatOpen}
        data-density="regular"
        style={gridVars}
      >
        <Sidebar />

        <div className="content-col">
          <Outlet />
        </div>

        <ChatConversationView />

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
