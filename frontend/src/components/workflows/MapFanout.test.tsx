import { fireEvent, render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

import type { NodeStatus, WorkflowRunNodeSummary } from "@/api/workflows";

import { MapFanout } from "./MapFanout";

/** A flow.map element-branch row keyed `map#<index>`, the shape the agent body fans out into. */
function branch(index: number, status: NodeStatus): WorkflowRunNodeSummary {
  return { nodeId: "agent", iterationKey: `map#${index}`, containerKind: "flow.map", status, inputs: null, outputs: null, error: null, startedAt: null, completedAt: null };
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
});
