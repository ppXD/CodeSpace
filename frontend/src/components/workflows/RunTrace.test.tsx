import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";

import type { RunRecordPageItem, WorkflowRunDataCompletenessView } from "@/api/workflows";

const payloadApiMock = vi.hoisted(() => vi.fn());
vi.mock("@/api/workflows", async () => {
  const actual = await vi.importActual<typeof import("@/api/workflows")>("@/api/workflows");
  return { ...actual, workflowsApi: { ...actual.workflowsApi, readRunRecordPayloadRange: payloadApiMock } };
});

// Drive the records through the hook; stub JsonView so the test asserts "the raw payload is shown" without its tree.
const recordsMock = {
  records: [] as RunRecordPageItem[], runStatus: undefined, isLoading: false, isLoadingOlder: false, error: null as Error | null,
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

function record(o: Partial<RunRecordPageItem>): RunRecordPageItem {
  return { recordId: "11111111-1111-4111-8111-111111111111", sequence: 1, recordType: "run.started", nodeId: null, iterationKey: "", occurredAt: "2026-06-23T10:00:00Z", payloadState: "Deferred", payloadContentType: "application/json", correlationId: null, parentRecordId: null, ...o };
}

function withRecords(records: RunRecordPageItem[] | undefined, isLoading = false) {
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
  payloadApiMock.mockReset();
  payloadApiMock.mockResolvedValue({
    availability: "Available", bytes: new TextEncoder().encode("{}"), runId: "r1", recordId: "11111111-1111-4111-8111-111111111111",
    sequence: 1, offsetBytes: 0, nextOffsetBytes: null, totalBytes: 2, contentType: "application/json",
  });
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
    fireEvent.click(screen.getByRole("button", { name: /retry/i }));
    expect(recordsMock.returnToLatest).toHaveBeenCalledTimes(1);
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

  it("fetches no payload until expansion, then renders the exact bounded body", async () => {
    const bytes = new TextEncoder().encode('{"error":"boom"}');
    payloadApiMock.mockResolvedValueOnce({ availability: "Available", bytes, runId: "r1", recordId: "11111111-1111-4111-8111-111111111111", sequence: 1, offsetBytes: 0, nextOffsetBytes: null, totalBytes: bytes.byteLength, contentType: "application/json" });
    withRecords([record({ sequence: 1, recordType: "node.failed" })]);
    render(<RunTrace runId="r1" />);

    expect(payloadApiMock).not.toHaveBeenCalled();
    expect(screen.queryByTestId("jsonview")).toBeNull();
    fireEvent.click(screen.getByRole("button"));

    expect(await screen.findByTestId("jsonview")).toHaveTextContent('"error":"boom"');
    expect(payloadApiMock).toHaveBeenCalledWith("r1", "11111111-1111-4111-8111-111111111111", 1, 0, 64 * 1024, expect.any(AbortSignal));
  });

  it("never JSON-parses a small body until the complete zero-to-EOF range is present", async () => {
    payloadApiMock.mockResolvedValueOnce({
      availability: "Available", bytes: new TextEncoder().encode('{"a":'), runId: "r1", recordId: "11111111-1111-4111-8111-111111111111",
      sequence: 1, offsetBytes: 0, nextOffsetBytes: 5, totalBytes: 7, contentType: "application/json",
    }).mockResolvedValueOnce({
      availability: "Available", bytes: new TextEncoder().encode("1}"), runId: "r1", recordId: "11111111-1111-4111-8111-111111111111",
      sequence: 1, offsetBytes: 5, nextOffsetBytes: null, totalBytes: 7, contentType: "application/json",
    });
    withRecords([record({ sequence: 1 })]);
    render(<RunTrace runId="r1" />);

    fireEvent.click(screen.getByRole("button"));
    const more = await screen.findByRole("button", { name: /load more payload/i });
    expect(screen.queryByTestId("jsonview")).toBeNull();
    fireEvent.click(more);

    expect(await screen.findByTestId("jsonview")).toHaveTextContent('"a":1');
  });

  it("aborts and releases local payload bytes when the row closes", async () => {
    let signal: AbortSignal | undefined;
    payloadApiMock.mockImplementationOnce((_runId, _recordId, _sequence, _offset, _limit, observed: AbortSignal) => {
      signal = observed;
      return new Promise(() => {});
    });
    withRecords([record({ sequence: 1 })]);
    render(<RunTrace runId="r1" />);

    fireEvent.click(screen.getByRole("button"));
    await waitFor(() => expect(payloadApiMock).toHaveBeenCalledTimes(1));
    expect(signal?.aborted).toBe(false);
    fireEvent.click(screen.getByRole("button"));

    expect(signal?.aborted).toBe(true);
  });

  it("offers manual retry for transient transport but not for invalid wire", async () => {
    payloadApiMock.mockResolvedValueOnce({ availability: "BackendUnavailable", code: "transport_unavailable", isRetryable: true })
      .mockResolvedValueOnce({ availability: "InvalidResponse", code: "invalid_record_payload_range_headers", isRetryable: false });
    withRecords([record({ sequence: 1 })]);
    render(<RunTrace runId="r1" />);

    fireEvent.click(screen.getByRole("button"));
    expect(await screen.findByText(/payload is temporarily unavailable/i)).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: /retry payload/i }));

    expect(await screen.findByText(/payload response was invalid/i)).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: /retry payload/i })).toBeNull();
    expect(payloadApiMock).toHaveBeenCalledTimes(2);
  });

  it("caps one local payload window at eight 64 KiB pages and makes continuation explicit", async () => {
    let call = 0;
    payloadApiMock.mockImplementation(async (_runId, _recordId, _sequence, offset: number) => {
      call += 1;
      const chunk = new Uint8Array(64 * 1024).fill(120);
      if (call === 8) chunk[chunk.length - 1] = 0xe7;
      if (call === 9) {
        chunk[0] = 0x95;
        chunk[1] = 0x8c;
      }
      return {
        availability: "Available", bytes: chunk, runId: "r1", recordId: "11111111-1111-4111-8111-111111111111", sequence: 1,
        offsetBytes: offset, nextOffsetBytes: offset + chunk.byteLength, totalBytes: 1024 * 1024, contentType: "application/json",
      };
    });
    withRecords([record({ sequence: 1 })]);
    render(<RunTrace runId="r1" />);

    fireEvent.click(screen.getByRole("button"));
    for (let page = 1; page < 8; page += 1) {
      await screen.findByRole("button", { name: /load more payload/i });
      fireEvent.click(screen.getByRole("button", { name: /load more payload/i }));
    }

    expect(await screen.findByText(/bounded preview window/i)).toBeInTheDocument();
    expect(payloadApiMock).toHaveBeenCalledTimes(8);
    fireEvent.click(screen.getByRole("button", { name: /next payload window/i }));
    await waitFor(() => expect(payloadApiMock).toHaveBeenLastCalledWith("r1", "11111111-1111-4111-8111-111111111111", 1, 512 * 1024, 64 * 1024, expect.any(AbortSignal)));
    expect(payloadApiMock).toHaveBeenCalledTimes(9);
    expect(await screen.findByText(/界/)).toBeInTheDocument();
    expect(screen.getByText(/continuation begun before this window/i)).toBeInTheDocument();
    expect(screen.queryByText(/�/)).toBeNull();
  });

  it("does not JSON-parse a partial body and decodes split UTF-8 without replacement characters", async () => {
    const totalBytes = 1024 * 1024;
    payloadApiMock.mockResolvedValueOnce({
      availability: "Available", bytes: new Uint8Array([0xe7]), runId: "r1", recordId: "11111111-1111-4111-8111-111111111111",
      sequence: 1, offsetBytes: 0, nextOffsetBytes: 1, totalBytes, contentType: "application/json",
    }).mockResolvedValueOnce({
      availability: "Available", bytes: new Uint8Array([0x95, 0x8c]), runId: "r1", recordId: "11111111-1111-4111-8111-111111111111",
      sequence: 1, offsetBytes: 1, nextOffsetBytes: 3, totalBytes, contentType: "application/json",
    });
    withRecords([record({ sequence: 1 })]);
    render(<RunTrace runId="r1" />);

    fireEvent.click(screen.getByRole("button"));
    const more = await screen.findByRole("button", { name: /load more payload/i });
    expect(screen.queryByTestId("jsonview")).toBeNull();
    fireEvent.click(more);

    expect(await screen.findByText("界")).toBeInTheDocument();
    expect(screen.queryByText(/�/)).toBeNull();
    expect(screen.queryByTestId("jsonview")).toBeNull();
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
