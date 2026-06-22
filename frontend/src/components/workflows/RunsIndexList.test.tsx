import { fireEvent, render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

import type { WorkflowRunStatus, WorkflowRunSummary } from "@/api/workflows";

import { RunsZones } from "./RunsIndexList";

function run(o: Partial<WorkflowRunSummary> & { id: string; status: WorkflowRunStatus }): WorkflowRunSummary {
  return {
    workflowId: "w", workflowVersion: 1, sourceType: "manual", error: null,
    startedAt: null, completedAt: null, createdDate: "2026-06-22T00:00:00Z", ...o,
  };
}

describe("RunsZones", () => {
  it("renders only the non-empty zones, each with its count", () => {
    const { container } = render(<RunsZones
      runs={[run({ id: "a", status: "Running" }), run({ id: "b", status: "Suspended" }), run({ id: "c", status: "Success" })]}
      nameById={new Map()} onOpen={() => {}} />);

    // one of each status → all three zones present
    expect(screen.getByText("Needs attention")).toBeTruthy();
    expect(screen.getByText("Live")).toBeTruthy();
    expect(screen.getByText("Recent")).toBeTruthy();
    expect(container.querySelectorAll(".runs-zone").length).toBe(3);
    expect(container.querySelectorAll(".runs-row").length).toBe(3);
  });

  it("hides a zone with no runs", () => {
    render(<RunsZones runs={[run({ id: "a", status: "Success" })]} nameById={new Map()} onOpen={() => {}} />);
    expect(screen.queryByText("Needs attention")).toBeNull();   // no Suspended run
    expect(screen.queryByText("Live")).toBeNull();              // nothing in flight
    expect(screen.getByText("Recent")).toBeTruthy();
  });

  it("titles a row by its workflow name, falling back to the source label for a task run", () => {
    const { container } = render(<RunsZones
      runs={[run({ id: "a", status: "Success", workflowId: "wf1" }), run({ id: "b", status: "Success", workflowId: null as unknown as string, sourceType: "snapshot" })]}
      nameById={new Map([["wf1", "Deploy pipeline"]])} onOpen={() => {}} />);

    const titles = [...container.querySelectorAll(".runs-row-title")].map((e) => e.textContent);
    expect(titles).toEqual(["Deploy pipeline", "Snapshot"]);   // resolved name, then null-workflow → source label
  });

  it("opens the clicked run", () => {
    const onOpen = vi.fn();
    const { container } = render(<RunsZones runs={[run({ id: "run-123", status: "Running" })]} nameById={new Map()} onOpen={onOpen} />);

    fireEvent.click(container.querySelector(".runs-row")!);
    expect(onOpen).toHaveBeenCalledWith("run-123");
  });
});
