import { useState } from "react";

import { Ic } from "@/_imported/ai-code-space/icons";
import { useCreateChannel } from "@/hooks/use-chat";

import { useChatDock } from "./ChatDockContext";
import { ChatMemberList } from "./ChatMemberList";
import { ConversationList } from "./ConversationList";

/**
 * The persistent right rail — your always-available entry into chat. A "+" in the header creates
 * a channel (an inline form with Add + Cancel, so it's escapable). Tabs (Space-style): Home (all
 * recent conversations), Channels (channels only), Members (the roster → click opens a DM).
 * Picking anything opens it in the centre panel. Renders nothing while the dock is closed.
 */
type ChatTab = "home" | "channels" | "members";
const TAB_KEY = "codespace.chatRail.tab";

export function ChatRail() {
  const { isOpen, activeConversationId, setActiveConversationId, close } = useChatDock();
  const create = useCreateChannel();
  const [tab, setTabState] = useState<ChatTab>(() => (localStorage.getItem(TAB_KEY) as ChatTab) || "home");
  const [creating, setCreating] = useState(false);
  const [name, setName] = useState("");

  if (!isOpen) return null;

  const setTab = (next: ChatTab) => {
    setTabState(next);
    localStorage.setItem(TAB_KEY, next);
  };

  const cancelCreate = () => {
    setCreating(false);
    setName("");
  };

  const submitCreate = async () => {
    const trimmed = name.trim();
    if (!trimmed || create.isPending) return;

    const created = await create.mutateAsync({ name: trimmed, slug: trimmed });
    cancelCreate();
    setActiveConversationId(created.id);   // open the channel you just made
  };

  return (
    <aside className="chat-rail" aria-label="Chats">
      <div className="chat-rail-head">
        <span className="chat-rail-title">Chats</span>
        <div className="chat-rail-head-actions">
          <button className="chrome-btn" title="New channel" aria-label="New channel" data-active={creating} onClick={() => setCreating(c => !c)}>
            <Ic.Plus size={15} />
          </button>
          <button className="chrome-btn" title="Close chat" aria-label="Close chat" onClick={close}>
            <Ic.ChevronRight size={14} />
          </button>
        </div>
      </div>

      <div className="chat-rail-tabs" role="tablist">
        <button className="chat-rail-tab" role="tab" aria-selected={tab === "home"} data-active={tab === "home"} title="Home" onClick={() => setTab("home")}>
          <Ic.Home size={16} />
        </button>
        <button className="chat-rail-tab" role="tab" aria-selected={tab === "channels"} data-active={tab === "channels"} title="Channels" onClick={() => setTab("channels")}>
          <span className="chat-rail-hash">#</span>
        </button>
        <button className="chat-rail-tab" role="tab" aria-selected={tab === "members"} data-active={tab === "members"} title="Members" onClick={() => setTab("members")}>
          <Ic.Users size={16} />
        </button>
      </div>

      {creating && (
        <div className="chat-create">
          <input
            className="chat-create-input"
            value={name}
            autoFocus
            placeholder="New channel name"
            onChange={(e) => setName(e.target.value)}
            onKeyDown={(e) => {
              if (e.key === "Enter") submitCreate();
              if (e.key === "Escape") cancelCreate();
            }}
          />
          <button className="btn btn-primary" onClick={submitCreate} disabled={create.isPending || name.trim().length === 0}>Add</button>
          <button className="btn btn-ghost" onClick={cancelCreate}>Cancel</button>
        </div>
      )}

      <div className="chat-rail-body">
        {tab === "home" && <ConversationList activeConversationId={activeConversationId} onSelect={setActiveConversationId} />}
        {tab === "channels" && <ConversationList activeConversationId={activeConversationId} onSelect={setActiveConversationId} filter={c => c.kind === "Channel"} />}
        {tab === "members" && <ChatMemberList onOpened={setActiveConversationId} />}
      </div>
    </aside>
  );
}
