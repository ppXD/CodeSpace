import type { TeamMemberSummary } from "@/api/teams";
import { avatarColor } from "@/lib/avatarColor";

/**
 * The @-mention autocomplete dropdown — purely presentational. The composer owns the query,
 * candidate list, and active-row index (driven by both arrow keys and hover); this just draws them.
 * Picking uses onMouseDown with preventDefault so the contenteditable keeps focus + selection, which
 * the composer needs to insert the chip at the right caret. Renders nothing when there's no match.
 */
export function MentionPicker({ candidates, activeIndex, onPick, onHover }: {
  candidates: readonly TeamMemberSummary[];
  activeIndex: number;
  onPick: (member: TeamMemberSummary) => void;
  onHover: (index: number) => void;
}) {
  if (candidates.length === 0) return null;

  return (
    <div className="mention-pop" role="listbox">
      {candidates.map((m, i) => {
        const color = avatarColor(m.userId);

        return (
          <button
            key={m.userId}
            type="button"
            role="option"
            aria-selected={i === activeIndex}
            className="mention-opt"
            data-active={i === activeIndex}
            onMouseDown={(e) => { e.preventDefault(); onPick(m); }}
            onMouseEnter={() => onHover(i)}
          >
            <span className="chat-conv-av mention-opt-av" style={{ background: color.bg, color: color.fg }}>{m.name.charAt(0).toUpperCase()}</span>
            <span className="mention-opt-name">{m.name}</span>
          </button>
        );
      })}
    </div>
  );
}
