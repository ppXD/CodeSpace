import { fireEvent, render, screen } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";

import type { PhaseAgentRef } from "@/api/workflows";

const { useAgentRunMock, useAgentRunEventsMock } = vi.hoisted(() => ({
  useAgentRunMock: vi.fn(),
  useAgentRunEventsMock: vi.fn(),
}));
vi.mock("@/hooks/use-agents", () => ({
  useAgentRun: () => useAgentRunMock(),
  useAgentRunEvents: () => useAgentRunEventsMock(),
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
});

describe("AgentTerminal", () => {
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

  it("switches to the tool-calls drill-in and back", () => {
    useAgentRunEventsMock.mockReturnValue({ data: [evt(1, "FileChanged", "x")], isLoading: false });
    render(<AgentTerminal agent={termAgent({ agentRunId: "a1" })} onClose={vi.fn()} />);

    expect(screen.queryByTestId("tool-calls")).toBeNull();
    fireEvent.click(screen.getByRole("button", { name: "Tool calls" }));
    expect(screen.getByTestId("tool-calls")).toHaveTextContent("tools:a1");

    fireEvent.click(screen.getByRole("button", { name: "Output" }));
    expect(screen.queryByTestId("tool-calls")).toBeNull();
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
