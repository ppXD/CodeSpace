import { fireEvent, render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

import { CockpitCards, type CockpitMetrics } from "./CockpitCards";

function metrics(o: Partial<CockpitMetrics> = {}): CockpitMetrics {
  return {
    decisions: { count: 0, oldestAge: null, highRisk: 0 },
    liveCount: 0, agentsActive: 0, failed: 0, suspended: 0,
    today: { count: 0, hourly: new Array(24).fill(0) },
    ...o,
  };
}

describe("CockpitCards", () => {
  it("renders the four cards with values + composed subtext", () => {
    const { container } = render(<CockpitCards filter={null} onFilter={() => {}} metrics={metrics({
      decisions: { count: 2, oldestAge: "14m", highRisk: 1 },
      liveCount: 3, agentsActive: 9, failed: 1, suspended: 1,
      today: { count: 28, hourly: new Array(24).fill(1) },
    })} />);

    expect(screen.getByText("Needs decision")).toBeTruthy();
    expect(screen.getByText("oldest 14m · 1 high risk")).toBeTruthy();
    expect(screen.getByText("9 agents active")).toBeTruthy();
    expect(screen.getByText("1 failed · 1 suspended")).toBeTruthy();
    expect(screen.getByText("28 runs")).toBeTruthy();
    expect(container.querySelectorAll(".cockpit-card").length).toBe(4);
    expect(container.querySelector(".cockpit-spark")).not.toBeNull();   // today sparkline
  });

  it("animates only the non-zero decision + live cards", () => {
    const { container } = render(<CockpitCards filter={null} onFilter={() => {}} metrics={metrics({
      decisions: { count: 1, oldestAge: "2m", highRisk: 0 }, liveCount: 2, agentsActive: 4,
    })} />);
    expect(container.querySelector(".cockpit-pulse")).not.toBeNull();   // decisions > 0
    expect(container.querySelector(".cockpit-flow")).not.toBeNull();    // live > 0
  });

  it("shows calm zero-state copy and no pulse when nothing is pending", () => {
    const { container } = render(<CockpitCards filter={null} onFilter={() => {}} metrics={metrics()} />);
    expect(screen.getByText("All clear")).toBeTruthy();    // decisions
    expect(screen.getByText("Idle")).toBeTruthy();         // live
    expect(screen.getByText("None")).toBeTruthy();         // failed
    expect(container.querySelector(".cockpit-pulse")).toBeNull();
    expect(container.querySelectorAll(".cockpit-card[data-zero]").length).toBe(4);
  });

  it("toggles the filter on click and marks the armed card", () => {
    const onFilter = vi.fn();
    const { container, rerender } = render(<CockpitCards filter={null} onFilter={onFilter} metrics={metrics({ liveCount: 1 })} />);

    fireEvent.click(screen.getByText("Live runs").closest("button")!);
    expect(onFilter).toHaveBeenCalledWith("live");

    rerender(<CockpitCards filter="live" onFilter={onFilter} metrics={metrics({ liveCount: 1 })} />);
    expect(container.querySelector('.cockpit-card[data-tone="live"][data-armed]')).not.toBeNull();
  });
});
