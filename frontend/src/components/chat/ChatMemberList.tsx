import { Ic } from "@/_imported/ai-code-space/icons";
import { useOpenDirect } from "@/hooks/use-chat";
import { useMe } from "@/hooks/use-me";
import { useTeamMembers } from "@/hooks/use-team-members";

/**
 * The Members tab: the team roster (minus yourself). Clicking a member find-or-creates the 1:1
 * DM with them and opens it — the race-safe singleton server-side means clicking twice resolves
 * to the same conversation, never a duplicate.
 */
export function ChatMemberList({ onOpened }: { onOpened: (conversationId: string) => void }) {
  const membersQuery = useTeamMembers();
  const me = useMe();
  const openDirect = useOpenDirect();

  const others = (membersQuery.data ?? []).filter(m => m.userId !== me.data?.id);

  const open = async (userId: string) => {
    if (openDirect.isPending) return;
    const dm = await openDirect.mutateAsync(userId);
    onOpened(dm.id);
  };

  return (
    <div className="chat-list">
      <div className="chat-list-body">
        {membersQuery.isLoading && <div className="chat-empty">Loading…</div>}

        {!membersQuery.isLoading && others.length === 0 && (
          <div className="chat-empty">No other members in this team yet.</div>
        )}

        {others.map(m => (
          <button key={m.userId} type="button" className="chat-conv" disabled={openDirect.isPending} onClick={() => open(m.userId)}>
            <span className="chat-conv-icon"><Ic.Users size={13} /></span>
            <span className="chat-conv-name">{m.name}</span>
          </button>
        ))}
      </div>
    </div>
  );
}
