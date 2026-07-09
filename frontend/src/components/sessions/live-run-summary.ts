import type { AssistantTurnBlock, LiveActivityBlock } from "@/api/sessions";

/** The running turn's live summary — the LATEST activity line (a run emits several live_activity blocks over its life)
 *  and whether an enabled Stop action is available. Pure + in its own module so the latest-wins + Stop-gating contract is
 *  unit-tested without standing up the Room's query/dialog providers, and so SessionRoomView stays a components-only file. */
export function liveRunSummary(turn: Pick<AssistantTurnBlock, "blocks" | "actions">): { activity: string; canStop: boolean } {
  const activity = turn.blocks.filter((b): b is LiveActivityBlock => b.type === "live_activity").at(-1)?.text ?? "";
  const canStop = turn.actions.some((a) => a.kind === "Stop" && a.enabled);
  return { activity, canStop };
}
