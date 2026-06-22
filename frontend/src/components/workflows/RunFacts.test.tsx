import { render, screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";

import type { WorkflowRunDetail } from "@/api/workflows";

import { RunFacts } from "./RunFacts";

function run(o: Partial<WorkflowRunDetail>): WorkflowRunDetail {
  return {
    id: "run-1", workflowId: "wf", workflowVersion: 3, sourceType: "manual", normalizedPayload: {},
    status: "Success", error: null, startedAt: null, completedAt: null, createdDate: "2026-06-22T00:00:00Z", nodes: [], ...o,
  };
}

describe("RunFacts", () => {
  it("title-cases the source and shows the release version", () => {
    render(<RunFacts run={run({ sourceType: "webhook", workflowVersion: 7 })} />);
    expect(screen.getByText("Webhook")).toBeTruthy();
    expect(screen.getByText("v7")).toBeTruthy();
  });

  it("renders the wall-clock duration (createdDate→completedAt) for a completed run", () => {
    render(<RunFacts run={run({ createdDate: "2026-06-22T00:00:00Z", completedAt: "2026-06-22T00:01:30Z" })} />);
    expect(screen.getByText("1m 30s")).toBeTruthy();
  });
});
