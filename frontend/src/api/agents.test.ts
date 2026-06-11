import { describe, expect, it } from "vitest";

import { type AgentRunEventDto, lastEventSequence, mergeRunEvents } from "./agents";

/**
 * The incremental live-log merge that backs useAgentRunEvents: each poll fetches only events past the
 * highest sequence already held, then merges. These pin the load-bearing behaviour — ordered by the
 * monotonic DB sequence, deduped against a cursor overlap, and reference-stable on a quiet tick (no
 * needless re-render).
 */
const ev = (sequence: number, kind = "AssistantMessage"): AgentRunEventDto =>
  ({ sequence, kind, text: `e${sequence}`, data: null, occurredAt: "2026-06-11T00:00:00Z" });

describe("mergeRunEvents", () => {
  it("returns the fresh batch (sorted) when there's nothing prior", () => {
    expect(mergeRunEvents([], [ev(2), ev(1), ev(3)]).map((e) => e.sequence)).toEqual([1, 2, 3]);
  });

  it("appends a newer delta in sequence order", () => {
    const prev = [ev(1), ev(2)];
    expect(mergeRunEvents(prev, [ev(3), ev(4)]).map((e) => e.sequence)).toEqual([1, 2, 3, 4]);
  });

  it("sorts an out-of-order delta by sequence", () => {
    expect(mergeRunEvents([ev(1)], [ev(4), ev(2), ev(3)]).map((e) => e.sequence)).toEqual([1, 2, 3, 4]);
  });

  it("dedups a cursor overlap, keeping each sequence once", () => {
    const prev = [ev(1), ev(2), ev(3)];
    const merged = mergeRunEvents(prev, [ev(3), ev(4)]);   // 3 re-sent
    expect(merged.map((e) => e.sequence)).toEqual([1, 2, 3, 4]);
  });

  it("returns the SAME reference when the fresh batch is empty (no re-render)", () => {
    const prev = [ev(1), ev(2)];
    expect(mergeRunEvents(prev, [])).toBe(prev);
  });

  it("returns the SAME reference when the fresh batch is a full overlap (nothing new)", () => {
    const prev = [ev(1), ev(2)];
    expect(mergeRunEvents(prev, [ev(1), ev(2)])).toBe(prev);
  });
});

describe("lastEventSequence", () => {
  it("is 0 for an empty log (→ fetches the whole log from after=0)", () => {
    expect(lastEventSequence([])).toBe(0);
  });

  it("is the max sequence, regardless of order", () => {
    expect(lastEventSequence([ev(1), ev(2), ev(3)])).toBe(3);
    expect(lastEventSequence([ev(3), ev(1), ev(2)])).toBe(3);
  });
});
