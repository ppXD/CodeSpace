import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import type { ToolCallView } from "@/api/agents";
import type { TeamMemberSummary } from "@/api/teams";
import { AgentToolCalls } from "./AgentToolCalls";

const state = vi.hoisted(() => ({
  run: { status: "Succeeded" as string },
  toolCalls: [] as ToolCallView[],
  events: [] as { sequence: number; kind: string; text: string; data: string | null; dataArtifactId?: string | null; occurredAt: string }[],
  isLoading: false,
  ledgerResolved: true,
  eventsLoading: false,
  hasOlder: false,
  olderEventsOmitted: false,
  newerEventsOmitted: false,
  atLatest: true,
  eventError: null as Error | null,
  identities: new Map<string, TeamMemberSummary>(),
  eventWindowArgs: vi.fn(),
  loadOlder: vi.fn(),
  returnToLatest: vi.fn(),
}));

vi.mock("@/hooks/use-agents", () => ({
  useAgentRun: () => ({ data: state.run }),
  useToolCalls: () => ({ data: state.toolCalls, isLoading: state.isLoading, isSuccess: state.ledgerResolved }),
  useAgentRunEventWindow: (id: string | undefined, active: boolean, kindFilter?: string) => {
    state.eventWindowArgs(id, active, kindFilter);
    return { data: state.events, isLoading: state.eventsLoading, isLoadingOlder: false, error: state.eventError, hasOlder: state.hasOlder, olderEventsOmitted: state.olderEventsOmitted, newerEventsOmitted: state.newerEventsOmitted, atLatest: state.atLatest, loadOlder: state.loadOlder, returnToLatest: state.returnToLatest };
  },
}));

vi.mock("@/hooks/use-team-members", () => ({
  useTeamMemberIdentityMap: () => state.identities,
}));

const call = (over: Partial<ToolCallView>): ToolCallView => ({
  toolKind: "git.open_pr",
  status: "Succeeded",
  createdDate: "2026-06-11T11:15:00Z",
  lastModifiedDate: "2026-06-11T11:15:02Z",
  error: null,
  approvedByUserId: null,
  approvedAt: null,
  ...over,
});

const runId = "11111111-1111-1111-1111-111111111111";
const artifactId = "22222222-2222-2222-2222-222222222222";

function payloadResponse(text: string, offset: number, total: number, next: number | null) {
  const bytes = new TextEncoder().encode(text);
  return new Response(bytes.buffer, { headers: {
    "Content-Type": "application/octet-stream",
    "X-CodeSpace-Agent-Run-Id": runId,
    "X-CodeSpace-Agent-Event-Sequence": "7",
    "X-CodeSpace-Agent-Event-Data-Artifact-Id": artifactId,
    "X-CodeSpace-Agent-Event-Data-Offset": String(offset),
    ...(next == null ? {} : { "X-CodeSpace-Agent-Event-Data-Next-Offset": String(next) }),
    "X-CodeSpace-Agent-Event-Data-Total-Bytes": String(total),
    "X-CodeSpace-Agent-Event-Data-Sha256": "a".repeat(64),
    "X-CodeSpace-Agent-Event-Data-Content-Type": "application/json",
    "X-CodeSpace-Agent-Event-Data-Integrity-Verified": offset === 0 && next == null && bytes.byteLength === total ? "true" : "false",
  } });
}

function offloadedEvent() {
  return { sequence: 7, kind: "ToolCall", text: "WebSearch", data: null, dataArtifactId: artifactId, occurredAt: "2026-06-11T11:15:00Z" };
}

beforeEach(() => {
  state.run = { status: "Succeeded" };
  state.toolCalls = [];
  state.events = [];
  state.isLoading = false;
  state.ledgerResolved = true;
  state.eventsLoading = false;
  state.hasOlder = false;
  state.olderEventsOmitted = false;
  state.newerEventsOmitted = false;
  state.atLatest = true;
  state.eventError = null;
  state.identities = new Map();
  state.eventWindowArgs.mockReset();
  state.loadOlder.mockReset();
  state.returnToLatest.mockReset();
});

afterEach(() => vi.unstubAllGlobals());

