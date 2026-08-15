import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

import { AgentRunLogs } from "./AgentRunLogs";

function json(body: unknown, status = 200) {
  return new Response(body == null ? null : JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });
}

function stream(streamId: string, streamKind: string, status = "Completed", totalBytes = 4, contentType = "text/plain", contentEncoding: string | null = "utf-8") {
  return {
    streamId,
    agentRunId: "run-1",
    streamKind,
    contentType,
    contentEncoding,
    captureSource: "local-process-spool/v1",
    retention: "Run",
    status,
    revision: 2,
    segmentCount: 1,
    totalBytes,
    sha256: status === "Completed" ? "0".repeat(64) : null,
    createdAt: "2026-08-15T01:00:00Z",
    lastModifiedAt: "2026-08-15T01:00:01Z",
    completedAt: status === "Open" ? null : "2026-08-15T01:00:01Z",
    errorCode: status === "Open" || status === "Completed" ? null : status === "CaptureFailed" ? "capture_timeout" : `${status.toLowerCase()}_stream`,
  };
}

function bytes(text: string, offset: number, totalBytes: number, hasMore: boolean) {
  const body = new TextEncoder().encode(text);
  return new Response(body.buffer.slice(body.byteOffset, body.byteOffset + body.byteLength) as ArrayBuffer, {
    headers: {
      "Content-Type": "application/octet-stream",
      "X-CodeSpace-Log-Offset": String(offset),
      "X-CodeSpace-Log-Next-Offset": String(offset + body.byteLength),
      "X-CodeSpace-Log-Total-Bytes": String(totalBytes),
      "X-CodeSpace-Log-Has-More": String(hasMore),
      "X-CodeSpace-Log-Revision": "2",
      "X-CodeSpace-Log-Content-Type": "text/plain",
      "X-CodeSpace-Log-Content-Encoding": "utf-8",
    },
  });
}

afterEach(() => vi.unstubAllGlobals());

