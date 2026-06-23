import { fireEvent, render, screen } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";

import type { RunRecordsResponse, RunRecordView } from "@/api/workflows";

// Drive the records through the hook; stub JsonView so the test asserts "the raw payload is shown" without its tree.
const recordsMock: { data: RunRecordsResponse | undefined; isLoading: boolean } = { data: undefined, isLoading: false };
vi.mock("@/hooks/use-workflows", () => ({
  useRunRecords: () => recordsMock,
}));
vi.mock("./JsonView", () => ({
  JsonView: ({ data }: { data: unknown }) => <div data-testid="jsonview">{JSON.stringify(data)}</div>,
}));

import { RunTrace } from "./RunTrace";

function record(o: Partial<RunRecordView>): RunRecordView {
  return { sequence: 1, recordType: "run.started", nodeId: null, iterationKey: "", occurredAt: "2026-06-23T10:00:00Z", payloadJson: "{}", ...o };
}

function withRecords(records: RunRecordView[] | undefined, isLoading = false) {
  recordsMock.data = records && { runId: "r1", runStatus: "Running", records };
  recordsMock.isLoading = isLoading;
}

beforeEach(() => {
  recordsMock.data = undefined;
  recordsMock.isLoading = false;
});

describe("RunTrace", () => {
  it("shows a loading state before the first fetch resolves", () => {
    withRecords(undefined, true);
    render(<RunTrace runId="r1" />);
    expect(screen.getByText(/loading the event ledger/i)).toBeInTheDocument();
  });

  it("shows an empty state when the run has no records", () => {
    withRecords([], false);
    render(<RunTrace runId="r1" />);
    expect(screen.getByText(/no records yet/i)).toBeInTheDocument();
  });

  it("renders every record's raw type verbatim, in order, including narrative-dropped types", () => {
    withRecords([
      record({ sequence: 1, recordType: "run.started" }),
      record({ sequence: 2, recordType: "scope.resolved" }),   // the narrative timeline drops this — Trace keeps it
      record({ sequence: 3, recordType: "log", nodeId: "code" }),
      record({ sequence: 4, recordType: "run.completed" }),
    ]);
    const { container } = render(<RunTrace runId="r1" />);

    const types = Array.from(container.querySelectorAll(".run-trace-type")).map((n) => n.textContent);
    expect(types).toEqual(["run.started", "scope.resolved", "log", "run.completed"]);
    expect(screen.getByText(/4 records/)).toBeInTheDocument();
  });

  it("expands a record with a non-trivial payload to its raw JSON", () => {
    withRecords([record({ sequence: 1, recordType: "node.failed", payloadJson: '{"error":"boom"}' })]);
    render(<RunTrace runId="r1" />);

    expect(screen.queryByTestId("jsonview")).toBeNull();
    fireEvent.click(screen.getByRole("button"));

    expect(screen.getByTestId("jsonview")).toHaveTextContent('"error":"boom"');
  });

  it("does not make an empty-payload record expandable, and renders it as a non-interactive row", () => {
    withRecords([record({ sequence: 1, recordType: "run.started", payloadJson: "{}" })]);
    const { container } = render(<RunTrace runId="r1" />);

    expect(container.querySelector(".run-trace-caret")).toBeNull();
    expect(container.querySelector(".run-trace-bar[data-expandable]")).toBeNull();
    expect(container.querySelector("button")).toBeNull();   // a flat row is a div, not a focusable no-op button
  });

  it("treats a bare-scalar payload as flat (only structured object/array payloads expand)", () => {
    withRecords([
      record({ sequence: 1, recordType: "llm.token", payloadJson: "42" }),
      record({ sequence: 2, recordType: "node.skipped", payloadJson: '"reason"' }),
    ]);
    const { container } = render(<RunTrace runId="r1" />);

    expect(container.querySelector(".run-trace-caret")).toBeNull();
    expect(container.querySelector("button")).toBeNull();
  });

  it("tones failure / cancel records for scanning, leaving others neutral", () => {
    withRecords([
      record({ sequence: 1, recordType: "node.failed" }),
      record({ sequence: 2, recordType: "run.cancelled" }),
      record({ sequence: 3, recordType: "node.completed" }),
    ]);
    const { container } = render(<RunTrace runId="r1" />);

    const tones = Array.from(container.querySelectorAll<HTMLElement>(".run-trace-row")).map((n) => n.dataset.tone);
    expect(tones).toEqual(["error", "error", undefined]);
  });
});
