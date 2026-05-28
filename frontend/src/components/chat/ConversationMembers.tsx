import { useState } from "react";

import { Ic } from "@/_imported/ai-code-space/icons";
import { useAddMember } from "@/hooks/use-chat";
import { useMe } from "@/hooks/use-me";
import { useTeamMembers } from "@/hooks/use-team-members";

/**
 * Header members control for a channel/group: the "N members" count is a button that opens a
 * popover listing who's in the conversation (the caller marked "(you)") plus an "Add people"
 * section — the invite-from-within flow, no self-join. Clicking a candidate invites them via
 * AddMember; added members move from the candidate list into the roster as membership refreshes.
 * Not rendered for DMs (fixed pairs — the title already names the other person).
 */
export function ConversationMembers({ conversationId, memberUserIds }: { conversationId: string; memberUserIds: string[] }) {
  const [open, setOpen] = useState(false);
  const membersQuery = useTeamMembers();
  const me = useMe();
  const add = useAddMember(conversationId);

  const roster = membersQuery.data ?? [];
  const byId = new Map(roster.map(m => [m.userId, m]));
  const memberSet = new Set(memberUserIds);
  const candidates = roster.filter(m => !memberSet.has(m.userId));

  const count = memberUserIds.length;

  return (
    <div className="chat-members">
      <button className="chat-members-trigger" data-active={open} aria-label="View members" onClick={() => setOpen(o => !o)}>
        <Ic.Users size={13} />
        <span>{count} {count === 1 ? "member" : "members"}</span>
      </button>

      {open && (
        <>
          <div className="chat-invite-mask" onClick={() => setOpen(false)} />
          <div className="chat-invite-pop chat-members-pop">
            <div className="chat-invite-pop-list">
              <div className="chat-members-subhead">Members · {count}</div>
              {memberUserIds.map(id => (
                <div key={id} className="chat-members-row">
                  <span className="chat-conv-av chat-conv-av-initial">{(byId.get(id)?.name ?? "?").charAt(0).toUpperCase()}</span>
                  <span className="chat-invite-name">{byId.get(id)?.name ?? "Unknown"}{id === me.data?.id ? " (you)" : ""}</span>
                </div>
              ))}

              <div className="chat-members-subhead">Add people</div>
              {membersQuery.isLoading && <div className="chat-empty">Loading…</div>}

              {!membersQuery.isLoading && candidates.length === 0 && (
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