describe("AgentToolCalls", () => {
  it("lists each governed tool call with its tool, status badge, and a chronological timestamp", () => {
    state.run = { status: "Running" };
    state.isLoading = false;
    state.identities = new Map();
    state.toolCalls = [
      call({ toolKind: "git.open_pr", status: "Succeeded" }),
      call({ toolKind: "git.merge_pr", status: "AwaitingApproval" }),
      call({ toolKind: "agent.run_command", status: "Failed", error: "exit code 1" }),
    ];

    render(<AgentToolCalls agentRunId="r1" />);

    expect(screen.getByText("git.open_pr")).toBeInTheDocument();
    expect(screen.getByText("git.merge_pr")).toBeInTheDocument();
    expect(screen.getByText("agent.run_command")).toBeInTheDocument();

    // Status badges, in the warm-theme tone vocabulary (Succeeded=ok, AwaitingApproval=pending, Failed=danger).
    expect(screen.getByText("Succeeded")).toBeInTheDocument();
    expect(screen.getByText("Awaiting approval")).toBeInTheDocument();          // camelCase enum → spaced label
    const failed = screen.getByText("Failed");
    expect(failed.className).toContain("wf-status-err");
    expect(screen.getByText("Succeeded").className).toContain("wf-status-ok");
    expect(state.eventWindowArgs).toHaveBeenCalledWith(undefined, true, "ToolCall");
  });

  it("resolves the approver id to a display name and shows when it was approved", () => {
    state.run = { status: "Succeeded" };
    state.isLoading = false;
    state.identities = new Map([
      ["u-7", { userId: "u-7", name: "Dana Reviewer", email: "d@x.io", avatarUrl: null, isBot: false, role: "Member" as const, joinedAt: null }],
    ]);
    state.toolCalls = [
      call({ toolKind: "git.merge_pr", status: "Succeeded", approvedByUserId: "u-7", approvedAt: "2026-06-11T11:16:00Z" }),
    ];

    render(<AgentToolCalls agentRunId="r1" />);

    expect(screen.getByText(/approved by Dana Reviewer/)).toBeInTheDocument();
  });

  it("surfaces a tool call's redacted error", () => {
    state.run = { status: "Failed" };
    state.isLoading = false;
    state.identities = new Map();
    state.toolCalls = [call({ status: "Failed", error: "403 Forbidden: insufficient scope" })];

    render(<AgentToolCalls agentRunId="r1" />);

    expect(screen.getByText(/403 Forbidden: insufficient scope/)).toBeInTheDocument();
  });

  it("falls back to the agent's actual tool calls when the governed ledger is empty", () => {
    // A Codex / Claude-Code run uses its own harness tools — the governed ledger is empty, but the event stream
    // carries the real ToolCall events. The tab shows those (name + a compact arg preview) rather than "none".
    state.run = { status: "Succeeded" };
    state.isLoading = false;
    state.eventsLoading = false;
    state.identities = new Map();
    state.toolCalls = [];
    state.events = [
      { sequence: 1, kind: "ToolCall", text: "WebSearch", data: '{"id":"c1","name":"WebSearch","query":"ai coding agents"}', occurredAt: "2026-06-11T11:15:00Z" },
      { sequence: 3, kind: "ToolCall", text: "Read", data: '{"id":"c2","name":"Read","path":"src/app.ts"}', occurredAt: "2026-06-11T11:15:02Z" },
    ];

    render(<AgentToolCalls agentRunId="r1" />);

    expect(screen.getByText("WebSearch")).toBeInTheDocument();
    expect(screen.getByText("Read")).toBeInTheDocument();
    expect(screen.getByText(/"query":"ai coding agents"/)).toBeInTheDocument();   // the arg preview, minus id/name
    expect(state.eventWindowArgs).toHaveBeenCalledWith("r1", false, "ToolCall");
  });

  it("exposes bounded native history controls and never calls the filtered reader while governance is unresolved", () => {
    state.run = { status: "Running" };
    state.isLoading = true;
    state.ledgerResolved = false;
    const view = render(<AgentToolCalls agentRunId="r1" />);
    expect(state.eventWindowArgs).toHaveBeenLastCalledWith(undefined, true, "ToolCall");

    state.isLoading = false;
    state.ledgerResolved = true;
    state.events = [{ sequence: 10, kind: "ToolCall", text: "Read", data: null, occurredAt: "2026-06-11T11:15:00Z" }];
    state.hasOlder = true;
    state.olderEventsOmitted = true;
    state.newerEventsOmitted = true;
    state.atLatest = false;
    state.eventError = new Error("filtered page unavailable");
    view.rerender(<AgentToolCalls agentRunId="r1" />);

    fireEvent.click(screen.getByRole("button", { name: "Load earlier tool calls" }));
    fireEvent.click(screen.getByRole("button", { name: "Return to latest tool calls" }));
    expect(screen.getByText("Earlier tool calls omitted.")).toBeInTheDocument();
    expect(screen.getByText("Newer tool calls omitted.")).toBeInTheDocument();
    expect(screen.getByText("filtered page unavailable")).toBeInTheDocument();
    expect(state.loadOlder).toHaveBeenCalledOnce();
    expect(state.returnToLatest).toHaveBeenCalledOnce();
  });

  it("renders a tool call's name + args, and makes long args a click-to-expand block (no lossy ellipsis)", () => {
    const fetchMock = vi.fn();
    vi.stubGlobal("fetch", fetchMock);
    const longPath = "/private/var/folders/z7/qrtkqj255vs6dg3wjfkgcn380000gn/T/codespace/really/long/ai-coding-agents-research-report.md";
    state.run = { status: "Succeeded" };
    state.isLoading = false;
    state.eventsLoading = false;
    state.identities = new Map();
    state.toolCalls = [];
    state.events = [
      { sequence: 1, kind: "ToolCall", text: longPath, data: `{"id":"c1","name":"Read","type":"tool_use","input":{"file_path":"${longPath}","limit":100}}`, occurredAt: "2026-06-11T11:15:00Z" },
    ];

    const { container } = render(<AgentToolCalls agentRunId="r1" />);

    expect(screen.getByText("Read")).toBeInTheDocument();   // the tool NAME (from data.name), not the raw path
    const details = container.querySelector("details.tc-argbox");
    expect(details).not.toBeNull();   // long args → a disclosure, not a hard-cut
    const full = container.querySelector(".tc-args-full");
    expect(full?.textContent).toContain(longPath);   // the FULL value is present, expandable — never truncated away
    expect(fetchMock).not.toHaveBeenCalled();       // inline payloads keep their original zero-I/O path
  });

  it("shows the empty state when there are no tool calls at all", () => {
    state.run = { status: "Succeeded" };
    state.isLoading = false;
    state.eventsLoading = false;
    state.identities = new Map();
    state.toolCalls = [];
    state.events = [];

    render(<AgentToolCalls agentRunId="r1" />);

    expect(screen.getByText("No tool calls for this run")).toBeInTheDocument();
  });

  it("does not claim an empty audit while matching older rows or a typed read error remain", () => {
    state.hasOlder = true;
    state.olderEventsOmitted = true;
    const view = render(<AgentToolCalls agentRunId="r1" />);
    expect(screen.queryByText("No tool calls for this run")).toBeNull();
    expect(screen.getByRole("button", { name: "Load earlier tool calls" })).toBeInTheDocument();

    state.hasOlder = false;
    state.olderEventsOmitted = false;
    state.eventError = new Error("audit unavailable");
    view.rerender(<AgentToolCalls agentRunId="r1" />);
    expect(screen.queryByText("No tool calls for this run")).toBeNull();
    expect(screen.getByText("audit unavailable")).toBeInTheDocument();

    state.eventError = null;
    state.newerEventsOmitted = true;
    state.atLatest = false;
    view.rerender(<AgentToolCalls agentRunId="r1" />);
    expect(screen.queryByText("No tool calls for this run")).toBeNull();
    expect(screen.getByRole("button", { name: "Return to latest tool calls" })).toBeInTheDocument();
  });

  it("renders nothing while the audit is still loading (the timeline already carries the live state)", () => {
    state.run = { status: "Running" };
    state.isLoading = true;
    state.ledgerResolved = false;
    state.eventsLoading = false;
    state.identities = new Map();
    state.toolCalls = [];
    state.events = [];

    const { container } = render(<AgentToolCalls agentRunId="r1" />);

    expect(container).toBeEmptyDOMElement();
    expect(state.eventWindowArgs).toHaveBeenCalledWith(undefined, true, "ToolCall");
  });

  it("keeps offloaded bytes local and unread until the user expands that exact native event", async () => {
    const requests: Array<{ url: URL; signal: AbortSignal }> = [];
    const raw = '{"name":"WebSearch","input":{"query":"bounded payload"}}';
    vi.stubGlobal("fetch", vi.fn((input: RequestInfo | URL, init: RequestInit = {}) => {
      requests.push({ url: new URL(String(input), "http://test.local"), signal: init.signal as AbortSignal });
      return payloadResponse(raw, 0, raw.length, null);
    }));
    state.run = { status: "Succeeded" };
    state.toolCalls = [];
    state.events = [offloadedEvent()];
    state.isLoading = false;
    state.eventsLoading = false;

    render(<AgentToolCalls agentRunId={runId} />);

    expect(requests).toHaveLength(0);
    fireEvent.click(screen.getByRole("button", { name: /expand offloaded payload for websearch/i }));
    expect(await screen.findByText(/bounded payload/i)).toBeInTheDocument();
    expect(requests).toHaveLength(1);
    expect(requests[0].url.pathname).toBe(`/api/agents/runs/${runId}/events/7/data`);
    expect(requests[0].url.searchParams.get("offsetBytes")).toBe("0");
    expect(requests[0].url.searchParams.get("limitBytes")).toBe(String(64 * 1024));
    expect(requests[0].signal).toBeInstanceOf(AbortSignal);
  });

  it("aborts an in-flight payload on run identity switch and on disclosure close", async () => {
    const signals: AbortSignal[] = [];
    vi.stubGlobal("fetch", vi.fn((_input: RequestInfo | URL, init: RequestInit = {}) => {
      signals.push(init.signal as AbortSignal);
      return new Promise<Response>(() => undefined);
    }));
    state.run = { status: "Succeeded" };
    state.toolCalls = [];
    state.events = [offloadedEvent()];
    state.isLoading = false;
    state.eventsLoading = false;

    const view = render(<AgentToolCalls agentRunId={runId} />);
    fireEvent.click(screen.getByRole("button", { name: /expand offloaded payload for websearch/i }));
    await waitFor(() => expect(signals).toHaveLength(1));

    const nextRunId = "33333333-3333-3333-3333-333333333333";
    view.rerender(<AgentToolCalls agentRunId={nextRunId} />);
    await waitFor(() => expect(signals).toHaveLength(2));
    expect(signals[0].aborted).toBe(true);

    fireEvent.click(screen.getByRole("button", { name: /collapse offloaded payload for websearch/i }));
    expect(signals[1].aborted).toBe(true);
  });

  it("keeps at most eight 64 KiB pages visible while the user advances a large payload", async () => {
    vi.stubGlobal("fetch", vi.fn((input: RequestInfo | URL) => {
      const url = new URL(String(input), "http://test.local");
      const offset = Number(url.searchParams.get("offsetBytes"));
      const next = offset < 8 ? offset + 1 : null;
      return payloadResponse(String(offset), offset, 9, next);
    }));
    state.run = { status: "Succeeded" };
    state.toolCalls = [];
    state.events = [offloadedEvent()];
    state.isLoading = false;
    state.eventsLoading = false;

    const { container } = render(<AgentToolCalls agentRunId={runId} />);
    fireEvent.click(screen.getByRole("button", { name: /expand offloaded payload for websearch/i }));
    await screen.findByText(/^0$/);
    for (let offset = 1; offset < 9; offset++) {
      fireEvent.click(screen.getByRole("button", { name: /load next payload range/i }));
      await screen.findByText(new RegExp(`^${offset}$`));
    }

    expect(container.querySelectorAll(".tc-payload-chunk").length).toBeLessThanOrEqual(8);
    expect(screen.queryByText(/^0$/)).toBeNull();
    expect(screen.getByText(/Earlier payload bytes were removed/i)).toBeInTheDocument();
  });

  it("shows closed typed storage health and offers retry only when the backend declares it retryable", async () => {
    const problem = { agentRunId: runId, eventSequence: 7, dataArtifactId: artifactId, availability: "BackendUnavailable", code: "BackendUnavailable", isRetryable: true };
    const raw = '{"name":"WebSearch","input":{"query":"recovered"}}';
    vi.stubGlobal("fetch", vi.fn()
      .mockResolvedValueOnce(new Response(JSON.stringify(problem), { status: 503, headers: { "Content-Type": "application/json" } }))
      .mockResolvedValueOnce(payloadResponse(raw, 0, raw.length, null)));
    state.run = { status: "Succeeded" };
    state.toolCalls = [];
    state.events = [offloadedEvent()];
    state.isLoading = false;
    state.eventsLoading = false;

    render(<AgentToolCalls agentRunId={runId} />);
    fireEvent.click(screen.getByRole("button", { name: /expand offloaded payload for websearch/i }));
    expect(await screen.findByText(/Storage backend unavailable/i)).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: /retry payload range/i }));
    expect(await screen.findByText(/recovered/i)).toBeInTheDocument();
  });
});
