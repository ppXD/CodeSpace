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

import { AgentTile } from "./AgentTile";

function tileAgent(o: Partial<PhaseAgentRef> & { agentRunId: string }): PhaseAgentRef {
  return { status: "Running", ...o };
}
const evt = (kind: string, text: string) => ({ sequence: 1, kind, text, occurredAt: "2026-06-23T00:00:00Z", data: null });
const tile = (c: HTMLElement) => c.querySelector<HTMLElement>(".agent-tile");

beforeEach(() => {
  Element.prototype.scrollIntoView = vi.fn();   // jsdom doesn't implement it
  useAgentRunMock.mockReturnValue({ data: { status: "Running", harness: "claude-code" } });
  useAgentRunEventsMock.mockReturnValue({ data: [] });
});

describe("AgentTile", () => {
  it("shows a running tile with its latest line as the live command + a cursor", () => {
    useAgentRunEventsMock.mockReturnValue({ data: [evt("FileChanged", "editing auth/session.ts")] });

    const { container } = render(<AgentTile agent={tileAgent({ agentRunId: "a1", label: "backend-fix" })} />);

    expect(tile(container)?.dataset.state).toBe("running");
    expect(screen.getByText("backend-fix")).toBeInTheDocument();
    expect(screen.getByText(/editing auth\/session\.ts/)).toBeInTheDocument();
    expect(container.querySelector(".agent-tile-cursor")).not.toBeNull();
    expect(container.querySelector(".agent-tile-live")).not.toBeNull();
  });

  it("dims a done tile and keeps its output summary (files · tokens)", () => {
    useAgentRunMock.mockReturnValue({ data: { status: "Succeeded", harness: "claude-code" } });
    useAgentRunEventsMock.mockReturnValue({ data: [evt("FileChanged", "x"), evt("FileChanged", "y")] });

    const { container } = render(<AgentTile agent={tileAgent({ agentRunId: "a1", label: "frontend-fix", inputTokens: 9000, outputTokens: 2300 })} />);

    expect(tile(container)?.dataset.state).toBe("done");
    expect(screen.getByText("2 files · 11.3k tokens")).toBeInTheDocument();
    expect(container.querySelector(".agent-tile-cursor")).toBeNull();   // no live cursor on a finished tile
  });

  it("renders a queued agent as amber 'waiting', not failure", () => {
    useAgentRunMock.mockReturnValue({ data: { status: "Queued" } });

    const { container } = render(<AgentTile agent={tileAgent({ agentRunId: "a1", label: "deploy" })} />);

    expect(tile(container)?.dataset.state).toBe("waiting");
    expect(screen.getByText("waiting")).toBeInTheDocument();
  });

  it("marks a failed / cancelled agent as a failed tile", () => {
    useAgentRunMock.mockReturnValue({ data: { status: "Failed" } });

    const { container } = render(<AgentTile agent={tileAgent({ agentRunId: "a1", label: "x" })} />);

    expect(tile(container)?.dataset.state).toBe("failed");
  });

  it("still shows the token / file spend on a FAILED tile (a failure consumed budget too)", () => {
    useAgentRunMock.mockReturnValue({ data: { status: "Failed" } });

    const { container } = render(<AgentTile agent={tileAgent({ agentRunId: "a1", label: "x", inputTokens: 300000, outputTokens: 73100 })} />);

    expect(container.querySelector(".agent-tile-fail")?.textContent).toBe("failed · 373.1k tokens");
  });

  it("shows the run's failure reason on a failed tile that emitted no event (instead of bare 'stopped')", () => {
    useAgentRunMock.mockReturnValue({ data: { status: "Failed", error: "no drivable credential for codex-cli" } });
    useAgentRunEventsMock.mockReturnValue({ data: [] });

    const { container } = render(<AgentTile agent={tileAgent({ agentRunId: "a1", label: "x" })} />);

    expect(tile(container)?.dataset.state).toBe("failed");
    expect(screen.getByText("no drivable credential for codex-cli")).toBeInTheDocument();
    expect(screen.queryByText("stopped")).toBeNull();
  });

  it("falls back to 'stopped' on a failed tile with neither a latest event nor a (non-empty) error", () => {
    useAgentRunEventsMock.mockReturnValue({ data: [] });

    // null error → "stopped"
    useAgentRunMock.mockReturnValue({ data: { status: "Failed", error: null } });
    const { rerender } = render(<AgentTile agent={tileAgent({ agentRunId: "a1", label: "x" })} />);
    expect(screen.getByText("stopped")).toBeInTheDocument();

    // an EMPTY-STRING error must also fall through to "stopped" (|| not ??), never render a blank line
    useAgentRunMock.mockReturnValue({ data: { status: "Failed", error: "" } });
    rerender(<AgentTile agent={tileAgent({ agentRunId: "a1", label: "x" })} />);
    expect(screen.getByText("stopped")).toBeInTheDocument();
  });

  it("falls back from the phase ref status when the agent row hasn't loaded", () => {
    useAgentRunMock.mockReturnValue({ data: undefined });

    const { container } = render(<AgentTile agent={tileAgent({ agentRunId: "a1", label: "x", status: "Succeeded" })} />);

    expect(tile(container)?.dataset.state).toBe("done");
  });

  it("names the tile from label, then nodeId, then a short id", () => {
    const { rerender } = render(<AgentTile agent={tileAgent({ agentRunId: "abcdef0123", nodeId: "code" })} />);
    expect(screen.getByText("code")).toBeInTheDocument();   // no label → nodeId

    rerender(<AgentTile agent={tileAgent({ agentRunId: "abcdef0123" })} />);
    expect(screen.getByText("agent abcdef01")).toBeInTheDocument();   // neither → short id
  });

  it("scrolls into view + marks selected when the outline picks it", () => {
    const { container } = render(<AgentTile agent={tileAgent({ agentRunId: "a1", label: "x" })} selected />);

    expect(tile(container)?.dataset.selected).toBe("true");
    expect(Element.prototype.scrollIntoView).toHaveBeenCalled();
  });

  it("calls onOpen when clicked and marks data-open + aria-expanded when expanded", () => {
    const onOpen = vi.fn();
    const { container, rerender } = render(<AgentTile agent={tileAgent({ agentRunId: "a1", label: "x" })} onOpen={onOpen} />);

    fireEvent.click(screen.getByRole("button"));
    expect(onOpen).toHaveBeenCalledTimes(1);

    rerender(<AgentTile agent={tileAgent({ agentRunId: "a1", label: "x" })} onOpen={onOpen} open />);
    expect(tile(container)?.dataset.open).toBe("true");
    expect(screen.getByRole("button").getAttribute("aria-expanded")).toBe("true");
  });
});
