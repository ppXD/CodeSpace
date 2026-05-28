import { useState } from "react";

import { Ic } from "@/_imported/ai-code-space/icons";
import { useAddMember } from "@/hooks/use-chat";
import { useTeamMembers } from "@/hooks/use-team-members";

/**
 * Header "add people" control for a channel/group — the invite-from-within flow (no self-join).
 * The "+" opens a dropdown of team members not already in the conversation; clicking one invites
 * them via AddMember. Added members drop off the list as the conversation's membership refreshes.
 * Not rendered for DMs (fixed pairs — the server rejects adding to them).
 */
export function ChannelInvite({ conversationId, memberUserIds }: { conversationId: string; memberUserIds: string[] }) {
  const [open, setOpen] = useState(false);
  const members = useTeamMembers();
  const add = useAddMember(conversationId);

  const current = new Set(memberUserIds);
  const candidates = (members.data ?? []).filter(m => !current.has(m.userId));

  return (
    <div className="chat-invite">
      <button className="chrome-btn" title="Add people" aria-label="Add people" data-active={open} onClick={() => setOpen(o => !o)}>
        <Ic.Plus size={15} />
      </button>

      {open && (
        <>
          <div className="chat-invite-mask" onClick={() => setOpen(false)} />
          <div className="chat-invite-pop">
            <div className="chat-invite-pop-head">Add people</div>
            <div className="chat-invite-pop-list">
              {members.isLoading && <div className="chat-empty">Loading…</div>}

              {!members.isLoading && candidates.length === 0 && (
                <div className="chat-empty">Everyone's already here.</div>
              )}

              {candidates.map(m => (
                <button key={m.userId} type="button" className="chat-invite-item" disabled={add.isPending} onClick={() => add.mutate(m.userId)}>
                  <span className="chat-conv-av chat-conv-av-initial">{m.name.charAt(0).toUpperCase()}</span>
                  <span className="chat-invite-name">{m.name}</span>
                  <Ic.Plus size={13} />
                </button>
              ))}
            </div>
          </div>
        </>
      )}
    </div>
  );
}
