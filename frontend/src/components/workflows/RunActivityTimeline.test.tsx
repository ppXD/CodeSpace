import { render, screen } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";

import type { RunPhase, RunTimelineEvent } from "@/api/workflows";

const { useRunTimelineMock, useRunPhasesMock } = vi.hoisted(() => ({
  useRunTimelineMock: vi.fn(),
  useRunPhasesMock: vi.fn(),
}));
vi.mock("@/hooks/use-workflows", () => ({
  useRunTimeline: () => useRunTimelineMock(),
  useRunPhases: () => useRunPhasesMock(),
}));
// Stub the wave (it pulls per-agent hooks) so this test isolates the MERGE + render order.
vi.mock("./AgentWave", () => ({
  AgentWave: ({ wave }: { wave: { label: string } }) => <div data-testid="wave">{wave.label}</div>,
}));

import { RunActivityTimeline } from "./RunActivityTimeline";

function phase(o: Partial<RunPhase> & { id: string }): RunPhase {
  return { label: o.id, kind: "node", status: "Active", order: 0, agents: [], metrics: { agentCount: 0, succeededCount: 0, failedCount: 0 }, sourceKey: "node-summary", ...o };
}
function event(o: Partial<RunTimelineEvent> & { id: string; occurredAt: string }): RunTimelineEvent {
  return { kind: "x", title: o.id, severity: "Info", sourceKey: "run-record", ...o };
}

/** The ordered labels of the rendered stream — an event's title, or a stubbed wave's label. */
function rowLabels(c: HTMLElement): string[] {
  return Array.from(c.querySelectorAll(".run-activity-list > li")).map(
    (li) => li.querySelector(".run-activity-title")?.textContent ?? li.querySelector('[data-testid="wave"]')?.textContent ?? "?",
  );
}

beforeEach(() => {
  useRunTimelineMock.mockReturnValue({ data: undefined, isLoading: false });
  useRunPhasesMock.mockReturnValue({ data: undefined, isLoading: false });
});

describe("RunActivityTimeline", () => {
  it("shows a loading state before the first fetch resolves", () => {
    useRunTimelineMock.mockReturnValue({ data: undefined, isLoading: true });
    render(<RunActivityTimeline runId="r1" />);
    expect(screen.getByText(/loading the run/i)).toBeInTheDocument();
  });

  it("shows an empty state once loaded with nothing", () => {
    render(<RunActivityTimeline runId="r1" />);
    expect(screen.getByText(/no activity yet/i)).toBeInTheDocument();
  });

  it("interleaves an agent wave between the events at its phase startedAt", () => {
    useRunTimelineMock.mockReturnValue({
      data: { runId: "r1", runStatus: "Running", events: [
        event({ id: "Run started", occurredAt: "2026-06-23T10:00:00Z" }),
        event({ id: "code edited", occurredAt: "2026-06-23T10:00:05Z", agentRunId: "a1" }),
      ] },
      isLoading: false,
    });
    useRunPhasesMock.mockReturnValue({
      data: { runId: "r1", runStatus: "Running", phases: [
        phase({ id: "p", label: "Implement", kind: "phase", order: 0, startedAt: "2026-06-23T10:00:02Z", agents: [{ agentRunId: "a1", status: "Running" }] }),
      ] },
      isLoading: false,
    });

    const { container } = render(<RunActivityTimeline runId="r1" />);

    expect(rowLabels(container)).toEqual(["Run started", "Implement", "code edited"]);
  });

  it("renders a pure-lifecycle run as events only (no waves)", () => {
    useRunTimelineMock.mockReturnValue({
      data: { runId: "r1", runStatus: "Success", events: [
        event({ id: "Run started", occurredAt: "2026-06-23T10:00:00Z" }),
        event({ id: "Run completed", occurredAt: "2026-06-23T10:00:01Z" }),
      ] },
      isLoading: false,
    });
    useRunPhasesMock.mockReturnValue({ data: { runId: "r1", runStatus: "Success", phases: [phase({ id: "end", label: "end" })] }, isLoading: false });

    const { container } = render(<RunActivityTimeline runId="r1" />);

    expect(rowLabels(container)).toEqual(["Run started", "Run completed"]);
    expect(container.querySelector('[data-testid="wave"]')).toBeNull();
  });
});
