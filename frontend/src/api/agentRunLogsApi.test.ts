import { afterEach, describe, expect, it, vi } from "vitest";

import { agentsApi } from "./agents";

function json(body: unknown, status = 200) {
  return new Response(body == null ? null : JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });
}

function content(body: Uint8Array, headers: Record<string, string>) {
  return new Response(body.buffer.slice(body.byteOffset, body.byteOffset + body.byteLength) as ArrayBuffer, { headers: { "Content-Type": "application/octet-stream", ...headers } });
}

afterEach(() => vi.unstubAllGlobals());

describe("Agent Run durable log API", () => {
  it("pages metadata with an opaque cursor and propagates AbortSignal", async () => {
    let captured: { url?: URL; signal?: AbortSignal } = {};
    vi.stubGlobal("fetch", vi.fn((input: RequestInfo | URL, init: RequestInit = {}) => {
      captured = { url: new URL(String(input), "http://test.local"), signal: init.signal as AbortSignal };
      return json({ items: [], nextCursor: null });
    }));
    const controller = new AbortController();

    await agentsApi.listRunLogs("run id", "opaque+/=", 17, controller.signal);

    expect(captured.url?.pathname).toBe("/api/agents/runs/run%20id/logs");
    expect(captured.url?.searchParams.get("cursor")).toBe("opaque+/=");
    expect(captured.url?.searchParams.get("limit")).toBe("17");
    expect(captured.signal).toBe(controller.signal);
  });

  it("returns exact bytes and validated bounded-range headers without decoding them in the API layer", async () => {
    const raw = new Uint8Array([0xe2]);
    vi.stubGlobal("fetch", vi.fn(() => content(raw, {
      "X-CodeSpace-Log-Offset": "0",
      "X-CodeSpace-Log-Next-Offset": "1",
      "X-CodeSpace-Log-Total-Bytes": "3",
      "X-CodeSpace-Log-Has-More": "true",
      "X-CodeSpace-Log-Revision": "7",
      "X-CodeSpace-Log-Content-Type": "text/plain",
      "X-CodeSpace-Log-Content-Encoding": "utf-8",
    })));

    const result = await agentsApi.readRunLogRange("run-1", "stream-1", 0, 65536);

    expect(result.availability).toBe("Available");
    if (result.availability !== "Available") throw new Error("expected available");
    expect([...result.bytes]).toEqual([0xe2]);
    expect(result).toMatchObject({ offsetBytes: 0, nextOffsetBytes: 1, totalBytes: 3, hasMore: true, revision: 7, contentType: "text/plain", contentEncoding: "utf-8" });
  });

  it("preserves typed storage failures and distinguishes HTTP 404 from an empty byte range", async () => {
    vi.stubGlobal("fetch", vi.fn()
      .mockResolvedValueOnce(json({ availability: "AccessDenied", code: "storage_acl_denied", isRetryable: false, streamId: "s" }, 424))
      .mockResolvedValueOnce(json(null, 404)));

    await expect(agentsApi.readRunLogRange("r", "s", 0, 1)).resolves.toEqual({ availability: "AccessDenied", code: "storage_acl_denied", isRetryable: false });
    await expect(agentsApi.readRunLogRange("r", "missing", 0, 1)).resolves.toMatchObject({ availability: "Missing", isRetryable: false });
  });

  it("fails closed when a success response omits or contradicts its range contract", async () => {
    vi.stubGlobal("fetch", vi.fn(() => content(new Uint8Array([1, 2]), {
      "X-CodeSpace-Log-Offset": "0",
      "X-CodeSpace-Log-Next-Offset": "1",
      "X-CodeSpace-Log-Total-Bytes": "2",
      "X-CodeSpace-Log-Has-More": "false",
      "X-CodeSpace-Log-Revision": "1",
      "X-CodeSpace-Log-Content-Type": "application/octet-stream",
    })));

    await expect(agentsApi.readRunLogRange("r", "s", 0, 2)).resolves.toEqual({ availability: "InvalidResponse", code: "invalid_log_range_headers", isRetryable: false });
  });

  it("rejects metadata with an unknown lifecycle, wrong owner, invalid numbers, or broken pagination", async () => {
    const valid = {
      streamId: "stream-1", agentRunId: "run-1", streamKind: "stdout/v1", contentType: "text/plain", contentEncoding: "utf-8",
      captureSource: "spool/v1", retention: "Run", status: "Completed", revision: 2, segmentCount: 1, totalBytes: 3,
      sha256: null, createdAt: "2026-08-15T00:00:00Z", lastModifiedAt: "2026-08-15T00:00:01Z", completedAt: "2026-08-15T00:00:01Z", errorCode: null,
    };
    vi.stubGlobal("fetch", vi.fn()
      .mockResolvedValueOnce(json({ items: [{ ...valid, status: "FutureSuccess" }], nextCursor: null }))
      .mockResolvedValueOnce(json({ items: [{ ...valid, agentRunId: "other-run" }], nextCursor: null }))
      .mockResolvedValueOnce(json({ items: [{ ...valid, totalBytes: -1 }], nextCursor: null }))
      .mockResolvedValueOnce(json({ items: [valid], nextCursor: "same-cursor" })));

    await expect(agentsApi.listRunLogs("run-1", null, 25)).rejects.toThrow(/metadata contract/i);
    await expect(agentsApi.listRunLogs("run-1", null, 25)).rejects.toThrow(/metadata contract/i);
    await expect(agentsApi.listRunLogs("run-1", null, 25)).rejects.toThrow(/metadata contract/i);
    await expect(agentsApi.listRunLogs("run-1", "same-cursor", 25)).rejects.toThrow(/pagination contract/i);
  });

  it("decodes one exact refreshed stream and rejects a mismatched stream identity", async () => {
    const row = {
      streamId: "stream-1", agentRunId: "run-1", streamKind: "stdout/v1", contentType: "text/plain", contentEncoding: null,
      captureSource: "spool/v1", retention: "Run", status: "Open", revision: 1, segmentCount: 0, totalBytes: 0,
      sha256: null, createdAt: "2026-08-15T00:00:00Z", lastModifiedAt: "2026-08-15T00:00:00Z", completedAt: null, errorCode: null,
    };
    vi.stubGlobal("fetch", vi.fn()
      .mockResolvedValueOnce(json(row))
      .mockResolvedValueOnce(json({ ...row, streamId: "other-stream" })));

    await expect(agentsApi.getRunLog("run-1", "stream-1")).resolves.toMatchObject({ streamId: "stream-1", status: "Open" });
    await expect(agentsApi.getRunLog("run-1", "stream-1")).rejects.toThrow(/metadata contract/i);
  });

  it("rejects metadata that omits wire-nullable fields instead of casting a partial object", async () => {
    vi.stubGlobal("fetch", vi.fn(() => json({
      streamId: "stream-1", agentRunId: "run-1", streamKind: "stdout/v1", contentType: "text/plain", captureSource: "spool/v1",
      retention: "Run", status: "Open", revision: 1, segmentCount: 0, totalBytes: 0, sha256: null,
      createdAt: "2026-08-15T00:00:00Z", lastModifiedAt: "2026-08-15T00:00:00Z", completedAt: null, errorCode: null,
    })));

    await expect(agentsApi.getRunLog("run-1", "stream-1")).rejects.toThrow(/metadata contract/i);
  });
});
