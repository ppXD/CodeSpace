import { render, screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";

import type { JournalObservationCoverage } from "@/api/sessions";
import { JournalObservationCoverageWarnings } from "./SessionRoomView";

const coverage = (over: Partial<JournalObservationCoverage> = {}): JournalObservationCoverage => ({
  sourceKind: "supervisor-plan-page/v1",
  reason: "OlderItemsOmitted",
  observedCount: 500,
  omittedCount: 1,
  omittedCountIsLowerBound: true,
  decisionId: "00000000-0000-0000-0000-000000000001",
  storyOrder: "501",
  ...over,
});

describe("JournalObservationCoverageWarnings", () => {
  it("renders omitted and unavailable states instead of an empty Plan", () => {
    render(<JournalObservationCoverageWarnings coverage={[
      coverage(),
      coverage({ sourceKind: "supervisor-plan-subtasks/v1", reason: "InvalidLeaf", observedCount: 0, omittedCount: 4, omittedCountIsLowerBound: false }),
    ]} />);

    expect(screen.getByText("Plan history partially available · showing 500; at least 1 older omitted")).toBeInTheDocument();
    expect(screen.getByText("Plan subtasks unavailable · recorded data is invalid")).toBeInTheDocument();
  });

  it("renders nothing for the healthy absent field and keeps unknown states visible", () => {
    const { container, rerender } = render(<JournalObservationCoverageWarnings />);
    expect(container).toBeEmptyDOMElement();

    rerender(<JournalObservationCoverageWarnings coverage={[coverage({ reason: "FutureCoverage" })]} />);
    expect(screen.getByText("Plan history unavailable · unknown coverage state")).toBeInTheDocument();
  });

  it("fails closed instead of crashing on a malformed collection or item", () => {
    const malformed = { nope: true } as unknown as JournalObservationCoverage[];
    const { rerender } = render(<JournalObservationCoverageWarnings coverage={malformed} />);
    expect(screen.getByText("Plan observation unavailable · invalid coverage metadata")).toBeInTheDocument();

    rerender(<JournalObservationCoverageWarnings coverage={[null as unknown as JournalObservationCoverage]} />);
    expect(screen.getByText("Plan observation unavailable · invalid coverage metadata")).toBeInTheDocument();

    rerender(<JournalObservationCoverageWarnings coverage={[coverage(), coverage(), coverage(), coverage()]} />);
    expect(screen.getByText("Plan observation unavailable · invalid coverage metadata")).toBeInTheDocument();

    rerender(<JournalObservationCoverageWarnings coverage={[coverage({ sourceKind: "x".repeat(101) })]} />);
    expect(screen.getByText("Plan observation unavailable · invalid coverage metadata")).toBeInTheDocument();
  });
});