describe("AgentRunLogs", () => {
  it("loads metadata first and reads only the selected stream in bounded ranges", async () => {
    const requests: Array<{ url: URL; signal?: AbortSignal | null }> = [];
    vi.stubGlobal("fetch", vi.fn((input: RequestInfo | URL, init: RequestInit = {}) => {
      const url = new URL(String(input), "http://test.local");
      requests.push({ url, signal: init.signal });
      if (url.pathname.endsWith("/logs")) return json({ items: [stream("out", "stdout/v1"), stream("err", "stderr/v1")], nextCursor: null });
      if (url.pathname.endsWith("/logs/out/content")) return bytes("done", 0, 4, false);
      return json({ code: "unexpected" }, 500);
    }));

    render(<AgentRunLogs agentRunId="run-1" />);

    expect(await screen.findByText("done")).toBeInTheDocument();
    expect(requests[0].url.pathname).toBe("/api/agents/runs/run-1/logs");
    expect(requests.filter(({ url }) => url.pathname.endsWith("/content"))).toHaveLength(1);
    expect(requests.some(({ url }) => url.pathname.endsWith("/logs/err/content"))).toBe(false);
    expect(requests.every(({ signal }) => signal instanceof AbortSignal)).toBe(true);
    expect(requests[1].url.searchParams.get("limitBytes")).toBe("65536");
  });

  it("aborts and clears the previous body when the stream changes", async () => {
    let stdoutSignal: AbortSignal | undefined;
    vi.stubGlobal("fetch", vi.fn((input: RequestInfo | URL, init: RequestInit = {}) => {
      const url = new URL(String(input), "http://test.local");
      if (url.pathname.endsWith("/logs")) return json({ items: [stream("out", "stdout/v1"), stream("err", "stderr/v1", "Completed", 6)], nextCursor: null });
      if (url.pathname.endsWith("/logs/out/content")) {
        stdoutSignal = init.signal as AbortSignal;
        return new Promise<Response>(() => undefined);
      }
      if (url.pathname.endsWith("/logs/err/content")) return bytes("stderr", 0, 6, false);
      return json({ code: "unexpected" }, 500);
    }));

    render(<AgentRunLogs agentRunId="run-1" />);
    await waitFor(() => expect(stdoutSignal).toBeInstanceOf(AbortSignal));
    fireEvent.click(screen.getByRole("tab", { name: /stderr/i }));

    expect(await screen.findByText("stderr")).toBeInTheDocument();
    expect(stdoutSignal?.aborted).toBe(true);
    expect(screen.queryByText("done")).toBeNull();
  });

  it("surfaces stream lifecycle and typed storage failures without treating them as empty output", async () => {
    vi.stubGlobal("fetch", vi.fn((input: RequestInfo | URL) => {
      const url = new URL(String(input), "http://test.local");
      if (url.pathname.endsWith("/logs")) return json({ items: [stream("out", "stdout/v1", "Open", 0), stream("err", "stderr/v1", "CaptureFailed", 0), stream("trace", "transcript/v1", "Corrupt", 10)], nextCursor: null });
      if (url.pathname.endsWith("/logs/out/content")) return json({ availability: "BackendUnavailable", code: "storage_offline", isRetryable: true, streamId: "out" }, 503);
      if (url.pathname.endsWith("/logs/err/content")) return json({ availability: "AccessDenied", code: "storage_acl_denied", isRetryable: false, streamId: "err" }, 424);
      if (url.pathname.endsWith("/logs/trace/content")) return json({ availability: "IntegrityFailure", code: "artifact_corrupt", isRetryable: false, streamId: "trace" }, 410);
      return json({ code: "unexpected" }, 500);
    }));

    render(<AgentRunLogs agentRunId="run-1" />);

    expect(await screen.findByText(/Storage backend unavailable/i)).toBeInTheDocument();
    expect(screen.getByText(/retryable/i)).toBeInTheDocument();
    expect(screen.getByRole("tab", { name: /stdout.*open/i })).toBeInTheDocument();
    expect(screen.getByRole("tab", { name: /stderr.*capture failed/i })).toBeInTheDocument();
    expect(screen.getByRole("tab", { name: /transcript.*corrupt/i })).toBeInTheDocument();
    expect(screen.queryByText(/no output/i)).toBeNull();
    expect(screen.getByRole("button", { name: /refresh stream/i })).toBeInTheDocument();

    fireEvent.click(screen.getByRole("tab", { name: /stderr/i }));
    expect(await screen.findByText(/Storage access denied/i)).toBeInTheDocument();
    fireEvent.click(screen.getByRole("tab", { name: /transcript/i }));
    expect(await screen.findByText(/Stored log bytes are corrupt/i)).toBeInTheDocument();
  });

  it("streams UTF-8 decoding across byte-range boundaries", async () => {
    vi.stubGlobal("fetch", vi.fn((input: RequestInfo | URL) => {
      const url = new URL(String(input), "http://test.local");
      if (url.pathname.endsWith("/logs")) return json({ items: [stream("out", "stdout/v1", "Completed", 3)], nextCursor: null });
      const offset = Number(url.searchParams.get("offsetBytes"));
      if (offset === 0) return new Response(new Uint8Array([0xe2]).buffer, { headers: { "X-CodeSpace-Log-Offset": "0", "X-CodeSpace-Log-Next-Offset": "1", "X-CodeSpace-Log-Total-Bytes": "3", "X-CodeSpace-Log-Has-More": "true", "X-CodeSpace-Log-Revision": "2", "X-CodeSpace-Log-Content-Type": "text/plain", "X-CodeSpace-Log-Content-Encoding": "utf-8" } });
      return new Response(new Uint8Array([0x82, 0xac]).buffer, { headers: { "X-CodeSpace-Log-Offset": "1", "X-CodeSpace-Log-Next-Offset": "3", "X-CodeSpace-Log-Total-Bytes": "3", "X-CodeSpace-Log-Has-More": "false", "X-CodeSpace-Log-Revision": "2", "X-CodeSpace-Log-Content-Type": "text/plain", "X-CodeSpace-Log-Content-Encoding": "utf-8" } });
    }));

    render(<AgentRunLogs agentRunId="run-1" />);
    const load = await screen.findByRole("button", { name: /load next/i });
    fireEvent.click(load);
    expect(await screen.findByText("€")).toBeInTheDocument();
    expect(screen.queryByText("�")).toBeNull();
  });

  it("does not flush an open live head and refreshes exact metadata before reading the completed revision", async () => {
    const order: string[] = [];
    let contentReads = 0;
    vi.stubGlobal("fetch", vi.fn((input: RequestInfo | URL) => {
      const url = new URL(String(input), "http://test.local");
      if (url.pathname.endsWith("/logs")) return json({ items: [{ ...stream("out", "stdout/v1", "Open", 1), revision: 1 }], nextCursor: null });
      if (url.pathname.endsWith("/logs/out")) {
        order.push("metadata-r2");
        return json({ ...stream("out", "stdout/v1", "Completed", 3), revision: 2 });
      }
      if (url.pathname.endsWith("/logs/out/content")) {
        contentReads++;
        if (contentReads === 1) {
          order.push("content-r1");
          return new Response(new Uint8Array([0xe2]).buffer, { headers: { "X-CodeSpace-Log-Offset": "0", "X-CodeSpace-Log-Next-Offset": "1", "X-CodeSpace-Log-Total-Bytes": "1", "X-CodeSpace-Log-Has-More": "false", "X-CodeSpace-Log-Revision": "1", "X-CodeSpace-Log-Content-Type": "text/plain", "X-CodeSpace-Log-Content-Encoding": "utf-8" } });
        }
        order.push("content-r2");
        expect(url.searchParams.get("offsetBytes")).toBe("1");
        return new Response(new Uint8Array([0x82, 0xac]).buffer, { headers: { "X-CodeSpace-Log-Offset": "1", "X-CodeSpace-Log-Next-Offset": "3", "X-CodeSpace-Log-Total-Bytes": "3", "X-CodeSpace-Log-Has-More": "false", "X-CodeSpace-Log-Revision": "2", "X-CodeSpace-Log-Content-Type": "text/plain", "X-CodeSpace-Log-Content-Encoding": "utf-8" } });
      }
      return json({ code: "unexpected" }, 500);
    }));

    render(<AgentRunLogs agentRunId="run-1" />);
    expect(await screen.findByText(/No bytes captured at this offset yet/i)).toBeInTheDocument();
    expect(screen.queryByText("�")).toBeNull();
    fireEvent.click(screen.getByRole("button", { name: /refresh stream/i }));

    expect(await screen.findByText("€")).toBeInTheDocument();
    expect(screen.getByText(/Capture completed and/i)).toBeInTheDocument();
    expect(order).toEqual(["content-r1", "metadata-r2", "content-r2"]);
  });

  it("flushes a retained UTF-8 tail when exact refreshed metadata becomes terminal with an empty final range", async () => {
    let contentReads = 0;
    vi.stubGlobal("fetch", vi.fn((input: RequestInfo | URL) => {
      const url = new URL(String(input), "http://test.local");
      if (url.pathname.endsWith("/logs")) return json({ items: [{ ...stream("out", "stdout/v1", "Open", 1), revision: 1 }], nextCursor: null });
      if (url.pathname.endsWith("/logs/out")) return json({ ...stream("out", "stdout/v1", "Completed", 1), revision: 2 });
      contentReads++;
      if (contentReads === 1) return new Response(new Uint8Array([0xe2]).buffer, { headers: { "X-CodeSpace-Log-Offset": "0", "X-CodeSpace-Log-Next-Offset": "1", "X-CodeSpace-Log-Total-Bytes": "1", "X-CodeSpace-Log-Has-More": "false", "X-CodeSpace-Log-Revision": "1", "X-CodeSpace-Log-Content-Type": "text/plain", "X-CodeSpace-Log-Content-Encoding": "utf-8" } });
      expect(url.searchParams.get("offsetBytes")).toBe("1");
      return new Response(new ArrayBuffer(0), { headers: { "X-CodeSpace-Log-Offset": "1", "X-CodeSpace-Log-Next-Offset": "1", "X-CodeSpace-Log-Total-Bytes": "1", "X-CodeSpace-Log-Has-More": "false", "X-CodeSpace-Log-Revision": "2", "X-CodeSpace-Log-Content-Type": "text/plain", "X-CodeSpace-Log-Content-Encoding": "utf-8" } });
    }));

    render(<AgentRunLogs agentRunId="run-1" />);
    expect(await screen.findByText(/No bytes captured at this offset yet/i)).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: /refresh stream/i }));
    expect(await screen.findByText("�")).toBeInTheDocument();
    expect(screen.getByText(/Capture completed and/i)).toBeInTheDocument();
  });

  it("does not decode binary or encoded representations and never requests their bodies", async () => {
    vi.stubGlobal("fetch", vi.fn((input: RequestInfo | URL) => {
      const url = new URL(String(input), "http://test.local");
      if (url.pathname.endsWith("/logs")) return json({ items: [
        stream("binary", "debug/v1", "Completed", 4, "application/octet-stream", null),
        stream("compressed", "transcript/v1", "Completed", 4, "text/plain", "gzip"),
      ], nextCursor: null });
      return bytes("must-not-read", 0, 13, false);
    }));

    render(<AgentRunLogs agentRunId="run-1" />);
    expect(await screen.findByText(/Unsupported log representation/i)).toBeInTheDocument();
    fireEvent.click(screen.getByRole("tab", { name: /transcript/i }));
    expect(await screen.findByText(/encoding gzip/i)).toBeInTheDocument();
    expect(vi.mocked(globalThis.fetch).mock.calls.filter(([input]) => new URL(String(input), "http://test.local").pathname.endsWith("/content")).length).toBe(0);
  });

  it("refuses a body whose revision or representation no longer matches refreshed metadata", async () => {
    vi.stubGlobal("fetch", vi.fn((input: RequestInfo | URL) => {
      const url = new URL(String(input), "http://test.local");
      if (url.pathname.endsWith("/logs")) return json({ items: [{ ...stream("out", "stdout/v1", "Open", 4), revision: 1 }], nextCursor: null });
      return new Response(new TextEncoder().encode("data").buffer, { headers: { "X-CodeSpace-Log-Offset": "0", "X-CodeSpace-Log-Next-Offset": "4", "X-CodeSpace-Log-Total-Bytes": "4", "X-CodeSpace-Log-Has-More": "false", "X-CodeSpace-Log-Revision": "2", "X-CodeSpace-Log-Content-Type": "text/plain", "X-CodeSpace-Log-Content-Encoding": "utf-8" } });
    }));

    render(<AgentRunLogs agentRunId="run-1" />);
    expect(await screen.findByText(/Invalid log response/i)).toBeInTheDocument();
    expect(screen.getByText(/range_metadata_mismatch/i)).toBeInTheDocument();
    expect(screen.queryByText("data")).toBeNull();
  });

  it("distinguishes a missing run from a present run with no streams", async () => {
    const fetchMock = vi.fn().mockResolvedValueOnce(json(null, 404)).mockResolvedValueOnce(json({ items: [], nextCursor: null }));
    vi.stubGlobal("fetch", fetchMock);

    const first = render(<AgentRunLogs agentRunId="missing" />);
    expect(await screen.findByText(/No durable log record exists/i)).toBeInTheDocument();
    first.unmount();

    render(<AgentRunLogs agentRunId="empty" />);
    expect(await screen.findByText(/No log streams have been captured/i)).toBeInTheDocument();
  });

  it("keeps the rendered byte window bounded while paging a large stream", async () => {
    const restartOrder: string[] = [];
    vi.stubGlobal("fetch", vi.fn((input: RequestInfo | URL) => {
      const url = new URL(String(input), "http://test.local");
      if (url.pathname.endsWith("/logs")) return json({ items: [stream("out", "stdout/v1", "Completed", 10)], nextCursor: null });
      if (url.pathname.endsWith("/logs/out")) {
        restartOrder.push("metadata");
        return json(stream("out", "stdout/v1", "Completed", 10));
      }
      const offset = Number(url.searchParams.get("offsetBytes"));
      if (restartOrder.length > 0 && offset === 0) restartOrder.push("content-zero");
      return bytes(String(offset), offset, 10, offset < 9);
    }));

    const { container } = render(<AgentRunLogs agentRunId="run-1" />);
    expect(await screen.findByText("0")).toBeInTheDocument();
    for (let offset = 1; offset < 10; offset++) {
      fireEvent.click(screen.getByRole("button", { name: /load next/i }));
      await waitFor(() => expect(screen.getByText(new RegExp(`${offset}$`))).toBeInTheDocument());
    }

    expect(screen.getByText(/Earlier bytes were removed/i)).toBeInTheDocument();
    expect(container.querySelectorAll(".agent-run-log-chunk").length).toBeLessThanOrEqual(8);
    expect(screen.queryByText(/^0$/)).toBeNull();

    fireEvent.click(screen.getByRole("button", { name: /start over/i }));
    expect(await screen.findByText(/^0$/)).toBeInTheDocument();
    expect(restartOrder).toEqual(["metadata", "content-zero"]);
    expect(screen.queryByText(/Earlier bytes were removed/i)).toBeNull();
  });

  it("keeps metadata paging and exact stream refresh independently cancellable", async () => {
    let pageSignal: AbortSignal | undefined;
    let refreshSignal: AbortSignal | undefined;
    let resolvePage!: (response: Response) => void;
    let resolveRefresh!: (response: Response) => void;
    const pageResponse = new Promise<Response>((resolve) => { resolvePage = resolve; });
    const refreshResponse = new Promise<Response>((resolve) => { resolveRefresh = resolve; });
    let refreshCount = 0;
    vi.stubGlobal("fetch", vi.fn((input: RequestInfo | URL, init: RequestInit = {}) => {
      const url = new URL(String(input), "http://test.local");
      if (url.pathname.endsWith("/logs") && url.searchParams.get("cursor") === "page-2") {
        pageSignal = init.signal as AbortSignal;
        return pageResponse;
      }
      if (url.pathname.endsWith("/logs") && url.searchParams.get("cursor") === "page-3") return json({ items: [], nextCursor: null });
      if (url.pathname.endsWith("/logs")) return json({ items: [stream("out", "stdout/v1", "Open", 0)], nextCursor: "page-2" });
      if (url.pathname.endsWith("/logs/out")) {
        refreshCount++;
        if (refreshCount === 1) return json({ ...stream("out", "stdout/v1", "Open", 0), revision: 3 });
        refreshSignal = init.signal as AbortSignal;
        return refreshResponse;
      }
      if (url.pathname.endsWith("/logs/out/content")) return bytes("", 0, 0, false);
      return json({ code: "unexpected" }, 500);
    }));

    render(<AgentRunLogs agentRunId="run-1" />);
    await screen.findByText(/No bytes captured at this offset yet/i);
    fireEvent.click(screen.getByRole("button", { name: /load more streams/i }));
    await waitFor(() => expect(pageSignal).toBeInstanceOf(AbortSignal));
    fireEvent.click(screen.getByRole("button", { name: /refresh stream/i }));
    await screen.findByText(/revision 3/i);

    expect(pageSignal?.aborted).toBe(false);
    resolvePage(json({ items: [stream("err", "stderr/v1")], nextCursor: "page-3" }));
    expect(await screen.findByRole("tab", { name: /stderr/i })).toBeInTheDocument();

    fireEvent.click(screen.getByRole("button", { name: /refresh stream/i }));
    await waitFor(() => expect(refreshSignal).toBeInstanceOf(AbortSignal));
    fireEvent.click(screen.getByRole("button", { name: /load more streams/i }));
    expect(refreshSignal?.aborted).toBe(false);
    resolveRefresh(json({ ...stream("out", "stdout/v1", "Open", 0), revision: 4 }));
    expect(await screen.findByText(/revision 4/i)).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: /load more streams/i })).toBeNull();
  });

  it("aborts an in-flight range when the panel unmounts", async () => {
    let contentSignal: AbortSignal | undefined;
    vi.stubGlobal("fetch", vi.fn((input: RequestInfo | URL, init: RequestInit = {}) => {
      const url = new URL(String(input), "http://test.local");
      if (url.pathname.endsWith("/logs")) return json({ items: [stream("out", "stdout/v1")], nextCursor: null });
      contentSignal = init.signal as AbortSignal;
      return new Promise<Response>(() => undefined);
    }));

    const view = render(<AgentRunLogs agentRunId="run-1" />);
    await waitFor(() => expect(contentSignal).toBeInstanceOf(AbortSignal));
    view.unmount();
    expect(contentSignal?.aborted).toBe(true);
  });
});
