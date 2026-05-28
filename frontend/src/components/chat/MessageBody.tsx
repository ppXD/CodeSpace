import type { TeamMemberSummary } from "@/api/teams";
import { parseMessageBody } from "@/lib/messageReferences";

/**
 * Renders a message body with inline `@`-reference chips. Generic: the chip keys off
 * <c>data-ref-type</c> for theming but the renderer hardcodes only the one universal mention
 * convention — a leading <c>@</c> for <c>user</c> refs. A user ref with no cached label resolves
 * its display name from the member map; any other type shows its label (or raw refId fallback).
 */
export function MessageBody({ body, members }: { body: string; members: Map<string, TeamMemberSummary> }) {
  const segments = parseMessageBody(body);

  return (
    <span className="chat-msg-text">
      {segments.map((seg, i) =>
        seg.kind === "text" ? (
          <span key={i}>{seg.text}</span>
        ) : (
          <span key={i} className="chat-ref" data-ref-type={seg.refType} title={`${seg.refType}:${seg.refId}`}>
            {seg.refType === "user" ? "@" : ""}
            {resolveDisplay(seg.refType, seg.refId, seg.label, members)}
          </span>
        ),
      )}
    </span>
  );
}

function resolveDisplay(refType: string, refId: string, label: string | null, members: Map<string, TeamMemberSummary>): string {
  if (label) return label;
  if (refType === "user") return members.get(refId)?.name ?? "user";
  return refId;
}
