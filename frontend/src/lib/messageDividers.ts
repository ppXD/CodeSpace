/**
 * Pure helpers for the conversation reading experience — the date separators and the single
 * "new messages" divider, mirroring Slack / Space. Kept free of React so the day-boundary and
 * unread-cursor logic is unit-testable without rendering.
 */

interface DividerMessage {
  id: string;
  authorUserId: string;
}

/** True when two messages fall on different calendar days (→ draw a date divider between them). */
export function isNewDay(prevIso: string, curIso: string): boolean {
  return new Date(prevIso).toDateString() !== new Date(curIso).toDateString();
}

/**
 * The label for a day divider: "Today" / "Yesterday" for the recent two, a weekday+date for the
 * rest of this year ("Wednesday, March 5"), and a year-qualified date for older ("March 5, 2024").
 */
export function dayDividerLabel(iso: string, now: Date = new Date()): string {
  const date = new Date(iso);
  const diffDays = Math.round((startOfDay(now).getTime() - startOfDay(date).getTime()) / 86_400_000);

  if (diffDays === 0) return "Today";
  if (diffDays === 1) return "Yesterday";

  return date.getFullYear() === now.getFullYear()
    ? date.toLocaleDateString(undefined, { weekday: "long", month: "long", day: "numeric" })
    : date.toLocaleDateString(undefined, { month: "long", day: "numeric", year: "numeric" });
}

/**
 * The id of the first message that should sit below the "new messages" divider, or null for no
 * divider. Messages are oldest-first; ids are UUID v7 so a lexicographic `>` is chronological.
 *
 * A null cursor (never read anything) yields no divider — the whole conversation is "new", and
 * Slack shows nothing in that case. Our own messages never trigger the divider, so re-opening a
 * conversation we just posted in doesn't flag our own line as unread.
 */
export function firstUnreadId(messages: ReadonlyArray<DividerMessage>, lastReadId: string | null, myUserId: string | null): string | null {
  if (!lastReadId) return null;

  const firstUnread = messages.find(m => m.id > lastReadId && m.authorUserId !== myUserId);
  return firstUnread ? firstUnread.id : null;
}

function startOfDay(d: Date): Date {
  return new Date(d.getFullYear(), d.getMonth(), d.getDate());
}
