import { fireEvent, render, screen } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";

import type { RunRecordView, WorkflowRunDataCompletenessView } from "@/api/workflows";

// Drive the records through the hook; stub JsonView so the test asserts "the raw payload is shown" without its tree.
const recordsMock = {
  records: [] as RunRecordView[], runStatus: undefined, isLoading: false, isLoadingOlder: false, error: null as Error | null,
  hasOlder: false, olderRecordsOmitted: false, newerRecordsOmitted: false, atLatest: true,
  loadOlder: vi.fn(), returnToLatest: vi.fn(),
};
const completenessMock: { data: WorkflowRunDataCompletenessView | null | undefined; isLoading: boolean; error: Error | null } = { data: undefined, isLoading: false, error: null };
vi.mock("@/hooks/use-workflows", () => ({
  useRunRecordWindow: () => recordsMock,
  useRunDataCompleteness: () => completenessMock,
}));
vi.mock("./JsonView", () => ({
  JsonView: ({ data }: { data: unknown }) => <div data-testid="jsonview">{JSON.stringify(data)}</div>,
}));

import { RunTrace } from "./RunTrace";

function record(o: Partial<RunRecordView>): RunRecordView {
  return { sequence: 1, recordType: "run.started", nodeId: null, iterationKey: "", occurredAt: "2026-06-23T10:00:00Z", payloadJson: "{}", ...o };
}

function withRecords(records: RunRecordView[] | undefined, isLoading = false) {
  recordsMock.records = records ?? [];
  recordsMock.isLoading = isLoading;
}

function completeness(facets: WorkflowRunDataCompletenessView["facets"]): WorkflowRunDataCompletenessView {
  return { runId: "r1", scope: "RecordedFacetsOnly", facets, hasStatements: facets.length > 0, runWideVerdict: null, truncated: false };
}

beforeEach(() => {
  recordsMock.records = [];
  recordsMock.isLoading = false;
  recordsMock.isLoadingOlder = false;
  recordsMock.error = null;
  recordsMock.hasOlder = false;
  recordsMock.olderRecordsOmitted = false;
  recordsMock.newerRecordsOmitted = false;
  recordsMock.atLatest = true;
  recordsMock.loadOlder.mockReset();
  recordsMock.returnToLatest.mockReset();
  completenessMock.data = completeness([]);
  completenessMock.isLoading = false;
  completenessMock.error = null;
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

  it("fails closed instead of rendering an invalid first page as an empty ledger", () => {
    withRecords([], false);
    recordsMock.error = new Error("Invalid Workflow Run record page response.");

    render(<RunTrace runId="r1" />);

    expect(screen.getByText(/couldn't load the event ledger/i)).toBeInTheDocument();
    expect(screen.queryByText(/no records yet/i)).toBeNull();
  });

  it("shows recorded facets independently and never presents them as a run-wide verdict", () => {
    withRecords([record({ sequence: 1 })]);
    completenessMock.data = completeness([
      { facet: "harness-process-attempt", expectedRecordCount: 2, presentRecordCount: 1, knownMissingCount: 1, verdict: "Partial", isStrictlyReadable: false, revision: 4, schemaVersion: 1, lastModifiedAt: "2026-08-21T02:00:00Z" },
      { facet: "native-record", expectedRecordCount: 1, presentRecordCount: 1, knownMissingCount: 0, verdict: "Exact", isStrictlyReadable: true, revision: 2, schemaVersion: 1, lastModifiedAt: "2026-08-21T02:00:01Z" },
    ]);

    render(<RunTrace runId="r1" active />);

    expect(screen.getByText(/recorded facets only/i)).toBeInTheDocument();
    expect(screen.getByText(/no run-wide verdict/i)).toBeInTheDocument();
    expect(screen.getByText("harness-process-attempt")).toBeInTheDocument();
    expect(screen.getByText("Partial")).toBeInTheDocument();
    expect(screen.getByText(/1 present \/ 2 expected/i)).toBeInTheDocument();
    expect(screen.getByText("native-record")).toBeInTheDocument();
    expect(screen.getByText("Exact")).toBeInTheDocument();
  });

  it("keeps zero statements visibly unstated instead of calling the run exact", () => {
    withRecords([record({ sequence: 1 })]);

    render(<RunTrace runId="r1" />);

    expect(screen.getByText(/no producer has stated completeness/i)).toBeInTheDocument();
    expect(screen.queryByText(/all data complete/i)).toBeNull();
  });

  it("keeps the last valid statements visible when a live metadata refresh fails", () => {
    withRecords([record({ sequence: 1 })]);
    completenessMock.data = completeness([
      { facet: "native-record", expectedRecordCount: 1, presentRecordCount: 1, knownMissingCount: 0, verdict: "Exact", isStrictlyReadable: true, revision: 2, schemaVersion: 1, lastModifiedAt: "2026-08-21T02:00:01Z" },
    ]);
    completenessMock.error = new Error("transient");

    render(<RunTrace runId="r1" active />);

    expect(screen.getByText("native-record")).toBeInTheDocument();
    expect(screen.getByText(/last refresh failed/i)).toBeInTheDocument();
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

  it("makes bounded truncation explicit and offers Older without pretending the count is total", () => {
    withRecords([record({ sequence: 8 }), record({ sequence: 9 })]);
    recordsMock.hasOlder = true;
    recordsMock.olderRecordsOmitted = true;

    render(<RunTrace runId="r1" />);

    expect(screen.getByText(/showing 2 records/i)).toBeInTheDocument();
    expect(screen.getByText(/earlier records are outside this bounded window/i)).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: /load older/i }));
    expect(recordsMock.loadOlder).toHaveBeenCalledTimes(1);
  });

  it("offers Return to latest for a historical window and preserves valid rows on refresh failure", () => {
    withRecords([record({ sequence: 1 })]);
    recordsMock.atLatest = false;
    recordsMock.newerRecordsOmitted = true;
    recordsMock.error = new Error("poll failed");

    render(<RunTrace runId="r1" active />);

    expect(screen.getByText(/newer records are outside this historical window/i)).toBeInTheDocument();
    expect(screen.getByText(/last refresh failed/i)).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: /return to latest/i }));
    expect(recordsMock.returnToLatest).toHaveBeenCalledTimes(1);
    expect(screen.getByText("run.started")).toBeInTheDocument();
  });
});
