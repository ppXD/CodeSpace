import { describe, expect, it } from "vitest";

import type { RunRecordView } from "@/api/workflows";
import { pickLiveText } from "./use-run-room-stream";

/**
 * The SSE subscription + React state are exercised in the running Room; the load-bearing, testable core is the fold that
 * turns a run's raw `interaction.*` ledger tail into the one line of text that is streaming RIGHT NOW.
 */
function rec(sequence: number, recordType: string, correlationId: string | null, payload?: object): RunRecordView {
  return {
    sequence,
    recordType,
    nodeId: null,
    iterationKey: "",
    occurredAt: "2026-07-08T00:00:00Z",
    payloadJson: payload ? JSON.stringify(payload) : "{}",
    correlationId,
    parentRecordId: null,
  };
}

describe("pickLiveText", () => {
  it("accumulates interaction.delta fragments of one call in stream order", () => {
    expect(
      pickLiveText([
        rec(1, "interaction.started", "a"),
        rec(2, "interaction.delta", "a", { ordinal: 0, text: "hello " }),
        rec(3, "interaction.delta", "a", { ordinal: 1, text: "there" }),
      ]),
    ).toBe("hello there");
  });

  it("drops a call's live text once it completes — the poll then settles the finished row", () => {
    expect(
      pickLiveText([
        rec(2, "interaction.delta", "a", { text: "hello there" }),
        rec(4, "interaction.completed", "a", { output: "hello there" }),
      ]),
    ).toBeNull();
  });

  it("drops a call's live text when it fails, same as completion", () => {
    expect(
      pickLiveText([
        rec(2, "interaction.delta", "a", { text: "partial" }),
        rec(4, "interaction.failed", "a", { error: "boom" }),
      ]),
    ).toBeNull();
  });

  it("shows the latest still-streaming call when two interleave", () => {
    expect(
      pickLiveText([
        rec(2, "interaction.delta", "older-call", { text: "older" }),
        rec(5, "interaction.delta", "newer-call", { text: "newer" }),
      ]),
    ).toBe("newer");
  });

  it("falls back to a still-streaming call when a newer one has already completed", () => {
    expect(
      pickLiveText([
        rec(2, "interaction.delta", "a", { text: "still going" }),
        rec(5, "interaction.delta", "b", { text: "done soon" }),
        rec(6, "interaction.completed", "b", {}),
      ]),
    ).toBe("still going");
  });

  it("ignores a delta with no correlation id", () => {
    expect(pickLiveText([rec(1, "interaction.delta", null, { text: "orphan" })])).toBeNull();
  });

  it("returns null when there are no records", () => {
    expect(pickLiveText([])).toBeNull();
  });

  it("tolerates a malformed delta payload", () => {
    const bad: RunRecordView = { ...rec(1, "interaction.delta", "a"), payloadJson: "{not json" };
    expect(pickLiveText([bad])).toBeNull();
  });
});
