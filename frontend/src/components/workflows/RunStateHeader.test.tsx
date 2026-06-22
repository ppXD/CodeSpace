import { render } from "@testing-library/react";
import { describe, expect, it } from "vitest";

import type { RunPhase } from "@/api/workflows";

import { RunStateHeader } from "./RunStateHeader";

function phase(o: Partial<RunPhase>): RunPhase {
  return {
    id: "p", label: "P", kind: "node", status: "Pending", order: 0,
    agents: [], metrics: { agentCount: 0, succeededCount: 0, failedCount: 0 }, sourceKey: "s",
    ...o,
  };
}

describe("RunStateHeader", () => {
  it("composes the control-state sentence from the run status + phases", () => {
    const { container, getByText } = render(<RunStateHeader runStatus="Running" phases={[
      phase({ label: "Implement", status: "Active", metrics: { agentCount: 4, succeededCount: 0, failedCount: 0 }, agents: [
        { agentRunId: "a1", status: "Running" }, { agentRunId: "a2", status: "Running" },
        { agentRunId: "a3", status: "Succeeded" }, { agentRunId: "a4", status: "Succeeded" },
      ] }),
    ]} />);
    expect(getByText("Running")).toBeTruthy();
    expect(getByText("Implement")).toBeTruthy();
    expect(getByText("2 of 4 agents active")).toBeTruthy();
    const region = container.querySelector('.run-state[data-status="running"]');
    expect(region).not.toBeNull();
    // The visual `·`-separated spans are aria-hidden; the live region carries one clean sentence.
    expect(region?.getAttribute("role")).toBe("status");
    expect(region?.getAttribute("aria-label")).toBe("Running, Implement, 2 of 4 agents active");
  });

  it("surfaces a waiting count for a parked run", () => {
    const { getByText } = render(<RunStateHeader runStatus="Suspended" phases={[
      phase({ label: "Approve push", status: "Waiting" }),
    ]} />);
    expect(getByText("1 waiting")).toBeTruthy();
  });

  it("shows just the status for a clean terminal run with no agents", () => {
    const { container, queryByText } = render(<RunStateHeader runStatus="Success" phases={[
      phase({ label: "Done", status: "Succeeded" }),
    ]} />);
    expect(container.querySelector('.run-state[data-status="success"] .run-state-lead')?.textContent).toBe("Success");
    expect(queryByText(/agents active/)).toBeNull();   // no agents → no agent clause
  });
});
