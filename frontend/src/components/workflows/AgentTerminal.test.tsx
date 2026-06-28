import { fireEvent, render, screen } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";

import type { PhaseAgentRef } from "@/api/workflows";

const { useAgentRunMock, useAgentRunEventsMock, useCellAttemptsMock } = vi.hoisted(() => ({
  useAgentRunMock: vi.fn(),
  useAgentRunEventsMock: vi.fn(),
  useCellAttemptsMock: vi.fn(),
}));
vi.mock("@/hooks/use-agents", () => ({
  useAgentRun: (id: string) => useAgentRunMock(id),
  useAgentRunEvents: (id: string) => useAgentRunEventsMock(id),
}));
vi.mock("@/hooks/use-workflows", () => ({
  useCellAttempts: () => useCellAttemptsMock(),
}));
// Stub the tool-calls panel — its own tests cover it; here we just assert the tab swaps to it.
vi.mock("./AgentToolCalls", () => ({
  AgentToolCalls: ({ agentRunId }: { agentRunId: string }) => <div data-testid="tool-calls">tools:{agentRunId}</div>,
}));

import { AgentTerminal } from "./AgentTerminal";

function termAgent(o: Partial<PhaseAgentRef> & { agentRunId: string }): PhaseAgentRef {
  return { status: "Running", ...o };
}
const evt = (sequence: number, kind: string, text: string) => ({ sequence, kind, text, data: null, occurredAt: "2026-06-23T00:00:00Z" });

beforeEach(() => {
  useAgentRunMock.mockReturnValue({ data: { status: "Running" } });
  useAgentRunEventsMock.mockReturnValue({ data: [], isLoading: false });
  useCellAttemptsMock.mockReturnValue({ data: { attempts: [] } });   // single-cell default → no switcher
});

