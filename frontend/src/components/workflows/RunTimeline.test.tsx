import { render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

import type { RunTimelineEvent, RunTimelineResponse } from "@/api/workflows";

// The component reads its events through this hook; stub it so the test drives the event list directly.
const useRunTimeline = vi.fn();
vi.mock("@/hooks/use-workflows", () => ({
  useRunTimeline: (runId: string | null) => useRunTimeline(runId),
}));

import { RunTimeline } from "./RunTimeline";

function event(o: Partial<RunTimelineEvent>): RunTimelineEvent {
  return {
    id: "e", kind: "run.started", title: "Run started", severity: "Info",
    occurredAt: "2026-06-22T10:00:00Z", sourceKey: "run-record", ...o,
  };
}

/** Drive the mocked hook with a given events array (undefined = still loading). */
function withEvents(events: RunTimelineEvent[] | undefined) {
  const data: RunTimelineResponse | undefined = events && { runId: "run-1", runStatus: "Running", events };
  useRunTimeline.mockReturnValue({ data });
}

const rows = (c: HTMLElement) => Array.from(c.querySelectorAll<HTMLElement>(".run-timeline-event"));

describe("RunTimeline", () => {
  it("renders nothing while the run has produced no events yet", () => {
    withEvents([]);
    const { container } = render(<RunTimeline runId="run-1" />);
    expect(container.querySelector(".run-timeline")).toBeNull();
  });

  it("renders nothing before the first fetch resolves (no data)", () => {
    withEvents(undefined);
    const { container } = render(<RunTimeline runId="run-1" />);
    expect(container.querySelector(".run-timeline")).toBeNull();
  });

  it("renders one row per event, preserving the server's order", () => {
    withEvents([
      event({ id: "a", title: "Run started" }),
      event({ id: "b", title: "Node implement completed", severity: "Success" }),
      event({ id: "c", title: "Run completed", severity: "Success" }),
    ]);
    const { container } = render(<RunTimeline runId="run-1" />);

    const titles = Array.from(container.querySelectorAll(".run-timeline-title")).map((n) => n.textContent);
    expect(titles).toEqual(["Run started", "Node implement completed", "Run completed"]);
    expect(rows(container)).toHaveLength(3);
  });

  it("tones each row by its severity (the dot's render axis), lowercased", () => {
    withEvents([
      event({ id: "a", severity: "Info" }),
      event({ id: "b", severity: "Success" }),
      event({ id: "c", severity: "Warning" }),
      event({ id: "d", severity: "Error" }),
    ]);
    const { container } = render(<RunTimeline runId="run-1" />);

    expect(rows(container).map((r) => r.dataset.severity)).toEqual(["info", "success", "warning", "error"]);
  });

  it("shows an event's summary when present and omits it otherwise", () => {
    withEvents([
      event({ id: "a", title: "Node failed", severity: "Error", summary: "tests did not pass" }),
      event({ id: "b", title: "Run started" }),
    ]);
    const { container } = render(<RunTimeline runId="run-1" />);

    expect(screen.getByText("tests did not pass")).toBeInTheDocument();
    expect(container.querySelectorAll(".run-timeline-summary")).toHaveLength(1);
  });

  it("passes the runId straight through to the hook", () => {
    withEvents([event({})]);
    render(<RunTimeline runId="run-xyz" />);
    expect(useRunTimeline).toHaveBeenCalledWith("run-xyz");
  });
});
