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

  it("prefers the real decision count over the phase-waiting proxy, pluralizing it", () => {
    const phases = [phase({ label: "Approve push", status: "Waiting" })];
    const { getByText, rerender } = render(<RunStateHeader runStatus="Suspended" phases={phases} pendingDecisions={2} />);
    expect(getByText("2 decisions need you")).toBeTruthy();
    expect(() => getByText("1 waiting")).toThrow();   // the proxy is superseded

    rerender(<RunStateHeader runStatus="Suspended" phases={phases} pendingDecisions={1} />);
    expect(getByText("1 decision needs you")).toBeTruthy();
  });

  it("drops the needs-you clause when the inbox has loaded with zero decisions", () => {
    const { queryByText } = render(<RunStateHeader runStatus="Running" phases={[
      phase({ label: "Approve push", status: "Waiting" }),
    ]} pendingDecisions={0} />);
    expect(queryByText("1 waiting")).toBeNull();          // loaded-and-empty wins over the proxy
    expect(queryByText(/needs you/)).toBeNull();
  });

  it("shows just the status for a clean terminal run with no agents", () => {
    const { container, queryByText } = render(<RunStateHeader runStatus="Success" phases={[
      phase({ label: "Done", status: "Succeeded" }),
    ]} />);
    expect(container.querySelector('.run-state[data-status="success"] .run-state-lead')?.textContent).toBe("Success");
    expect(queryByText(/agents active/)).toBeNull();   // no agents → no agent clause
  });

  it("settles the agent tally to a plain count on a terminal run (no nonsensical '0 of 1 active')", () => {
    const { getByText, queryByText } = render(<RunStateHeader runStatus="Success" phases={[
      phase({ label: "code", status: "Succeeded", agents: [{ agentRunId: "a1", status: "Succeeded" }] }),
    ]} />);
    expect(getByText("1 agent")).toBeTruthy();          // settled count, not "0 of 1 agents active"
    expect(queryByText(/agents active/)).toBeNull();    // the live-progress phrasing is gone once terminal
  });

  it("pluralizes the settled agent count on a multi-agent terminal run", () => {
    const { getByText } = render(<RunStateHeader runStatus="Failure" phases={[
      phase({ label: "fan-out", status: "Failed", agents: [
        { agentRunId: "a1", status: "Succeeded" }, { agentRunId: "a2", status: "Failed" }, { agentRunId: "a3", status: "Succeeded" },
      ] }),
    ]} />);
    expect(getByText("3 agents")).toBeTruthy();
  });

  it("keeps the live 'N of M active' phrasing while the run is still active", () => {
    const { getByText } = render(<RunStateHeader runStatus="Running" phases={[
      phase({ label: "code", status: "Active", agents: [{ agentRunId: "a1", status: "Running" }] }),
    ]} />);
    expect(getByText("1 of 1 agents active")).toBeTruthy();   // active run → live tally, even at full/zero counts
  });
});
