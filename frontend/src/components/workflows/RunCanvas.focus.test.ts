import { describe, expect, it, vi } from "vitest";

import { focusNodeOnCanvas } from "./RunCanvas";

/**
 * The imperative half of the D3 forward jump — center + zoom the canvas viewport onto a node by id. Pure over its
 * injected `getNode` / `setCenter` (React Flow's own), so it's tested here without rendering the whole flow: a present
 * node centers on its MIDDLE; a not-yet-laid-out / absent node is a clean no-op (a deep-linked ?node= that arrives
 * before the nodes settle must never throw — it retries on the next nodes change).
 */
describe("focusNodeOnCanvas", () => {
  it("centers on the node's middle (position + half its measured size) at the focus zoom", () => {
    const setCenter = vi.fn();
    const getNode = (id: string) => (id === "n1" ? { position: { x: 100, y: 200 }, measured: { width: 180, height: 80 } } : undefined);

    const acted = focusNodeOnCanvas("n1", getNode, setCenter);

    expect(acted).toBe(true);
    // middle = (100 + 180/2, 200 + 80/2) = (190, 240)
    expect(setCenter).toHaveBeenCalledTimes(1);
    const [x, y, opts] = setCenter.mock.calls[0];
    expect([x, y]).toEqual([190, 240]);
    expect(opts?.zoom).toBeGreaterThan(1);     // zooms IN (reads the card), never resets to fit
    expect(opts?.duration).toBeGreaterThan(0); // animated pan so the eye tracks where it landed
  });

  it("falls back to the node's width/height when React Flow hasn't measured it yet", () => {
    const setCenter = vi.fn();
    const getNode = () => ({ position: { x: 0, y: 0 }, width: 200, height: 100 });

    focusNodeOnCanvas("n1", getNode, setCenter);

    const [x, y] = setCenter.mock.calls[0];
    expect([x, y]).toEqual([100, 50]);
  });

  it("no-ops (no throw, no setCenter) when the node isn't in the graph yet — the retry path", () => {
    const setCenter = vi.fn();

    const acted = focusNodeOnCanvas("missing", () => undefined, setCenter);

    expect(acted).toBe(false);
    expect(setCenter).not.toHaveBeenCalled();
  });

  it("no-ops when no focus id is set (the pane isn't focusing any node)", () => {
    const setCenter = vi.fn();
    const getNode = vi.fn();

    const acted = focusNodeOnCanvas(undefined, getNode, setCenter);

    expect(acted).toBe(false);
    expect(getNode).not.toHaveBeenCalled();
    expect(setCenter).not.toHaveBeenCalled();
  });
});
