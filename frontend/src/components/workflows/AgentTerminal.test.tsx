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

  it("carries the live status + model + token + file rollup in the footer", () => {
    useAgentRunEventsMock.mockReturnValue({ data: [evt(1, "FileChanged", "x"), evt(2, "FileChanged", "y")], isLoading: false });
    render(<AgentTerminal agent={termAgent({ agentRunId: "a1", model: "claude-opus", inputTokens: 12000, outputTokens: 2200 })} onClose={vi.fn()} />);

    expect(screen.getByText("running")).toBeInTheDocument();
    expect(screen.getByText("claude-opus")).toBeInTheDocument();
    expect(screen.getByText("14.2k tokens")).toBeInTheDocument();
    expect(screen.getByText("2 files")).toBeInTheDocument();
  });

  it("collapses via the close button", () => {
    const onClose = vi.fn();
    render(<AgentTerminal agent={termAgent({ agentRunId: "a1" })} onClose={onClose} />);

    fireEvent.click(screen.getByRole("button", { name: /collapse terminal/i }));
    expect(onClose).toHaveBeenCalledTimes(1);
  });
});
