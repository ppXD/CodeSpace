import { act, fireEvent, render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

import type { NodeStatus, WorkflowRunNodeSummary } from "@/api/workflows";
import { RunLiveContext, type RunLiveStore } from "@/hooks/use-run-live";
import type { NodeLiveSignals, RunLiveState } from "@/lib/runLiveFold";

import { MapFanout } from "./MapFanout";

/** A flow.map element-branch row keyed `map#<index>`, the shape the agent body fans out into. */
function branch(index: number, status: NodeStatus): WorkflowRunNodeSummary {
  return { nodeId: "agent", iterationKey: `map#${index}`, containerKind: "flow.map", status, inputs: null, outputs: null, error: null, startedAt: null, completedAt: null };
}

/** A frozen live store that reports `signals` for node "agent" — the overlay MapFanout reads via useNodeLiveContext. */
function liveStore(signals: NodeLiveSignals): RunLiveStore {
  const state: RunLiveState = { byNode: new Map([["agent", signals]]), lastSeq: 0, terminal: false };
  return { getState: () => state, subscribe: () => () => {} };
}

const dots = (c: HTMLElement) => [...c.querySelectorAll<HTMLElement>(".wf-rf-fanout-dot")];

describe("MapFanout", () => {
  it("renders one dot per branch (sorted by element index) + the per-state summary, no terminal until a branch is picked", () => {
    const rows = [branch(2, "Running"), branch(0, "Success"), branch(1, "Failure"), branch(3, "Pending")];

    const { container } = render(<MapFanout rows={rows} renderBranch={() => <div>detail</div>} />);

    expect(screen.getByText("4 branches")).toBeInTheDocument();
    expect(screen.getByText("1 done")).toBeInTheDocument();
    expect(screen.getByText("1 running")).toBeInTheDocument();
    expect(screen.getByText("1 failed")).toBeInTheDocument();
    expect(screen.getByText("1 queued")).toBeInTheDocument();

    expect(dots(container).map((d) => d.dataset.state)).toEqual(["done", "failed", "running", "queued"]);   // #0..#3
    expect(container.querySelector(".wf-rf-fanout-term")).toBeNull();   // nothing focused yet
  });

  it("focuses a branch on click — its terminal renders via renderBranch with the RIGHT row — and toggles off", () => {
    const rows = [branch(0, "Success"), branch(1, "Failure")];
    const renderBranch = vi.fn((row: WorkflowRunNodeSummary) => <div>detail of {row.iterationKey}</div>);

    const { container } = render(<MapFanout rows={rows} renderBranch={renderBranch} />);

    fireEvent.click(dots(container)[1]);   // the failed branch, #1
    expect(container.querySelector(".wf-rf-fanout-term")).not.toBeNull();
    expect(screen.getByText("#1")).toBeInTheDocument();
    expect(renderBranch).toHaveBeenLastCalledWith(rows[1]);            // the #1 row by index, not array position
    expect(dots(container)[1].dataset.sel).toBe("true");

    fireEvent.click(dots(container)[1]);   // click the focused branch again → collapse
    expect(container.querySelector(".wf-rf-fanout-term")).toBeNull();
  });

  it("keeps the focused branch pinned to its element when an earlier branch appears on a poll", () => {
    const renderBranch = (r: WorkflowRunNodeSummary) => <div>detail of {r.iterationKey}</div>;
    const { container, rerender } = render(<MapFanout rows={[branch(5, "Failure"), branch(7, "Running")]} renderBranch={renderBranch} />);

    fireEvent.click(dots(container)[1]);   // focus element #7 (the second, sorted dot)
    expect(screen.getByText("#7")).toBeInTheDocument();

    // A 2s live poll surfaces a slower LOWER-index branch (#2) that sorts to the FRONT, shifting every later
    // branch one slot right. Selection is keyed by element index, so the open terminal must STAY on #7.
    rerender(<MapFanout rows={[branch(2, "Running"), branch(5, "Failure"), branch(7, "Running")]} renderBranch={renderBranch} />);

    expect(screen.getByText("#7")).toBeInTheDocument();                    // not #5 (the slot the old code would show)
    expect(screen.getByText(/detail of map#7/)).toBeInTheDocument();
  });

  it("renders nothing for a non-map (loop / flat) row set so the caller keeps its plain list", () => {
    const loopRow: WorkflowRunNodeSummary = { ...branch(0, "Success"), containerKind: "flow.loop" };

    const { container } = render(<MapFanout rows={[loopRow]} renderBranch={() => <div>x</div>} />);

    expect(container.querySelector(".wf-rf-fanout")).toBeNull();
  });

  it("counts a Suspended branch as waiting — a '· N waiting' summary entry + a waiting dot (distinct from running)", () => {
    const rows = [branch(0, "Success"), branch(1, "Suspended"), branch(2, "Running")];

    const { container } = render(<MapFanout rows={rows} renderBranch={() => null} />);

    expect(screen.getByText("· 1 waiting")).toBeInTheDocument();
    expect(screen.getByText("1 running")).toBeInTheDocument();                                     // Suspended no longer folds into running
    expect(container.querySelector('.wf-rf-fanout-dot[data-state="waiting"]')).not.toBeNull();
  });

  it("pulses ONLY the branch that just flipped (data-fresh), leaving steady running cells static", () => {
    vi.useFakeTimers();
    try {
      const r0 = [branch(0, "Running"), branch(1, "Running"), branch(2, "Running")];
      const { container, rerender } = render(<MapFanout rows={r0} renderBranch={() => null} />);

      expect(dots(container).filter((d) => d.hasAttribute("data-fresh"))).toHaveLength(0);          // first render: nothing "just changed"

      rerender(<MapFanout rows={[branch(0, "Running"), branch(1, "Success"), branch(2, "Running")]} renderBranch={() => null} />);

      const flipped = dots(container).filter((d) => d.hasAttribute("data-fresh"));
      expect(flipped).toHaveLength(1);
      expect(flipped[0]).toBe(dots(container)[1]);                                                   // only #1 (Running → Success)

      act(() => vi.advanceTimersByTime(1300));                                                       // the transient attribute clears after ~1.2s
      expect(dots(container).filter((d) => d.hasAttribute("data-fresh"))).toHaveLength(0);
    } finally {
      vi.useRealTimers();
    }
  });

  it("uses the live branches.total as the denominator, padding queued dots up to it", () => {
    const rows = Array.from({ length: 7 }, (_, i) => branch(i, "Running"));
    const store = liveStore({ branches: { done: 0, failed: 0, running: 7, waiting: 0, total: 12 }, lastEventSeq: 0 });

    const { container } = render(
      <RunLiveContext.Provider value={store}><MapFanout rows={rows} renderBranch={() => null} /></RunLiveContext.Provider>,
    );

    expect(dots(container)).toHaveLength(12);                                                        // 7 rendered branches + 5 queued placeholders
    expect(dots(container).filter((d) => d.dataset.state === "queued")).toHaveLength(5);
    expect(screen.getByText("12 branches")).toBeInTheDocument();                                     // denominator = live total, not the 7 rows
  });

  it("falls back to the rendered branch count as denominator when the live total is absent", () => {
    const rows = Array.from({ length: 7 }, (_, i) => branch(i, "Running"));
    const store = liveStore({ branches: { done: 0, failed: 0, running: 7, waiting: 0 }, lastEventSeq: 0 });

    const { container } = render(
      <RunLiveContext.Provider value={store}><MapFanout rows={rows} renderBranch={() => null} /></RunLiveContext.Provider>,
    );

    expect(dots(container)).toHaveLength(7);                                                         // no placeholders — total = rows count
    expect(screen.getByText("7 branches")).toBeInTheDocument();
  });

  it("folds a fully-settled map into a results chip with the branch count; clicking it collapses the strip", () => {
    const rows = [branch(0, "Success"), branch(1, "Success"), branch(2, "Failure")];

    const { container } = render(<MapFanout rows={rows} renderBranch={() => null} />);

    const chip = container.querySelector<HTMLElement>(".wf-rf-fanout-reduce");
    expect(chip).not.toBeNull();
    expect(chip?.textContent).toContain("[3]");
    expect(container.querySelector(".wf-rf-fanout-strip")).not.toBeNull();                           // strip stays reachable (existing focus-a-branch flow)

    fireEvent.click(chip!);
    expect(container.querySelector(".wf-rf-fanout-strip")).toBeNull();                               // clicking the chip folds the cells away
  });

  it("derives counts from the rows alone when there is no live store (degrades, no throw)", () => {
    const rows = [branch(0, "Success"), branch(1, "Running")];

    const { container } = render(<MapFanout rows={rows} renderBranch={() => null} />);

    expect(screen.getByText("2 branches")).toBeInTheDocument();
    expect(screen.getByText("1 done")).toBeInTheDocument();
    expect(screen.getByText("1 running")).toBeInTheDocument();
    expect(dots(container)).toHaveLength(2);
  });
});
