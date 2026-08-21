import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

import type { WorkflowRunCaptureCompleteness, WorkflowRunModelCallAttemptMetadata, WorkflowRunModelCallBodyCaptureHealth, WorkflowRunModelCallBodyReferenceState, WorkflowRunModelCallDetailMetadata } from "@/api/workflows";

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

function stableDetail(responseReferenceState: WorkflowRunModelCallBodyReferenceState = "Referenced",
  responseCompleteness: WorkflowRunCaptureCompleteness = "Exact"): WorkflowRunModelCallDetailMetadata {
  const attempt = (attemptId: string, attemptOrdinal: number, model: string, status: string): WorkflowRunModelCallAttemptMetadata => ({
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
    unavailableFigures: [],
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

function exactLedgerDetail(responseReferenceState: WorkflowRunModelCallBodyReferenceState = "Partial") {
  const detail = stableDetail(responseReferenceState, "Partial");
  detail.sourceKind = "workflow-run-record/v1";
  detail.bodies = [{ body: "LogicalRequest", attemptId: null, artifactId: null, referenceState: "Partial", captureCompleteness: "Partial" }];
  detail.attempts = [detail.attempts[1]];
  detail.attempts[0].sourceStartedRecordId = "66666666-6666-6666-6666-666666666666";
  detail.attempts[0].sourceTerminalRecordId = "77777777-7777-7777-7777-777777777777";
  detail.attempts[0].bodies = [
    { body: "AttemptRequest", attemptId: activeAttemptId, artifactId: null, referenceState: "Partial", captureCompleteness: "Partial" },
    { body: "AttemptResponse", attemptId: activeAttemptId, artifactId: null, referenceState: responseReferenceState, captureCompleteness: "Partial", captureHealth: "Retry", materializationFormat: null },
    { body: "AttemptError", attemptId: activeAttemptId, artifactId: null, referenceState: "Partial", captureCompleteness: "Partial" },
  ];
  return detail;
}

function activeResponse(detail: WorkflowRunModelCallDetailMetadata) {
  return detail.attempts[1].bodies.find((body) => body.body === "AttemptResponse")!;
}

type FetchHandler = (url: URL, init: RequestInit) => Response | Promise<Response>;

function renderContent(handler: FetchHandler, tab: WorkflowRunModelCallTab = "result") {
  vi.stubGlobal("fetch", vi.fn((input: RequestInfo | URL, init: RequestInit = {}) => handler(new URL(String(input), "http://test.local"), init)));
  const client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
  const view = render(<QueryClientProvider client={client}><WorkflowRunModelCallContent runId="run-1" sequence={42} tab={tab} /></QueryClientProvider>);
  return {
    client,
    unmount: view.unmount,
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
    expect(screen.getByText("Capture health not reported")).toBeInTheDocument();
    expect(screen.getByText("materialization format not reported")).toBeInTheDocument();

    fireEvent.click(screen.getByRole("button", { name: "Load more" }));
    expect(await screen.findByText("second stable chunk")).toBeInTheDocument();
    expect(document.querySelector(".room-mcpre")).toHaveTextContent("first stable chunksecond stable chunk");
    expect(vi.mocked(globalThis.fetch).mock.calls.some(([input]) => String(input).includes("offsetBytes=18"))).toBe(true);
  });

  it.each([
    ["Pending", "Capture pending", "retryable", "materialization format pending"],
    ["Materializing", "Capture materializing", "retryable after lease expiry", "materialization format pending"],
    ["Retry", "Capture retry scheduled", "retryable", "materialization format pending"],
    ["Failed", "Capture failed", "not retryable (retry exhausted)", "materialization format unavailable"],
    ["Abandoned", "Capture abandoned", "not retryable", "materialization format unavailable"],
  ] satisfies Array<[WorkflowRunModelCallBodyCaptureHealth, string, string, string]>)("renders %s capture health from stable metadata and never reads contradictory referenced bytes", async (health, label, retryability, format) => {
    const detail = stableDetail();
    const response = activeResponse(detail);
    response.captureHealth = health;
    response.materializationFormat = null;
    renderContent((url) => {
      if (url.pathname === "/api/workflows/runs/run-1/model-calls/42") return json(projectedMetadata());
      if (url.pathname === `/api/workflows/runs/run-1/model-calls/${callId}`) return json(detail);
      return json({ message: "Body read must not occur for a non-available capture" }, 500);
    });

    expect(await screen.findByText(label)).toBeInTheDocument();
    expect(screen.getByText(retryability)).toBeInTheDocument();
    expect(screen.getByText(format)).toBeInTheDocument();
    await waitFor(() => expect(vi.mocked(globalThis.fetch)).toHaveBeenCalledTimes(2));
  });

  it.each([
    ["external-artifact/v1", "format external artifact (external-artifact/v1)"],
    ["utf8-string-envelope/v1", "format UTF-8 text envelope (utf8-string-envelope/v1)"],
    ["json-envelope/v1", "format JSON envelope (json-envelope/v1)"],
  ])("renders Available with closed format %s and reads only the selected body", async (materializationFormat, formatLabel) => {
    const detail = stableDetail();
    const response = activeResponse(detail);
    response.captureHealth = "Available";
    response.materializationFormat = materializationFormat;
    const bodyText = `materialized ${materializationFormat}`;
    renderContent((url) => {
      if (url.pathname === "/api/workflows/runs/run-1/model-calls/42") return json(projectedMetadata());
      if (url.pathname === `/api/workflows/runs/run-1/model-calls/${callId}`) return json(detail);
      if (url.pathname.endsWith("/bodies/AttemptResponse")) return json({
        body: "AttemptResponse", attemptId: activeAttemptId, captureCompleteness: "Exact", availability: "Available",
        text: bodyText, offsetBytes: 0, returnedBytes: bodyText.length, totalBytes: bodyText.length, nextOffsetBytes: null,
        contentType: "text/plain", artifactId: response.artifactId, integrityVerified: true, message: null,
      });
      return json({ message: `Unexpected ${url.pathname}` }, 500);
    });

    expect(await screen.findByText(bodyText)).toBeInTheDocument();
    expect(screen.getByText("Capture available")).toBeInTheDocument();
    expect(screen.getByText("complete")).toBeInTheDocument();
    expect(screen.getByText(formatLabel)).toBeInTheDocument();
    expect(vi.mocked(globalThis.fetch).mock.calls.filter(([input]) => String(input).includes("/bodies/")).length).toBe(1);
  });

  it.each([
    ["FutureCaptureState", "utf8-string-envelope/v1", "Unsupported capture health"],
    ["Available", "future-envelope/v2", "Unsupported materialization format"],
    ["Available", null, "Available capture is missing its materialization format"],
    ["Pending", "utf8-string-envelope/v1", "Non-available capture reported a materialization format"],
    [null, "external-artifact/v1", "Materialization format reported without capture health"],
  ])("fails closed on invalid stable capture metadata (%s, %s)", async (health, format, message) => {
    const detail = stableDetail();
    const response = activeResponse(detail);
    response.captureHealth = health as WorkflowRunModelCallBodyCaptureHealth | null;
    response.materializationFormat = format;
    renderContent((url) => {
      if (url.pathname === "/api/workflows/runs/run-1/model-calls/42") return json(projectedMetadata());
      if (url.pathname === `/api/workflows/runs/run-1/model-calls/${callId}`) return json(detail);
      return json({ message: "Body read must fail closed" }, 500);
    });

    expect(await screen.findByText(message)).toBeInTheDocument();
    await waitFor(() => expect(vi.mocked(globalThis.fetch)).toHaveBeenCalledTimes(2));
  });

  it("pages past 50k-token scale, bounds a 128k-token-equivalent DOM window, and can restart at byte zero", async () => {
    const pageBytes = 64 * 1024;
    const totalBytes = 9 * pageBytes;
    const pageText = (page: number) => `page-${page}:`.padEnd(pageBytes, "x");
    const visiblePagePrefixes = () => [...document.querySelectorAll(".room-mcchunk")].map((element) => element.textContent?.slice(0, 7));
    const { client } = renderContent((url) => {
      if (url.pathname === "/api/workflows/runs/run-1/model-calls/42") return json(projectedMetadata());
      if (url.pathname === `/api/workflows/runs/run-1/model-calls/${callId}`) return json(stableDetail());
      if (url.pathname.endsWith("/bodies/AttemptResponse")) {
        const offset = Number(url.searchParams.get("offsetBytes"));
        const nextOffset = offset + pageBytes;
        return json({
          body: "AttemptResponse",
          attemptId: activeAttemptId,
          captureCompleteness: "Exact",
          availability: "Available",
          text: pageText(offset / pageBytes),
          offsetBytes: offset,
          returnedBytes: pageBytes,
          totalBytes,
          nextOffsetBytes: nextOffset < totalBytes ? nextOffset : null,
          contentType: "text/plain",
          artifactId: "55555555-5555-5555-5555-555555555555",
          integrityVerified: true,
          message: null,
        });
      }
      return json({ message: `Unexpected ${url.pathname}` }, 500);
    });

    await waitFor(() => expect(visiblePagePrefixes()).toEqual(["page-0:"]));
    for (let page = 1; page <= 7; page++) {
      fireEvent.click(screen.getByRole("button", { name: "Load more" }));
      await waitFor(() => expect(visiblePagePrefixes()).toContain(`page-${page}:`));
      if (page === 3) expect(screen.getByText(/Showing bytes 0–262,144 of 589,824/)).toBeInTheDocument();
    }

    expect(screen.getByText(/Showing bytes 0–524,288 of 589,824/)).toBeInTheDocument();
    expect(document.querySelectorAll(".room-mcchunk")).toHaveLength(8);
    expect(JSON.stringify(client.getQueryCache().getAll().map((query) => query.state.data))).not.toContain("page-0:");
    fireEvent.click(screen.getByRole("button", { name: "Load more" }));
    await waitFor(() => expect(visiblePagePrefixes()).toContain("page-8:"));
    expect(visiblePagePrefixes()).not.toContain("page-0:");
    expect(document.querySelectorAll(".room-mcchunk")).toHaveLength(8);
    expect(screen.getByText(/Showing bytes 65,536–589,824 of 589,824/)).toBeInTheDocument();
    expect(screen.getByText(/Earlier bytes were removed from this view/i)).toBeInTheDocument();

    fireEvent.click(screen.getByRole("button", { name: "Start over" }));
    await waitFor(() => expect(visiblePagePrefixes()).toEqual(["page-0:"]));
    expect(screen.getByText(/Showing bytes 0–65,536 of 589,824/)).toBeInTheDocument();
    expect(screen.queryByText(/Earlier bytes were removed from this view/i)).toBeNull();
  });

  it("aborts and rejects a stale physical-attempt body response after selection changes", async () => {
    let staleSignal: AbortSignal | undefined;
    let resolveStale!: (response: Response) => void;
    const staleResponse = new Promise<Response>((resolve) => { resolveStale = resolve; });
    renderContent((url, init) => {
      if (url.pathname === "/api/workflows/runs/run-1/model-calls/42") return json(projectedMetadata());
      if (url.pathname === `/api/workflows/runs/run-1/model-calls/${callId}`) return json(stableDetail());
      if (url.pathname.endsWith("/bodies/AttemptResponse")) {
        const attemptId = url.searchParams.get("attemptId");
        if (attemptId === activeAttemptId) {
          staleSignal = init.signal as AbortSignal;
          return staleResponse;
        }
        return json({
          body: "AttemptResponse",
          attemptId: firstAttemptId,
          captureCompleteness: "Exact",
          availability: "Available",
          text: "selected attempt one",
          offsetBytes: 0,
          returnedBytes: 20,
          totalBytes: 20,
          nextOffsetBytes: null,
          contentType: "text/plain",
          artifactId: "55555555-5555-5555-5555-555555555555",
          integrityVerified: true,
          message: null,
        });
      }
      return json({ message: `Unexpected ${url.pathname}` }, 500);
    });

    await waitFor(() => expect(staleSignal).toBeInstanceOf(AbortSignal));
    fireEvent.click(screen.getByRole("button", { name: /attempt 1.*model-a.*failed/i }));
    expect(await screen.findByText("selected attempt one")).toBeInTheDocument();
    expect(staleSignal?.aborted).toBe(true);

    resolveStale(json({
      body: "AttemptResponse",
      attemptId: activeAttemptId,
      captureCompleteness: "Exact",
      availability: "Available",
      text: "stale attempt two",
      offsetBytes: 0,
      returnedBytes: 17,
      totalBytes: 17,
      nextOffsetBytes: null,
      contentType: "text/plain",
      artifactId: "55555555-5555-5555-5555-555555555555",
      integrityVerified: true,
      message: null,
    }));
    await waitFor(() => expect(screen.queryByText("stale attempt two")).toBeNull());
    expect(screen.getByText("selected attempt one")).toBeInTheDocument();
  });

  it("aborts a large-body page read when the drawer closes", async () => {
    let bodySignal: AbortSignal | undefined;
    const { unmount } = renderContent((url, init) => {
      if (url.pathname === "/api/workflows/runs/run-1/model-calls/42") return json(projectedMetadata());
      if (url.pathname === `/api/workflows/runs/run-1/model-calls/${callId}`) return json(stableDetail());
      if (url.pathname.endsWith("/bodies/AttemptResponse")) {
        bodySignal = init.signal as AbortSignal;
        return new Promise<Response>(() => undefined);
      }
      return json({ message: `Unexpected ${url.pathname}` }, 500);
    });

    await waitFor(() => expect(bodySignal).toBeInstanceOf(AbortSignal));
    unmount();
    expect(bodySignal?.aborted).toBe(true);
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

  it("renders declared-unavailable and unstated usage figures without inventing zeroes", async () => {
    const detail = stableDetail();
    detail.attempts[1].unavailableFigures = ["cache_read_tokens", "reasoning_tokens", "cost_amount", "provider_request_id", "first_token_at", "completed_at"];
    detail.attempts[1].usage.inputTokens = null;
    detail.attempts[1].usage.cacheReadTokens = null;
    detail.attempts[1].usage.reasoningTokens = null;
    detail.attempts[1].costAmount = null;
    detail.attempts[1].costCurrency = null;
    detail.attempts[1].pricingVersion = null;
    detail.attempts[1].providerRequestId = null;
    detail.attempts[1].firstTokenAt = null;
    detail.attempts[1].completedAt = null;
    const { rerenderTab } = renderContent((url) => {
      if (url.pathname === "/api/workflows/runs/run-1/model-calls/42") return json(projectedMetadata());
      if (url.pathname === `/api/workflows/runs/run-1/model-calls/${callId}`) return json(detail);
      return json({ message: `Unexpected ${url.pathname}` }, 500);
    }, "usage");

    expect(await screen.findByText(/Logical #7/i)).toBeInTheDocument();
    expect(screen.getByText("Input").nextElementSibling).toHaveTextContent("not recorded");
    expect(screen.getByText("Cache read").nextElementSibling).toHaveTextContent("unavailable");
    expect(screen.getByText("Cache write").nextElementSibling).toHaveTextContent("0 tokens");
    expect(screen.getByText("Reasoning").nextElementSibling).toHaveTextContent("unavailable");
    expect(screen.getByText("Total").nextElementSibling).toHaveTextContent("not recorded");
    expect(screen.getByText("Cost").nextElementSibling).toHaveTextContent("unavailable");

    rerenderTab("trace");
    expect(await screen.findByText("Provider request")).toBeInTheDocument();
    expect(screen.getByText("Provider request").nextElementSibling).toHaveTextContent("unavailable");
    expect(screen.getByText("First token").nextElementSibling).toHaveTextContent("unavailable");
    expect(screen.getByText("Completed at").nextElementSibling).toHaveTextContent("unavailable");
  });

  it("keeps same-source inline result and offloaded prompts readable after stable projection admission", async () => {
    const parts: string[] = [];
    const { rerenderTab } = renderContent((url) => {
      if (url.pathname === "/api/workflows/runs/run-1/model-calls/42") return json(projectedMetadata());
      if (url.pathname === `/api/workflows/runs/run-1/model-calls/${callId}`) return json(exactLedgerDetail());
      if (url.pathname.includes("/parts/")) {
        const part = url.pathname.split("/").at(-1)!;
        parts.push(part);
        expect(url.searchParams.get("offsetBytes")).toBe("0");
        expect(url.searchParams.get("limitBytes")).toBe(String(64 * 1024));
        return json({
          part,
          availability: "Available",
          text: part === "Result" ? "legacy inline result" : `${part} from recorded artifact`,
          offsetBytes: 0,
          returnedBytes: 20,
          totalBytes: 20,
          nextOffsetBytes: null,
          contentType: "text/plain",
          artifactId: part === "SystemPrompt" ? "88888888-8888-8888-8888-888888888888" : null,
          integrityVerified: true,
          message: null,
        });
      }
      return json({ message: `Unexpected ${url.pathname}` }, 500);
    });

    expect(await screen.findByText("legacy inline result")).toBeInTheDocument();
    expect(screen.getByText("Capture retry scheduled")).toBeInTheDocument();
    expect(screen.getByText("retryable")).toBeInTheDocument();
    expect(parts).toEqual(["Result"]);
    expect(vi.mocked(globalThis.fetch).mock.calls.some(([input]) => String(input).includes("/bodies/"))).toBe(false);

    rerenderTab("prompt");
    expect(await screen.findByText("SystemPrompt from recorded artifact")).toBeInTheDocument();
    expect(screen.getByText("UserPrompt from recorded artifact")).toBeInTheDocument();
    expect(parts).toEqual(["Result", "SystemPrompt", "UserPrompt"]);
  });

  it("fails closed on unknown stable capture metadata before an otherwise-authorized legacy fallback", async () => {
    const detail = exactLedgerDetail();
    detail.attempts[0].bodies.find((body) => body.body === "AttemptResponse")!.captureHealth = "FutureCaptureState" as WorkflowRunModelCallBodyCaptureHealth;
    renderContent((url) => {
      if (url.pathname === "/api/workflows/runs/run-1/model-calls/42") return json(projectedMetadata());
      if (url.pathname === `/api/workflows/runs/run-1/model-calls/${callId}`) return json(detail);
      return json({ message: "No body source may be read for unknown capture metadata" }, 500);
    });

    expect(await screen.findByText("Unsupported capture health")).toBeInTheDocument();
    await waitFor(() => expect(vi.mocked(globalThis.fetch)).toHaveBeenCalledTimes(2));
    expect(vi.mocked(globalThis.fetch).mock.calls.some(([input]) => String(input).includes("/parts/"))).toBe(false);
    expect(vi.mocked(globalThis.fetch).mock.calls.some(([input]) => String(input).includes("/bodies/"))).toBe(false);
  });

  it("uses the exact failed ledger source for a bounded error fallback", async () => {
    const detail = exactLedgerDetail();
    detail.attempts[0].status = "Failed";
    renderContent((url) => {
      if (url.pathname === "/api/workflows/runs/run-1/model-calls/42") return json(projectedMetadata());
      if (url.pathname === `/api/workflows/runs/run-1/model-calls/${callId}`) return json(detail);
      if (url.pathname.endsWith("/parts/Error")) return json({
        part: "Error", availability: "Available", text: "provider failed inline", offsetBytes: 0, returnedBytes: 22,
        totalBytes: 22, nextOffsetBytes: null, contentType: "text/plain", artifactId: null, integrityVerified: true, message: null,
      });
      return json({ message: `Unexpected ${url.pathname}` }, 500);
    }, "trace");

    expect(await screen.findByText("provider failed inline")).toBeInTheDocument();
  });

  it("never mixes a legacy source into harness-native or ambiguous multi-attempt projections", async () => {
    const harness = exactLedgerDetail();
    harness.sourceKind = "harness-native-record/v1";
    const first = renderContent((url) => {
      if (url.pathname === "/api/workflows/runs/run-1/model-calls/42") return json(projectedMetadata());
      if (url.pathname === `/api/workflows/runs/run-1/model-calls/${callId}`) return json(harness);
      return json({ message: "Legacy read must not occur" }, 500);
    });
    expect(await screen.findByText(/Body capture partial/i)).toBeInTheDocument();
    expect(vi.mocked(globalThis.fetch).mock.calls.some(([input]) => String(input).includes("/parts/"))).toBe(false);
    first.unmount();

    vi.unstubAllGlobals();
    const ambiguous = stableDetail("Partial", "Partial");
    ambiguous.sourceKind = "workflow-run-record/v1";
    renderContent((url) => {
      if (url.pathname === "/api/workflows/runs/run-1/model-calls/42") return json(projectedMetadata());
      if (url.pathname === `/api/workflows/runs/run-1/model-calls/${callId}`) return json(ambiguous);
      return json({ message: "Legacy read must not occur" }, 500);
    });
    expect(await screen.findByText(/Body capture partial/i)).toBeInTheDocument();
    expect(vi.mocked(globalThis.fetch).mock.calls.some(([input]) => String(input).includes("/parts/"))).toBe(false);
  });

  it("aborts an exact-source legacy body fallback when the tab changes", async () => {
    let partSignal: AbortSignal | undefined;
    const { rerenderTab } = renderContent((url, init) => {
      if (url.pathname === "/api/workflows/runs/run-1/model-calls/42") return json(projectedMetadata());
      if (url.pathname === `/api/workflows/runs/run-1/model-calls/${callId}`) return json(exactLedgerDetail());
      if (url.pathname.endsWith("/parts/Result")) {
        partSignal = init.signal as AbortSignal;
        return new Promise<Response>(() => undefined);
      }
      return json({ message: `Unexpected ${url.pathname}` }, 500);
    });

    await waitFor(() => expect(partSignal).toBeInstanceOf(AbortSignal));
    rerenderTab("usage");
    await waitFor(() => expect(partSignal?.aborted).toBe(true));
  });
});
