import { describe, expect, it } from "vitest";

import type { JournalStep, RoomBlock } from "@/api/sessions";
import type { WorkflowRunStatus } from "@/api/workflows";
import { journalStepNodeId, type PaneBinding, resolveBinding, resolvePaneFromTurn, shouldShowJumpToLatest } from "./SessionRoomView";

/**
 * The companion pane's split-state decision, extracted as a pure helper so it's testable without rendering the
 * heavy Room. It maps a turn number → the run to dock (summon + URL-restore) or null (close / no request). The
 * full summon→open / close→single-column integration is covered by `pnpm build` + the post-merge :5180 pass; the
 * Room's own render pulls in the whole chat composer + terminals, so it isn't unit-rendered here.
 */
function turn(id: string, seq: number, turnIndex: number, runId: string, status: WorkflowRunStatus = "Success"): RoomBlock {
  return { type: "assistant_turn", id, seq, turnIndex, turnRunId: runId, runId, status, blocks: [], actions: [] };
}

const blocks: RoomBlock[] = [
  { type: "user_message", id: "u2", seq: 0, text: "do a thing" },
  turn("a2", 1, 2, "run-2"),
  { type: "user_message", id: "u5", seq: 2, text: "another thing" },
  turn("a5", 3, 5, "run-5"),
];

describe("resolvePaneFromTurn", () => {
  it("binds a present turn to its run (summon / URL-restore)", () => {
    expect(resolvePaneFromTurn(blocks, 5)).toEqual({ runId: "run-5", turn: 5 });
    expect(resolvePaneFromTurn(blocks, 2)).toEqual({ runId: "run-2", turn: 2 });
  });

  it("returns null for no requested turn (closed pane)", () => {
    expect(resolvePaneFromTurn(blocks, null)).toBeNull();
  });

  it("returns null for a turn number that isn't in the room", () => {
    expect(resolvePaneFromTurn(blocks, 9)).toBeNull();
  });
});

/**
 * The D2 follow/pin binding decision. `resolveBinding` derives the pane's effective { runId, turn, view }: follow →
 * the LATEST turn's run (so it auto-rebinds when a new turn advances the latest), pinned → the pinned turn's run.
 * `shouldShowJumpToLatest` gates the "jump to latest" chip — pinned behind a still-active newer turn. Both pure.
 */
describe("resolveBinding (follow / pin effective turn)", () => {
  it("follow → the LATEST turn's run + view", () => {
    const b: PaneBinding = { open: true, mode: "follow", view: "canvas" };
    expect(resolveBinding(blocks, b, 5)).toEqual({ runId: "run-5", turn: 5, view: "canvas" });
  });

  it("follow rebinds when the latest advances (a new turn staged)", () => {
    const b: PaneBinding = { open: true, mode: "follow", view: "trace" };
    expect(resolveBinding(blocks, b, 2)).toEqual({ runId: "run-2", turn: 2, view: "trace" }); // latest was 2
    expect(resolveBinding(blocks, b, 5)).toEqual({ runId: "run-5", turn: 5, view: "trace" }); // latest advanced to 5
  });

  it("pinned → the PINNED turn's run, ignoring the latest", () => {
    const b: PaneBinding = { open: true, mode: "pinned", turn: 2, view: "changes" };
    expect(resolveBinding(blocks, b, 5)).toEqual({ runId: "run-2", turn: 2, view: "changes" });
  });

  it("returns null when closed, or when the bound turn is absent", () => {
    expect(resolveBinding(blocks, { open: false }, 5)).toBeNull();
    expect(resolveBinding(blocks, { open: true, mode: "follow", view: "canvas" }, null)).toBeNull(); // no turns yet
    expect(resolveBinding(blocks, { open: true, mode: "pinned", turn: 9, view: "canvas" }, 5)).toBeNull(); // missing pin
  });

  it("carries the D3 canvas focus `node` through when the binding has one, and omits it otherwise", () => {
    const focused: PaneBinding = { open: true, mode: "pinned", turn: 2, view: "canvas", node: "step-3" };
    expect(resolveBinding(blocks, focused, 5)).toEqual({ runId: "run-2", turn: 2, view: "canvas", node: "step-3" });

    // A nodeless binding resolves without a `node` key at all — no undefined leaks into the URL/focus prop.
    const bare: PaneBinding = { open: true, mode: "pinned", turn: 2, view: "canvas" };
    expect(resolveBinding(blocks, bare, 5)).not.toHaveProperty("node");
  });
});

/**
 * The D3 forward-jump gate — a journal step exposes a canvas node only when the backend attached one, so the
 * "在Canvas查看" affordance is never offered without a real target (a fabricated node would setCenter on nothing).
 */
describe("journalStepNodeId (the no-id → no-affordance gate)", () => {
  const step = (over: Partial<JournalStep>): JournalStep => ({
    id: "s", cursor: "c", at: "2026-07-14T00:00:00Z", kind: "decision", beat: true, title: "Dispatched", tone: "Info",
    milestone: true, agents: [], deferred: [], plan: [], ...over,
  });

  it("returns the node id for a step that maps to a workflow node", () => {
    expect(journalStepNodeId(step({ nodeId: "map-1" }))).toBe("map-1");
  });

  it("returns null for a step with no node (supervisor decision / model call / lifecycle) → no affordance", () => {
    expect(journalStepNodeId(step({ nodeId: null }))).toBeNull();
    expect(journalStepNodeId(step({}))).toBeNull();
  });
});

describe("shouldShowJumpToLatest", () => {
  const pinned2: PaneBinding = { open: true, mode: "pinned", turn: 2, view: "canvas" };

  it("true when pinned behind a NEWER, ACTIVE turn", () => {
    expect(shouldShowJumpToLatest(pinned2, { turnIndex: 5, status: "Running" })).toBe(true);
  });

  it("false when the newer turn is already terminal", () => {
    expect(shouldShowJumpToLatest(pinned2, { turnIndex: 5, status: "Success" })).toBe(false);
  });

  it("false when the latest is not newer than the pin (same or older)", () => {
    expect(shouldShowJumpToLatest(pinned2, { turnIndex: 2, status: "Running" })).toBe(false);
  });

  it("false when following (the pane already tracks the latest) or closed", () => {
    expect(shouldShowJumpToLatest({ open: true, mode: "follow", view: "canvas" }, { turnIndex: 5, status: "Running" })).toBe(false);
    expect(shouldShowJumpToLatest({ open: false }, { turnIndex: 5, status: "Running" })).toBe(false);
    expect(shouldShowJumpToLatest(pinned2, null)).toBe(false);
  });
});
