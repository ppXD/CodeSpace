import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

import { WorkflowRunModelCallContent, type WorkflowRunModelCallTab } from "./WorkflowRunModelCallContent";

const callId = "11111111-1111-1111-1111-111111111111";
const firstAttemptId = "22222222-2222-2222-2222-222222222222";
const activeAttemptId = "33333333-3333-3333-3333-333333333333";

function json(body: unknown, status = 200) {
  return new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });
}

function projectedMetadata() {
  return {
    runId: "run-1",
    sequence: 42,
    workflowRunModelCallId: callId,
    projectionState: "Projected",
    captureCompleteness: "Exact",
    correlationId: null,
    status: "Completed",
    parts: [],
  };
}

function stableDetail(responseReferenceState = "Referenced", responseCompleteness = "Exact") {
  const attempt = (attemptId: string, attemptOrdinal: number, model: string, status: string) => ({
    attemptId,
    attemptOrdinal,
    effectiveProvider: "anthropic-compatible",
    effectiveModel: model,
    effectiveModelRowId: null,
    transportKind: "claude-code-cli",
    endpointFingerprint: "endpoint-a",
    providerRequestId: `provider-${attemptOrdinal}`,
    status,
    errorCode: status === "Failed" ? "gateway_timeout" : null,
    finishReason: status === "Completed" ? "end_turn" : null,
    httpStatusCode: status === "Completed" ? 200 : 504,
    captureSource: "interaction-ledger",
    captureCompleteness: attemptOrdinal === 2 ? responseCompleteness : "Exact",
    sourceEvidence: "StartedAndTerminal",
    sourceStartedRecordId: null,
    sourceTerminalRecordId: null,
    sourceEvidenceRevision: 1,
    usage: { inputTokens: 10, outputTokens: attemptOrdinal * 20, cacheReadTokens: 3, cacheWriteTokens: 0, reasoningTokens: 5 },
    costAmount: attemptOrdinal === 2 ? 0.42 : 0.12,
    costCurrency: "USD",
    pricingVersion: "v1",
    startedAt: "2026-08-15T01:00:00Z",
    firstTokenAt: "2026-08-15T01:00:01Z",
    completedAt: "2026-08-15T01:00:02Z",
    schemaVersion: 1,
    bodies: [
      { body: "AttemptRequest", attemptId, artifactId: "44444444-4444-4444-4444-444444444444", referenceState: "Referenced", captureCompleteness: "Exact" },
      { body: "AttemptResponse", attemptId, artifactId: responseReferenceState === "Referenced" ? "55555555-5555-5555-5555-555555555555" : null, referenceState: responseReferenceState, captureCompleteness: attemptOrdinal === 2 ? responseCompleteness : "Exact" },
      { body: "AttemptError", attemptId, artifactId: null, referenceState: "NotRecorded", captureCompleteness: "Exact" },
    ],
  });

  return {
    workflowRunModelCallId: callId,
    runId: "run-1",
    callOrdinal: 7,
    nodeId: "supervisor",
    iterationKey: "0",
    workPlanId: null,
    planVersion: null,
    workUnitId: null,
    workUnitContractHash: null,
    executionAttemptId: null,
    executionAttemptOrdinal: null,
    executionGeneration: 4,
    purpose: "supervisor.decision",
    requestedProvider: "auto",
    requestedModel: "requested-model",
    requestedModelRowId: null,
    selectionPolicy: "auto",
    sourceKind: "supervisor",
    sourceCorrelationId: null,
    captureSource: "interaction-ledger",
    captureCompleteness: responseCompleteness,
    schemaVersion: 1,
    createdAt: "2026-08-15T01:00:00Z",
    bodies: [{ body: "LogicalRequest", attemptId: null, artifactId: "66666666-6666-6666-6666-666666666666", referenceState: "Referenced", captureCompleteness: "Exact" }],
    attempts: [
      attempt(firstAttemptId, 1, "model-a", "Failed"),
      attempt(activeAttemptId, 2, "model-b", "Completed"),
    ],
  };
}

type FetchHandler = (url: URL, init: RequestInit) => Response | Promise<Response>;

function renderContent(handler: FetchHandler, tab: WorkflowRunModelCallTab = "result") {
  vi.stubGlobal("fetch", vi.fn((input: RequestInfo | URL, init: RequestInit = {}) => handler(new URL(String(input), "http://test.local"), init)));
  const client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
  const view = render(<QueryClientProvider client={client}><WorkflowRunModelCallContent runId="run-1" sequence={42} tab={tab} /></QueryClientProvider>);
  return {
    client,
    rerenderTab(next: WorkflowRunModelCallTab) {
      view.rerender(<QueryClientProvider client={client}><WorkflowRunModelCallContent runId="run-1" sequence={42} tab={next} /></QueryClientProvider>);
    },
  };
}

afterEach(() => vi.unstubAllGlobals());

