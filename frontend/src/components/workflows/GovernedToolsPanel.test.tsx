import { act, fireEvent, render, screen } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";

import type { WorkflowRunToolCallDetail, WorkflowRunToolCallMetadata } from "@/api/workflows";

const { getDetail, useWindow } = vi.hoisted(() => ({ getDetail: vi.fn(), useWindow: vi.fn() }));
vi.mock("@/api/workflows", async (importOriginal) => {
  const original = await importOriginal<typeof import("@/api/workflows")>();
  return { ...original, workflowsApi: { ...original.workflowsApi, getRunToolCall: getDetail } };
});
vi.mock("@/hooks/use-governed-tool-call-window", async (importOriginal) => {
  const original = await importOriginal<typeof import("@/hooks/use-governed-tool-call-window")>();
  return { ...original, useGovernedToolCallWindow: useWindow };
});

import { GovernedToolsPanel } from "./GovernedToolsPanel";

function call(over: Partial<WorkflowRunToolCallMetadata> = {}): WorkflowRunToolCallMetadata {
  return {
    toolCallId: "22222222-2222-2222-2222-222222222222",
    runId: "11111111-1111-1111-1111-111111111111",
    toolAdapterKind: "governed-tool-call/v1",
    toolName: "git.open_pr",
    effectClass: "SideEffecting",
    state: "Completed",
    callOrdinal: 7,
    sourceKind: "tool-call-ledger/v1",
    sourceCorrelationId: "33333333-3333-3333-3333-333333333333",
    captureSource: "tool-call-ledger/v1",
    captureCompleteness: "Unavailable",
    createdAt: "2026-08-21T01:00:00Z",
    lastModifiedAt: "2026-08-21T01:01:00Z",
    terminalAt: "2026-08-21T01:01:00Z",
    errorCode: null,
    ...over,
  };
}

function detail(): WorkflowRunToolCallDetail {
  return {
    call: call(),
    attempts: [{
      attemptOrdinal: 1,
      status: "Indeterminate",
      captureSource: "tool-call-ledger/v1",
      captureCompleteness: "Unavailable",
      startedAt: "2026-08-21T01:00:00Z",
      completedAt: "2026-08-21T01:01:00Z",
      createdAt: "2026-08-21T01:00:00Z",
      lastModifiedAt: "2026-08-21T01:01:00Z",
      errorCode: "LedgerFailedOutcomeUnknown",
    }],
    attemptsTruncated: true,
  };
}

const returnToLatest = vi.fn();
const loadOlder = vi.fn();

function window(over: Record<string, unknown> = {}) {
  return {
    calls: [call()],
    isLoading: false,
    isLoadingOlder: false,
    error: null,
    hasOlder: false,
    olderCallsOmitted: false,
    newerCallsOmitted: false,
    atLatest: true,
    loadOlder,
    returnToLatest,
    ...over,
  };
}

beforeEach(() => {
  getDetail.mockReset();
  useWindow.mockReset();
  loadOlder.mockReset();
  returnToLatest.mockReset();
  useWindow.mockReturnValue(window());
});

