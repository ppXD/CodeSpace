import { describe, expect, it } from "vitest";

import type { RoomAction, RoomBlock } from "@/api/sessions";
import { liveRunSummary } from "./SessionRoomView";

/**
 * The pinned contract for the running turn's pinned live bar: it shows the LATEST activity line (a run emits several
 * live_activity blocks over its life), and it offers Stop only when the backend says the capability is enabled.
 */
function activity(id: string, text: string): RoomBlock {
  return { id, type: "live_activity", text } as RoomBlock;
}

function stop(enabled: boolean): RoomAction {
  return { kind: "Stop", enabled } as RoomAction;
}

describe("liveRunSummary", () => {
  it("takes the latest live_activity line", () => {
    const blocks = [activity("1", "dispatching agents"), activity("2", "running tests")];
    expect(liveRunSummary({ blocks, actions: [] }).activity).toBe("running tests");
  });

  it("is empty when the turn has no live_activity block", () => {
    expect(liveRunSummary({ blocks: [], actions: [] }).activity).toBe("");
  });

  it("can stop only when an enabled Stop action is present", () => {
    expect(liveRunSummary({ blocks: [], actions: [stop(true)] }).canStop).toBe(true);
    expect(liveRunSummary({ blocks: [], actions: [stop(false)] }).canStop).toBe(false);
    expect(liveRunSummary({ blocks: [], actions: [{ kind: "OpenTrace", enabled: true } as RoomAction] }).canStop).toBe(false);
    expect(liveRunSummary({ blocks: [], actions: [] }).canStop).toBe(false);
  });
});
