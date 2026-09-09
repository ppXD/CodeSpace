import { render, screen, within } from "@testing-library/react";
import { describe, expect, it } from "vitest";

import type { RunScorecardTrend } from "@/api/agents";
import { AgentLearningTrendView } from "./AgentLearningTrendPanel";

const trend: RunScorecardTrend = {
  since: "2026-08-12T00:00:00Z",
  scoredRuns: 12,
  buckets: [
    { day: "2026-09-06T00:00:00Z", runs: 4, solvedRuns: 3, deliveredRuns: 3, unattendedSolvedWithDeliveryRuns: 2, unattendedSolveWithDeliveryRate: 0.5, suspendedRuns: 1, legacyRuns: 0, costUsd: 1.2, brainPlaneUsd: 0.2 },
    { day: "2026-09-07T00:00:00Z", runs: 0, solvedRuns: 0, deliveredRuns: 0, unattendedSolvedWithDeliveryRuns: 0, unattendedSolveWithDeliveryRate: null, suspendedRuns: 2, legacyRuns: 1, costUsd: null, brainPlaneUsd: null },
  ],
  byLessonArm: [
    { arm: "injected", runs: 5, solvedRuns: 4, deliveredRuns: 4, unattendedSolvedWithDeliveryRuns: 3, unattendedSolveWithDeliveryRate: 0.6, humanTouchedRuns: 2, humanInterventionRate: 0.4, avgHumanTouches: 0.6, totalCostUsd: 4.5, unknownCostRuns: 1, avgCostPerPricedRunUsd: 1.125, brainPlaneUsd: 1.2, unknownBrainCostRuns: 2, avgBrainPlaneCostPerPricedRunUsd: 0.4 },
    { arm: "withheld", runs: 4, solvedRuns: 2, deliveredRuns: 2, unattendedSolvedWithDeliveryRuns: 1, unattendedSolveWithDeliveryRate: 0.25, humanTouchedRuns: 0, humanInterventionRate: 0, avgHumanTouches: 0, totalCostUsd: null, unknownCostRuns: 4, avgCostPerPricedRunUsd: null, brainPlaneUsd: null, unknownBrainCostRuns: 4, avgBrainPlaneCostPerPricedRunUsd: null },
    { arm: "unmeasured", runs: 3, solvedRuns: 1, deliveredRuns: 1, unattendedSolvedWithDeliveryRuns: 0, unattendedSolveWithDeliveryRate: 0, humanTouchedRuns: 1, humanInterventionRate: 1 / 3, avgHumanTouches: 1 / 3, totalCostUsd: 0, unknownCostRuns: 0, avgCostPerPricedRunUsd: 0, brainPlaneUsd: 0, unknownBrainCostRuns: 0, avgBrainPlaneCostPerPricedRunUsd: 0 },
  ],
};

describe("AgentLearningTrendView", () => {
  it("renders daily truth and every lesson arm from the endpoint without turning parked-only activity into zero percent", () => {
    render(<AgentLearningTrendView trend={trend} />);

    expect(screen.getByText("12 scored runs · since Aug 12")).toBeInTheDocument();
    expect(screen.getByText("50%")).toBeInTheDocument();
    expect(screen.getByText("Not measured")).toBeInTheDocument();
    expect(screen.getByText("2 parked · 1 legacy")).toBeInTheDocument();

    const injected = screen.getByText("Lessons injected").closest("tr");
    expect(injected).not.toBeNull();
    expect(within(injected!).getByText("60%")).toBeInTheDocument();
    expect(within(injected!).getByText("3/5")).toBeInTheDocument();
    expect(within(injected!).getByText("40%")).toBeInTheDocument();
    expect(within(injected!).getByText("0.6/run")).toBeInTheDocument();
    expect(within(injected!).getByText("$1.13")).toBeInTheDocument();
    expect(within(injected!).getByText("1 unknown")).toBeInTheDocument();
    expect(within(injected!).getByText("$0.40")).toBeInTheDocument();
    expect(within(injected!).getByText("2 unknown")).toBeInTheDocument();

    const withheld = screen.getByText("Lessons withheld").closest("tr");
    expect(withheld).not.toBeNull();
    expect(within(withheld!).getAllByText("Not priced")).toHaveLength(2);
    expect(within(withheld!).getAllByText("4 unknown")).toHaveLength(2);

    const unmeasured = screen.getByText("Outside experiment").closest("tr");
    expect(unmeasured).not.toBeNull();
    expect(within(unmeasured!).getAllByText("$0.00")).toHaveLength(2);
    expect(within(unmeasured!).queryByText(/unknown/)).not.toBeInTheDocument();
    expect(screen.getByText(/observational/i)).toBeInTheDocument();
  });

  it("distinguishes an empty measured window from a zero solve rate", () => {
    render(<AgentLearningTrendView trend={{ since: "2026-09-01T00:00:00Z", scoredRuns: 0, buckets: [], byLessonArm: [] }} />);

    expect(screen.getByText("No durable learning measurements in this window.")).toBeInTheDocument();
    expect(screen.queryByText("0%")).not.toBeInTheDocument();
  });
});
