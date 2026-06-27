import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";

import type { PhaseAgentRef } from "@/api/workflows";
import { ApiError } from "@/api/request";

const rerunSet = { mutateAsync: vi.fn(), isPending: false };
const replay = { mutateAsync: vi.fn(), isPending: false };
const confirmMock = vi.fn<(o: { title: string; message?: string }) => Promise<boolean>>();
const alertMock = vi.fn<(o: { title: string; message?: string }) => Promise<void>>();
vi.mock("@/hooks/use-workflows", () => ({ useRerunMapBranches: () => rerunSet, useReplayRun: () => replay }));
vi.mock("@/components/dialog", () => ({ useConfirm: () => confirmMock, useAlert: () => alertMock }));

import { WaveRerunControl } from "./WaveRerunControl";
import { RunActionsContext } from "./runActionsContext";
import { RunOpenContext } from "./runOpenContext";
import type { AgentWave } from "./runActivity";

const agent = (i: number, status: string): PhaseAgentRef => ({ agentRunId: `a${i}`, status, iterationKey: `map#${i}` });
const mapWave = (statuses: string[], opts: { kind?: string } = {}): AgentWave => ({
  id: "map", kind: opts.kind ?? "map", label: "Fan out", startedAt: null, agents: statuses.map((s, i) => agent(i, s)),
});

function renderControl(wave: AgentWave, opts: { isTerminal?: boolean; onOpenRun?: (id: string) => void } = {}) {
  return render(
    <RunActionsContext.Provider value={{ runId: "r1", isTerminal: opts.isTerminal ?? true }}>
      <RunOpenContext.Provider value={opts.onOpenRun ?? null}>
        <WaveRerunControl wave={wave} />
      </RunOpenContext.Provider>
    </RunActionsContext.Provider>,
  );
}

beforeEach(() => {
  rerunSet.mutateAsync = vi.fn().mockResolvedValue({ runId: "fork" });
  replay.mutateAsync = vi.fn().mockResolvedValue({ runId: "fork" });
  confirmMock.mockReset();
  alertMock.mockReset().mockResolvedValue(undefined);
});

describe("WaveRerunControl", () => {
  it("renders nothing for a non-map wave", () => {
    const { container } = renderControl(mapWave(["Failed"], { kind: "phase" }));
    expect(container.firstChild).toBeNull();
  });

  it("renders nothing on a live run", () => {
    const { container } = renderControl(mapWave(["Failed"]), { isTerminal: false });
    expect(container.firstChild).toBeNull();
  });

  it("renders nothing when no item failed", () => {
    const { container } = renderControl(mapWave(["Succeeded", "Succeeded"]));
    expect(container.firstChild).toBeNull();
  });

  it("reruns the failed items (incl. timed-out) and navigates to the fork", async () => {
    confirmMock.mockResolvedValue(true);
    const onOpenRun = vi.fn();
    renderControl(mapWave(["Failed", "Succeeded", "TimedOut", "Succeeded"]), { onOpenRun });

    fireEvent.click(screen.getByRole("button", { name: /rerun 2 failed items/i }));

    await waitFor(() => expect(rerunSet.mutateAsync).toHaveBeenCalledTimes(1));
    expect(rerunSet.mutateAsync).toHaveBeenCalledWith(expect.objectContaining({ mapNodeId: "map", branchIndices: [0, 2], operationId: expect.any(String) }));
    await waitFor(() => expect(onOpenRun).toHaveBeenCalledWith("fork"));
  });

  it("replays the whole run from the dropdown", async () => {
    confirmMock.mockResolvedValue(true);
    renderControl(mapWave(["Failed", "Succeeded"]));

    fireEvent.click(screen.getByRole("button", { name: /more rerun options/i }));
    fireEvent.click(await screen.findByRole("menuitem", { name: /rerun entire run/i }));

    await waitFor(() => expect(replay.mutateAsync).toHaveBeenCalledWith("r1"));
  });

  it("alerts on a 409 conflict instead of crashing", async () => {
    confirmMock.mockResolvedValue(true);
    rerunSet.mutateAsync = vi.fn().mockRejectedValue(new ApiError(409, "rerun_already_in_progress", "busy"));
    renderControl(mapWave(["Failed", "Failed"]));

    fireEvent.click(screen.getByRole("button", { name: /rerun 2 failed items/i }));

    await waitFor(() => expect(alertMock).toHaveBeenCalledWith(expect.objectContaining({ title: "Rerun already in progress" })));
  });
});