describe("AgentTerminal", () => {
  it("offers a per-cell rerun switcher and swaps the viewed agent run to the picked attempt", () => {
    useCellAttemptsMock.mockReturnValue({ data: { attempts: [
      { attemptNumber: 1, runId: "r1", agentRunId: "ag1", status: "Failure", createdDate: "2026-06-23T00:00:00Z", isLatest: false },
      { attemptNumber: 2, runId: "r2", agentRunId: "ag2", status: "Success", createdDate: "2026-06-23T01:00:00Z", isLatest: true },
    ] } });

    render(<AgentTerminal agent={termAgent({ agentRunId: "ag2", nodeId: "map", iterationKey: "map#0" })} />);

    const pills = screen.getAllByRole("tab");
    expect(pills.map((p) => p.textContent?.trim())).toEqual(["Attempt 1", "Attempt 2latest"]);
    expect(pills[1].getAttribute("aria-selected")).toBe("true");   // the latest (the ref's agent run) is selected by default

    fireEvent.click(screen.getByRole("tab", { name: /attempt 1/i }));
    expect(useAgentRunEventsMock).toHaveBeenCalledWith("ag1");      // the earlier (failed) attempt's record is now streamed
  });

  it("shows the SELECTED attempt's own metrics (tokens · time · model) when looking back at an earlier attempt", () => {
    // The reported bug: viewing a failed/earlier attempt showed NO tokens and the LATEST attempt's time, because the
    // footer/identity read the ref (merged-latest) regardless of which attempt was picked. Each CellAttempt now carries
    // its own metrics, so switching must surface THAT attempt's figures.
    useAgentRunMock.mockReturnValue({ data: { status: "Running", harness: "claude-code" } });
    useCellAttemptsMock.mockReturnValue({ data: { attempts: [
      { attemptNumber: 1, runId: "r1", agentRunId: "ag1", status: "Failure", createdDate: "2026-06-23T00:00:00Z", isLatest: false, durationMs: 45_000, inputTokens: 2500, outputTokens: 500, costUsd: 0.0009, filesChanged: 1, toolCount: 4, model: "claude-sonnet" },
      { attemptNumber: 2, runId: "r2", agentRunId: "ag2", status: "Success", createdDate: "2026-06-23T01:00:00Z", isLatest: true, durationMs: 137_000, inputTokens: 12000, outputTokens: 2200, costUsd: 0.0045, filesChanged: 3, toolCount: 16, model: "claude-opus" },
    ] } });

    // The ref carries the merged-LATEST figures (attempt 2).
    render(<AgentTerminal agent={termAgent({ agentRunId: "ag2", nodeId: "map", iterationKey: "map#0", model: "claude-opus", toolCount: 16, durationMs: 137_000, inputTokens: 12000, outputTokens: 2200, costUsd: 0.0045, filesChanged: 3 })} />);

    expect(screen.getByText("14.2k tokens")).toBeInTheDocument();   // default = latest
    expect(screen.getByText("2m 17s")).toBeInTheDocument();

    fireEvent.click(screen.getByRole("tab", { name: /attempt 1/i }));

    expect(screen.getByText("3.0k tokens")).toBeInTheDocument();    // the failed attempt's OWN spend, no longer hidden
    expect(screen.getByText("45s")).toBeInTheDocument();            // its OWN time, not the latest's 2m 17s
    expect(screen.getByText("claude-sonnet")).toBeInTheDocument();  // its OWN model
    expect(screen.getByText("$0.0009")).toBeInTheDocument();
    expect(screen.getByText("1 file")).toBeInTheDocument();
    expect(screen.queryByText("14.2k tokens")).toBeNull();         // the latest's figures are gone
    expect(screen.queryByText("2m 17s")).toBeNull();
  });

  it("shows no switcher when the cell ran only once", () => {
    useCellAttemptsMock.mockReturnValue({ data: { attempts: [{ attemptNumber: 1, runId: "r1", agentRunId: "ag1", status: "Success", createdDate: "2026-06-23T00:00:00Z", isLatest: true }] } });
    render(<AgentTerminal agent={termAgent({ agentRunId: "ag1", nodeId: "n", iterationKey: "" })} />);
    expect(screen.queryByRole("tab")).toBeNull();
  });

  it("renders the event stream as terminal scrollback, prompting commands + toning errors", () => {
    useAgentRunEventsMock.mockReturnValue({ data: [
      evt(1, "CommandExecuted", "pnpm test --filter auth"),
      evt(2, "FileChanged", "edited session.ts"),
      evt(3, "Error", "2 failing"),
    ], isLoading: false });

    const { container } = render(<AgentTerminal agent={termAgent({ agentRunId: "a1", label: "backend-fix" })} onClose={vi.fn()} />);

    expect(screen.getByText(/pnpm test --filter auth/)).toBeInTheDocument();
    const rows = Array.from(container.querySelectorAll<HTMLElement>(".agent-terminal-row"));
    expect(rows).toHaveLength(3);
    expect(rows[0].dataset.kind).toBe("command");
    expect(rows[2].dataset.kind).toBe("error");
  });

  it("shows a connecting state while the first events load", () => {
    useAgentRunEventsMock.mockReturnValue({ data: undefined, isLoading: true });
    render(<AgentTerminal agent={termAgent({ agentRunId: "a1" })} onClose={vi.fn()} />);
    expect(screen.getByText(/connecting to the sandbox/i)).toBeInTheDocument();
  });

  it("surfaces the run's failure reason when an agent failed BEFORE emitting any event (not 'No output yet.')", () => {
    // The reported gap: a dispatch / harness failure leaves the event stream empty, so the terminal read "No output yet."
    // even though the run carries WHY it failed on its `error` field. Surface that as an error line instead.
    useAgentRunMock.mockReturnValue({ data: { status: "Failed", error: "codex-cli cannot drive the Anthropic credential 'metis-coder'" } });
    useAgentRunEventsMock.mockReturnValue({ data: [], isLoading: false });

    const { container } = render(<AgentTerminal agent={termAgent({ agentRunId: "a1", status: "Failed" })} onClose={vi.fn()} />);

    expect(screen.queryByText(/no output yet/i)).toBeNull();
    const row = container.querySelector<HTMLElement>(".agent-terminal-row");
    expect(row?.dataset.kind).toBe("error");
    expect(row).toHaveTextContent(/cannot drive the Anthropic credential/);
  });

  it("still reads 'No output yet.' for a non-failed run with an empty stream (no error to surface)", () => {
    useAgentRunMock.mockReturnValue({ data: { status: "Succeeded", error: null } });
    useAgentRunEventsMock.mockReturnValue({ data: [], isLoading: false });

    render(<AgentTerminal agent={termAgent({ agentRunId: "a1", status: "Succeeded" })} onClose={vi.fn()} />);

    expect(screen.getByText(/no output yet/i)).toBeInTheDocument();
  });

  it("switches to the tool-calls drill-in and back", () => {
    useAgentRunEventsMock.mockReturnValue({ data: [evt(1, "FileChanged", "x")], isLoading: false });
    render(<AgentTerminal agent={termAgent({ agentRunId: "a1" })} onClose={vi.fn()} />);

    expect(screen.queryByTestId("tool-calls")).toBeNull();
    fireEvent.click(screen.getByRole("button", { name: "Tool calls" }));
    expect(screen.getByTestId("tool-calls")).toHaveTextContent("tools:a1");

    fireEvent.click(screen.getByRole("button", { name: "Output" }));
    expect(screen.queryByTestId("tool-calls")).toBeNull();
  });

  it("shows the agent's goal/instruction (its prompt) in a collapsible disclosure when present", () => {
    useAgentRunMock.mockReturnValue({ data: { status: "Running", harness: "claude-code", goal: "Trace the DI registration of nodes and plugins, then report the seam." } });
    render(<AgentTerminal agent={termAgent({ agentRunId: "a1" })} onClose={vi.fn()} />);

    expect(screen.getByText("Instruction")).toBeInTheDocument();
    expect(screen.getByText(/Trace the DI registration of nodes and plugins/)).toBeInTheDocument();
  });

  it("omits the instruction disclosure when the agent run carries no goal", () => {
    useAgentRunMock.mockReturnValue({ data: { status: "Running", harness: "claude-code" } });
    render(<AgentTerminal agent={termAgent({ agentRunId: "a1" })} onClose={vi.fn()} />);
    expect(screen.queryByText("Instruction")).toBeNull();
  });

  it("leads with the agent name and carries the full identity strip (harness · model · tools · time) + the cost/files facts", () => {
    useAgentRunMock.mockReturnValue({ data: { status: "Running", harness: "claude-code" } });
    // Five FileChanged events, but the footer count comes from the REF's git-truth filesChanged (3), not the event tally.
    useAgentRunEventsMock.mockReturnValue({ data: [evt(1, "FileChanged", "x"), evt(2, "FileChanged", "x"), evt(3, "FileChanged", "y"), evt(4, "FileChanged", "y"), evt(5, "FileChanged", "z")], isLoading: false });
    render(<AgentTerminal agent={termAgent({ agentRunId: "a1", label: "backend-fix", model: "claude-opus", toolCount: 16, durationMs: 137_000, inputTokens: 12000, outputTokens: 2200, costUsd: 0.0045, filesChanged: 3 })} onClose={vi.fn()} />);

    expect(screen.getByText("backend-fix")).toBeInTheDocument();   // the title leads with the name
    expect(screen.getByText("claude-code")).toBeInTheDocument();   // harness — the live run row
    expect(screen.getByText("claude-opus")).toBeInTheDocument();   // model
    expect(screen.getByText("16 tools")).toBeInTheDocument();      // the Slice-A rollup
    expect(screen.getByText("2m 17s")).toBeInTheDocument();        // run time

    expect(screen.getByText("running")).toBeInTheDocument();       // footer: live status
    expect(screen.getByText("14.2k tokens")).toBeInTheDocument();
    expect(screen.getByText("$0.0045")).toBeInTheDocument();       // cost, computed server-side
    expect(screen.getByText("3 files")).toBeInTheDocument();       // the git-truth ref count (3), NOT the 5 FileChanged events
  });

  it("omits an identity part that is absent (no harness/model/tools/time → no strip)", () => {
    useAgentRunMock.mockReturnValue({ data: { status: "Running" } });   // no harness
    render(<AgentTerminal agent={termAgent({ agentRunId: "a1" })} onClose={vi.fn()} />);

    expect(document.querySelector(".agent-terminal-meta")).toBeNull();
  });

  it("collapses via the close button", () => {
    const onClose = vi.fn();
    render(<AgentTerminal agent={termAgent({ agentRunId: "a1" })} onClose={onClose} />);

    fireEvent.click(screen.getByRole("button", { name: /collapse terminal/i }));
    expect(onClose).toHaveBeenCalledTimes(1);
  });
});