describe("WorkflowRunModelCallContent", () => {
  it("uses the stable id reader, keeps bounded body pages local, and selects the latest physical attempt", async () => {
    const bodySignals: AbortSignal[] = [];
    const { client } = renderContent((url, init) => {
      if (url.pathname === "/api/workflows/runs/run-1/model-calls/42") return json(projectedMetadata());
      if (url.pathname === `/api/workflows/runs/run-1/model-calls/${callId}`) return json(stableDetail());
      if (url.pathname === `/api/workflows/runs/run-1/model-calls/${callId}/bodies/AttemptResponse`) {
        bodySignals.push(init.signal as AbortSignal);
        expect(url.searchParams.get("attemptId")).toBe(activeAttemptId);
        const offset = Number(url.searchParams.get("offsetBytes"));
        return json({
          body: "AttemptResponse",
          attemptId: activeAttemptId,
          captureCompleteness: "Exact",
          availability: "Available",
          text: offset === 0 ? "first stable chunk" : "second stable chunk",
          offsetBytes: offset,
          returnedBytes: 18,
          totalBytes: 36,
          nextOffsetBytes: offset === 0 ? 18 : null,
          contentType: "text/plain",
          artifactId: "55555555-5555-5555-5555-555555555555",
          integrityVerified: true,
          message: null,
        });
      }
      return json({ message: `Unexpected ${url.pathname}` }, 500);
    });

    expect(await screen.findByText("first stable chunk")).toBeInTheDocument();
    expect(screen.getByText(/logical #7/i)).toBeInTheDocument();
    expect(screen.getByRole("button", { name: /attempt 2.*model-b.*completed/i })).toHaveAttribute("aria-pressed", "true");
    expect(bodySignals[0]).toBeInstanceOf(AbortSignal);
    expect(JSON.stringify(client.getQueryCache().getAll().map((query) => query.state.data))).not.toContain("first stable chunk");
    expect(vi.mocked(globalThis.fetch).mock.calls.some(([input]) => String(input).includes("/parts/"))).toBe(false);
    expect(vi.mocked(globalThis.fetch).mock.calls.filter(([input]) => String(input).includes("/bodies/")).length).toBe(1);

    fireEvent.click(screen.getByRole("button", { name: "Load more" }));
    expect(await screen.findByText(/first stable chunksecond stable chunk/)).toBeInTheDocument();
    expect(vi.mocked(globalThis.fetch).mock.calls.some(([input]) => String(input).includes("offsetBytes=18"))).toBe(true);
  });

  it("keeps LegacyFallback on the sequence part reader and surfaces typed backend state", async () => {
    renderContent((url) => {
      if (url.pathname === "/api/workflows/runs/run-1/model-calls/42") return json({ ...projectedMetadata(), workflowRunModelCallId: null, projectionState: "LegacyFallback", captureCompleteness: "LegacyUnknown" });
      if (url.pathname === "/api/workflows/runs/run-1/model-calls/42/parts/Trace") return json({
        part: "Trace",
        availability: "BackendUnavailable",
        text: null,
        offsetBytes: 0,
        returnedBytes: 0,
        totalBytes: null,
        nextOffsetBytes: null,
        contentType: null,
        artifactId: null,
        integrityVerified: false,
        message: "The artifact backend is temporarily unavailable.",
      });
      return json({ message: `Unexpected ${url.pathname}` }, 500);
    }, "trace");

    expect(await screen.findByText(/Backend unavailable/i)).toBeInTheDocument();
    expect(screen.getByText(/temporarily unavailable/i)).toBeInTheDocument();
    expect(vi.mocked(globalThis.fetch).mock.calls.some(([input]) => String(input).includes(callId))).toBe(false);
  });

  it("does not read an untrusted body reference and shows partial/corrupt capture metadata", async () => {
    renderContent((url) => {
      if (url.pathname === "/api/workflows/runs/run-1/model-calls/42") return json({ ...projectedMetadata(), captureCompleteness: "Corrupt" });
      if (url.pathname === `/api/workflows/runs/run-1/model-calls/${callId}`) return json(stableDetail("Corrupt", "Corrupt"));
      return json({ message: "Body read must not occur" }, 500);
    });

    expect(await screen.findByText(/Body reference corrupt/i)).toBeInTheDocument();
    expect(screen.getAllByText(/capture corrupt/i).length).toBeGreaterThan(0);
    await waitFor(() => expect(vi.mocked(globalThis.fetch)).toHaveBeenCalledTimes(2));
  });

  it("aborts the body request when its tab unmounts", async () => {
    let bodySignal: AbortSignal | undefined;
    const { rerenderTab } = renderContent((url, init) => {
      if (url.pathname === "/api/workflows/runs/run-1/model-calls/42") return json(projectedMetadata());
      if (url.pathname === `/api/workflows/runs/run-1/model-calls/${callId}`) return json(stableDetail());
      if (url.pathname.endsWith("/bodies/AttemptResponse")) {
        bodySignal = init.signal as AbortSignal;
        return new Promise<Response>(() => undefined);
      }
      return json({ message: `Unexpected ${url.pathname}` }, 500);
    });

    await waitFor(() => expect(bodySignal).toBeInstanceOf(AbortSignal));
    rerenderTab("usage");
    await waitFor(() => expect(bodySignal?.aborted).toBe(true));
    expect(await screen.findByText("50 tokens")).toBeInTheDocument();
  });
});
