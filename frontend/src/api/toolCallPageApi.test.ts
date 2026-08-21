import { afterEach, describe, expect, it, vi } from "vitest";

import { agentsApi, InvalidToolCallPageError, type ToolCallPageResponse, type ToolCallView } from "./agents";

const runId = "11111111-1111-1111-1111-111111111111";

function call(index: number): ToolCallView {
  return { toolKind: `git.tool_${index}`, status: "Succeeded", createdDate: `2026-08-21T00:00:${String(index).padStart(2, "0")}.000Z`, lastModifiedDate: `2026-08-21T00:00:${String(index).padStart(2, "0")}.000Z`, error: null, approvedByUserId: null, approvedAt: null };
}

function page(over: Partial<ToolCallPageResponse> = {}): ToolCallPageResponse {
  return { agentRunId: runId, mode: "Tail", requestCursor: null, items: [call(1), call(2)], hasOlder: true, nextOlderCursor: "opaque-cursor", ...over };
}

function json(body: unknown) {
  return new Response(JSON.stringify(body), { headers: { "Content-Type": "application/json" } });
}

afterEach(() => vi.unstubAllGlobals());

describe("governed ToolCall page API", () => {
  it("sends Tail and Older identities and preserves only the safe metadata projection", async () => {
    const requests: URL[] = [];
    vi.stubGlobal("fetch", vi.fn((input: RequestInfo | URL) => {
      requests.push(new URL(String(input), "http://test.local"));
      const older = requests.length === 2;
      return json(page(older ? { mode: "Older", requestCursor: "opaque-cursor", hasOlder: false, nextOlderCursor: null } : {}));
    }));

    const tail = await agentsApi.pageToolCalls(runId, { mode: "Tail", limit: 128 });
    const older = await agentsApi.pageToolCalls(runId, { mode: "Older", cursor: "opaque-cursor", limit: 128 });

    expect(requests[0].pathname).toBe(`/api/agents/runs/${runId}/tool-calls/page`);
    expect(requests[0].searchParams.get("direction")).toBe("Tail");
    expect(requests[1].searchParams.get("cursor")).toBe("opaque-cursor");
    expect(tail.items).toHaveLength(2);
    expect(older.requestCursor).toBe("opaque-cursor");
  });

  it("fails closed on wrong run/mode/cursor, unsafe rows, descending dates, and malformed page edges", async () => {
    const invalid = [
      page({ agentRunId: "22222222-2222-2222-2222-222222222222" }),
      page({ mode: "Older" }),
      page({ requestCursor: "wrong" }),
      { ...page(), items: [{ ...call(1), resultJson: "secret" }] },
      page({ items: [call(2), call(1)] }),
      page({ hasOlder: false, nextOlderCursor: "still-more" }),
    ];
    vi.stubGlobal("fetch", vi.fn().mockImplementation(() => json(invalid.shift())));

    await expect(agentsApi.pageToolCalls(runId, { mode: "Tail", limit: 128 })).rejects.toBeInstanceOf(InvalidToolCallPageError);
    await expect(agentsApi.pageToolCalls(runId, { mode: "Tail", limit: 128 })).rejects.toBeInstanceOf(InvalidToolCallPageError);
    await expect(agentsApi.pageToolCalls(runId, { mode: "Tail", limit: 128 })).rejects.toBeInstanceOf(InvalidToolCallPageError);
    await expect(agentsApi.pageToolCalls(runId, { mode: "Tail", limit: 128 })).rejects.toBeInstanceOf(InvalidToolCallPageError);
    await expect(agentsApi.pageToolCalls(runId, { mode: "Tail", limit: 128 })).rejects.toBeInstanceOf(InvalidToolCallPageError);
    await expect(agentsApi.pageToolCalls(runId, { mode: "Tail", limit: 128 })).rejects.toBeInstanceOf(InvalidToolCallPageError);
  });

  it("rejects invalid requests before I/O", async () => {
    const fetchSpy = vi.fn();
    vi.stubGlobal("fetch", fetchSpy);

    await expect(agentsApi.pageToolCalls(runId, { mode: "Tail", cursor: "x", limit: 128 })).rejects.toThrow(/invalid governed ToolCall page request/i);
    await expect(agentsApi.pageToolCalls(runId, { mode: "Older", limit: 128 })).rejects.toThrow(/invalid governed ToolCall page request/i);
    await expect(agentsApi.pageToolCalls(runId, { mode: "Older", cursor: " ", limit: 128 })).rejects.toThrow(/invalid governed ToolCall page request/i);
    await expect(agentsApi.pageToolCalls(runId, { mode: "Tail", limit: 501 })).rejects.toThrow(/invalid governed ToolCall page request/i);
    expect(fetchSpy).not.toHaveBeenCalled();
  });
});
