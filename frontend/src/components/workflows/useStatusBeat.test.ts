import { act, renderHook } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import type { NodeKind, NodeStatus } from "@/api/workflows";

import { useStatusBeat } from "./useStatusBeat";

/**
 * The load-bearing behaviour: a beat fires only on a status TRANSITION observed while mounted, never on the initial
 * render — so opening a run that is already finished is silent — and it clears itself after the ~1.2s CSS window.
 * Which beat (ignite / settle / verdict) is a pure function of the node's kind / typeKey.
 */
interface Props { status: NodeStatus | undefined; kind: NodeKind; typeKey: string }

function setup(initial: Props) {
  return renderHook((p: Props) => useStatusBeat(p.status, p.kind, p.typeKey), { initialProps: initial });
}

describe("useStatusBeat", () => {
  beforeEach(() => vi.useFakeTimers());
  afterEach(() => { vi.useRealTimers(); vi.unstubAllGlobals(); });

  it("fires ignite when a trigger transitions undefined → Success, then clears after 1200ms", () => {
    const h = setup({ status: undefined, kind: "Trigger", typeKey: "trigger.push" });
    expect(h.result.current).toBeUndefined();               // mount: no beat

    h.rerender({ status: "Success", kind: "Trigger", typeKey: "trigger.push" });
    expect(h.result.current).toBe("ignite");                // the transition fires the beat

    act(() => { vi.advanceTimersByTime(1199); });
    expect(h.result.current).toBe("ignite");                // still within the window

    act(() => { vi.advanceTimersByTime(1); });
    expect(h.result.current).toBeUndefined();               // window elapsed → cleared
  });

  it("does NOT replay a beat for a run opened already AT a terminal Success (no transition after mount)", () => {
    const h = setup({ status: "Success", kind: "Trigger", typeKey: "trigger.push" });
    expect(h.result.current).toBeUndefined();

    act(() => { vi.advanceTimersByTime(2000); });
    expect(h.result.current).toBeUndefined();               // stayed silent — critical for finished runs
  });

  it("fires settle when a Terminal node transitions Running → Success", () => {
    const h = setup({ status: "Running", kind: "Terminal", typeKey: "builtin.terminal" });
    h.rerender({ status: "Success", kind: "Terminal", typeKey: "builtin.terminal" });
    expect(h.result.current).toBe("settle");
  });

  it("fires verdict when a routing node (logic.if) transitions Running → Success", () => {
    const h = setup({ status: "Running", kind: "Regular", typeKey: "logic.if" });
    h.rerender({ status: "Success", kind: "Regular", typeKey: "logic.if" });
    expect(h.result.current).toBe("verdict");
  });

  it("fires verdict for a flow.try container settling", () => {
    const h = setup({ status: "Running", kind: "Try", typeKey: "flow.try" });
    h.rerender({ status: "Success", kind: "Try", typeKey: "flow.try" });
    expect(h.result.current).toBe("verdict");
  });

  it("emits no beat for a non-beat kind (a plain Regular node settling)", () => {
    const h = setup({ status: "Running", kind: "Regular", typeKey: "llm.complete" });
    h.rerender({ status: "Success", kind: "Regular", typeKey: "llm.complete" });
    expect(h.result.current).toBeUndefined();
  });

  it("emits no beat for a transition that does not land on Success (Running → Failure)", () => {
    const h = setup({ status: "Running", kind: "Terminal", typeKey: "builtin.terminal" });
    h.rerender({ status: "Failure", kind: "Terminal", typeKey: "builtin.terminal" });
    expect(h.result.current).toBeUndefined();
  });

  it("respects prefers-reduced-motion — returns undefined even on a beat-worthy transition", () => {
    vi.stubGlobal("matchMedia", vi.fn().mockReturnValue({ matches: true }));
    const h = setup({ status: "Running", kind: "Trigger", typeKey: "trigger.push" });
    h.rerender({ status: "Success", kind: "Trigger", typeKey: "trigger.push" });
    expect(h.result.current).toBeUndefined();
  });
});
