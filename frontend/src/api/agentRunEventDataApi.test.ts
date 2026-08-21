import { afterEach, describe, expect, it, vi } from "vitest";

import { agentsApi } from "./agents";

const runId = "11111111-1111-1111-1111-111111111111";
const artifactId = "22222222-2222-2222-2222-222222222222";

function content(body: Uint8Array, headers: Record<string, string>) {
  return new Response(body.buffer.slice(body.byteOffset, body.byteOffset + body.byteLength) as ArrayBuffer, { headers: { "Content-Type": "application/octet-stream", ...headers } });
}

function availableHeaders(offset: number, total: number, next: number | null): Record<string, string> {
  return {
    "X-CodeSpace-Agent-Run-Id": runId,
    "X-CodeSpace-Agent-Event-Sequence": "17",
    "X-CodeSpace-Agent-Event-Data-Artifact-Id": artifactId,
    "X-CodeSpace-Agent-Event-Data-Offset": String(offset),
    ...(next == null ? {} : { "X-CodeSpace-Agent-Event-Data-Next-Offset": String(next) }),
    "X-CodeSpace-Agent-Event-Data-Total-Bytes": String(total),
    "X-CodeSpace-Agent-Event-Data-Sha256": "a".repeat(64),
    "X-CodeSpace-Agent-Event-Data-Content-Type": "application/json",
    "X-CodeSpace-Agent-Event-Data-Integrity-Verified": offset === 0 && next == null ? "true" : "false",
  };
}

function json(body: unknown, status: number) {
  return new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });
}

afterEach(() => vi.unstubAllGlobals());

describe("Agent Run event payload API", () => {
  it("reads exact scoped bytes, propagates AbortSignal, and validates the bounded response envelope", async () => {
    let captured: { url?: URL; signal?: AbortSignal } = {};
    vi.stubGlobal("fetch", vi.fn((input: RequestInfo | URL, init: RequestInit = {}) => {
      captured = { url: new URL(String(input), "http://test.local"), signal: init.signal as AbortSignal };
      return content(new Uint8Array([0xe2]), availableHeaders(0, 3, 1));
    }));
    const controller = new AbortController();

    const result = await agentsApi.readRunEventDataRange(runId, 17, artifactId, 0, 64 * 1024, controller.signal);

    expect(captured.url?.pathname).toBe(`/api/agents/runs/${runId}/events/17/data`);
    expect(captured.url?.searchParams.get("offsetBytes")).toBe("0");
    expect(captured.url?.searchParams.get("limitBytes")).toBe(String(64 * 1024));
    expect(captured.signal).toBe(controller.signal);
    expect(result).toMatchObject({
      availability: "Available", agentRunId: runId, eventSequence: 17, dataArtifactId: artifactId,
      offsetBytes: 0, nextOffsetBytes: 1, totalBytes: 3, sha256: "a".repeat(64), contentType: "application/json", integrityVerified: false,
    });
    if (result.availability !== "Available") throw new Error("expected available payload bytes");
    expect([...result.bytes]).toEqual([0xe2]);
  });

  it("accepts an absent next-offset only for the exact final byte range", async () => {
    vi.stubGlobal("fetch", vi.fn(() => content(new Uint8Array([0x82, 0xac]), availableHeaders(1, 3, null))));

    const result = await agentsApi.readRunEventDataRange(runId, 17, artifactId, 1, 64 * 1024);

    expect(result).toMatchObject({ availability: "Available", offsetBytes: 1, nextOffsetBytes: null, totalBytes: 3, integrityVerified: false });
  });

  it("preserves a closed typed unavailable result and only its declared retryability", async () => {
    vi.stubGlobal("fetch", vi.fn(() => json({
      agentRunId: runId, eventSequence: 17, dataArtifactId: artifactId,
      availability: "BackendUnavailable", code: "BackendUnavailable", isRetryable: true,
    }, 503)));

    await expect(agentsApi.readRunEventDataRange(runId, 17, artifactId, 0, 64 * 1024)).resolves.toEqual({
      availability: "BackendUnavailable", code: "BackendUnavailable", isRetryable: true,
    });
  });

  it("fails closed on foreign identities, unknown states, contradictory ranges, and naked 404", async () => {
    vi.stubGlobal("fetch", vi.fn()
      .mockResolvedValueOnce(json({ agentRunId: "foreign", eventSequence: 17, dataArtifactId: artifactId, availability: "AccessDenied", code: "denied", isRetryable: false }, 424))
      .mockResolvedValueOnce(json({ agentRunId: runId, eventSequence: 17, dataArtifactId: artifactId, availability: "FutureState", code: "future", isRetryable: true }, 503))
      .mockResolvedValueOnce(json({ agentRunId: runId, eventSequence: 17, dataArtifactId: artifactId, availability: "AccessDenied", code: "denied", isRetryable: true }, 424))
      .mockResolvedValueOnce(content(new Uint8Array([1, 2]), availableHeaders(0, 2, 1)))
      .mockResolvedValueOnce(content(new Uint8Array([1]), { ...availableHeaders(0, 1, null), "X-CodeSpace-Agent-Event-Data-Next-Offset": "invalid" }))
      .mockResolvedValueOnce(new Response(null, { status: 404 })));

    await expect(agentsApi.readRunEventDataRange(runId, 17, artifactId, 0, 1)).resolves.toEqual({ availability: "InvalidResponse", code: "invalid_event_data_problem", isRetryable: false });
    await expect(agentsApi.readRunEventDataRange(runId, 17, artifactId, 0, 1)).resolves.toEqual({ availability: "InvalidResponse", code: "invalid_event_data_problem", isRetryable: false });
    await expect(agentsApi.readRunEventDataRange(runId, 17, artifactId, 0, 1)).resolves.toEqual({ availability: "InvalidResponse", code: "invalid_event_data_problem", isRetryable: false });
    await expect(agentsApi.readRunEventDataRange(runId, 17, artifactId, 0, 2)).resolves.toEqual({ availability: "InvalidResponse", code: "invalid_event_data_range_headers", isRetryable: false });
    await expect(agentsApi.readRunEventDataRange(runId, 17, artifactId, 0, 1)).resolves.toEqual({ availability: "InvalidResponse", code: "invalid_event_data_range_headers", isRetryable: false });
    await expect(agentsApi.readRunEventDataRange(runId, 17, artifactId, 0, 1)).resolves.toEqual({ availability: "Missing", code: "http_404", isRetryable: false });
  });
});