describe("GovernedToolsPanel", () => {
  it("clearly scopes the feed and does not fetch detail until a row is selected", () => {
    render(<GovernedToolsPanel runId="11111111-1111-1111-1111-111111111111" active />);

    expect(screen.getByText("Terminal governed side effects only")).toBeInTheDocument();
    expect(screen.getByText(/CLI and native tool activity stays in each Agent terminal/i)).toBeInTheDocument();
    expect(screen.getByText("git.open_pr")).toBeInTheDocument();
    expect(screen.getByText(/Agent admission #7/)).toBeInTheDocument();
    expect(getDetail).not.toHaveBeenCalled();
    expect(useWindow).toHaveBeenCalledWith("11111111-1111-1111-1111-111111111111", true);
  });

  it("loads selected detail locally and renders only bounded metadata, typed error code and lower-bound timing", async () => {
    getDetail.mockResolvedValue(detail());
    const { container } = render(<GovernedToolsPanel runId="11111111-1111-1111-1111-111111111111" active={false} />);

    fireEvent.click(screen.getByRole("button", { name: /git.open_pr/ }));
    await act(async () => { await Promise.resolve(); });

    expect(getDetail).toHaveBeenCalledExactlyOnceWith(
      "11111111-1111-1111-1111-111111111111",
      "22222222-2222-2222-2222-222222222222",
      expect.any(AbortSignal),
    );
    expect(screen.getByText("Attempt 1")).toBeInTheDocument();
    expect(screen.getByText("LedgerFailedOutcomeUnknown")).toBeInTheDocument();
    expect(screen.getByText(/100 attempts shown at most; additional attempts were omitted/i)).toBeInTheDocument();
    expect(screen.getByText(/source admission lower-bound, not provider wire start/i)).toBeInTheDocument();
    expect(container.textContent).not.toMatch(/artifact|arguments|result body|endpoint|invocation|approval|idempotency/i);
  });

  it("shows explicit omission controls for old and locally evicted new rows", () => {
    useWindow.mockReturnValue(window({ hasOlder: true, olderCallsOmitted: true, newerCallsOmitted: true, atLatest: false }));
    render(<GovernedToolsPanel runId="11111111-1111-1111-1111-111111111111" active={false} />);

    expect(screen.getByText(/Older observations are omitted/i)).toBeInTheDocument();
    expect(screen.getByText(/Newer observations were omitted by the 512-row local window/i)).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Load older" }));
    fireEvent.click(screen.getByRole("button", { name: "Return to latest" }));
    expect(loadOlder).toHaveBeenCalledTimes(1);
    expect(returnToLatest).toHaveBeenCalledTimes(1);
  });

  it("keeps last valid rows visible with a refresh error and supports a manual retry", () => {
    useWindow.mockReturnValue(window({ error: new Error("backend unavailable") }));
    render(<GovernedToolsPanel runId="11111111-1111-1111-1111-111111111111" active />);

    expect(screen.getByText("git.open_pr")).toBeInTheDocument();
    expect(screen.getByText("backend unavailable")).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Retry latest" }));
    expect(returnToLatest).toHaveBeenCalledTimes(1);
  });

  it("keeps selected stable detail bound to its exact run/call when a latest-page poll no longer contains that row", async () => {
    getDetail.mockResolvedValue(detail());
    const view = render(<GovernedToolsPanel runId="11111111-1111-1111-1111-111111111111" active />);
    fireEvent.click(screen.getByRole("button", { name: /git.open_pr/ }));
    await act(async () => { await Promise.resolve(); });

    useWindow.mockReturnValue(window({ calls: [call({ toolCallId: "55555555-5555-5555-5555-555555555555", toolName: "git.push" })] }));
    view.rerender(<GovernedToolsPanel runId="11111111-1111-1111-1111-111111111111" active />);

    expect(screen.getByText("Attempt 1")).toBeInTheDocument();
    expect(screen.getByText("22222222-2222-2222-2222-222222222222")).toBeInTheDocument();
    expect(screen.getByText("git.push")).toBeInTheDocument();
    expect(getDetail).toHaveBeenCalledTimes(1);
  });

  it("aborts selected detail on identity switch/unmount and ignores stale replies", async () => {
    let resolveFirst!: (value: WorkflowRunToolCallDetail) => void;
    getDetail
      .mockImplementationOnce((_run: string, _call: string, signal: AbortSignal) => new Promise<WorkflowRunToolCallDetail>((resolve) => {
        expect(signal.aborted).toBe(false);
        resolveFirst = resolve;
      }))
      .mockResolvedValueOnce({ ...detail(), call: call({ runId: "44444444-4444-4444-4444-444444444444" }) });
    const view = render(<GovernedToolsPanel runId="11111111-1111-1111-1111-111111111111" active={false} />);
    fireEvent.click(screen.getByRole("button", { name: /git.open_pr/ }));
    const firstSignal = getDetail.mock.calls[0][2] as AbortSignal;

    useWindow.mockReturnValue(window({ calls: [call({ runId: "44444444-4444-4444-4444-444444444444" })] }));
    view.rerender(<GovernedToolsPanel runId="44444444-4444-4444-4444-444444444444" active={false} />);
    expect(firstSignal.aborted).toBe(true);
    fireEvent.click(screen.getByRole("button", { name: /git.open_pr/ }));
    await act(async () => { await Promise.resolve(); });
    expect(screen.getByText("Attempt 1")).toBeInTheDocument();

    await act(async () => resolveFirst(detail()));
    expect(screen.getByText("Attempt 1")).toBeInTheDocument();
    const secondSignal = getDetail.mock.calls[1][2] as AbortSignal;
    view.unmount();
    expect(secondSignal.aborted).toBe(true);
  });
});
