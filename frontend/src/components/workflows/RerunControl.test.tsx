import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";

import type { NodeStatus, WorkflowRunNodeSummary } from "@/api/workflows";
import { ApiError } from "@/api/request";

// The control drives three rerun mutations + the confirm/alert dialogs through these modules; stub them so the
// test owns the user's confirm choice, the mutation result, and the navigation callback.
const rerunOne = { mutateAsync: vi.fn(), isPending: false };
const rerunSet = { mutateAsync: vi.fn(), isPending: false };
const replay = { mutateAsync: vi.fn(), isPending: false };
const confirmMock = vi.fn<() => Promise<boolean>>();
const alertMock = vi.fn<() => Promise<void>>();
vi.mock("@/hooks/use-workflows", () => ({
  useRerunMapBranch: () => rerunOne,
  useRerunMapBranches: () => rerunSet,
  useReplayRun: () => replay,
}));
vi.mock("@/components/dialog", () => ({ useConfirm: () => confirmMock, useAlert: () => alertMock }));

import { RerunControl } from "./RerunControl";
import { RunActionsContext } from "./runActionsContext";
import { RunOpenContext } from "./runOpenContext";
import type { FanBranch } from "./mapBranches";

const branch = (index: number, status: NodeStatus, opts: { iterationKey?: string; containerKind?: string } = {}): FanBranch => ({
  index,
  badge: `#${index}`,
  row: { nodeId: "echo", iterationKey: opts.iterationKey ?? `map#${index}`, status, containerKind: opts.containerKind ?? "flow.map" } as WorkflowRunNodeSummary,
});

function renderControl(branches: FanBranch[], focused: FanBranch, opts: { isTerminal?: boolean; onOpenRun?: (id: string) => void } = {}) {
  return render(
    <RunActionsContext.Provider value={{ runId: "r1", isTerminal: opts.isTerminal ?? true }}>
      <RunOpenContext.Provider value={opts.onOpenRun ?? null}>
        <RerunControl branches={branches} focused={focused} />
      </RunOpenContext.Provider>
    </RunActionsContext.Provider>,
  );
}

beforeEach(() => {
  rerunOne.mutateAsync = vi.fn().mockResolvedValue({ runId: "fork" });
  rerunSet.mutateAsync = vi.fn().mockResolvedValue({ runId: "fork" });
  replay.mutateAsync = vi.fn().mockResolvedValue({ runId: "fork" });
  confirmMock.mockReset();
  alertMock.mockReset().mockResolvedValue(undefined);
});

describe("RerunControl", () => {
  it("renders nothing on a still-live run", () => {
    const { container } = renderControl([branch(0, "Failure")], branch(0, "Failure"), { isTerminal: false });
    expect(container.firstChild).toBeNull();
  });

  it("renders nothing for a non-map (e.g. loop) branch", () => {
    const b = branch(0, "Failure", { containerKind: "flow.loop" });
    const { container } = renderControl([b], b);
    expect(container.firstChild).toBeNull();
  });

  it("renders nothing for a nested (multi-segment) branch", () => {
    const b = branch(0, "Failure", { iterationKey: "outer#0/inner#1" });
    const { container } = renderControl([b], b);
    expect(container.firstChild).toBeNull();
  });

  it("reruns the focused item with a fresh operation id and navigates to the fork", async () => {
    confirmMock.mockResolvedValue(true);
    const onOpenRun = vi.fn();
    const branches = [branch(0, "Failure"), branch(1, "Success"), branch(2, "Success"), branch(3, "Success")];
    renderControl(branches, branches[0], { onOpenRun });

    fireEvent.click(screen.getByRole("button", { name: /rerun item/i }));

    await waitFor(() => expect(rerunOne.mutateAsync).toHaveBeenCalledTimes(1));
    expect(rerunOne.mutateAsync).toHaveBeenCalledWith(expect.objectContaining({ mapNodeId: "map", branchIndex: 0, operationId: expect.any(String) }));
    await waitFor(() => expect(onOpenRun).toHaveBeenCalledWith("fork"));
  });

  it("offers 'Rerun N failed items' as the primary when several failed, reruning the failed set", async () => {
    confirmMock.mockResolvedValue(true);
    const branches = [branch(0, "Failure"), branch(1, "Failure"), branch(2, "Success"), branch(3, "Failure")];
    renderControl(branches, branches[0]);

    fireEvent.click(screen.getByRole("button", { name: /rerun 3 failed items/i }));

    await waitFor(() => expect(rerunSet.mutateAsync).toHaveBeenCalledTimes(1));
    expect(rerunSet.mutateAsync).toHaveBeenCalledWith(expect.objectContaining({ mapNodeId: "map", branchIndices: [0, 1, 3] }));
  });

  it("does not rerun when the confirm is dismissed", async () => {
    confirmMock.mockResolvedValue(false);
    const branches = [branch(0, "Failure"), branch(1, "Success")];
    renderControl(branches, branches[0]);

    fireEvent.click(screen.getByRole("button", { name: /rerun item/i }));

    await waitFor(() => expect(confirmMock).toHaveBeenCalled());
    expect(rerunOne.mutateAsync).not.toHaveBeenCalled();
  });

  it("alerts (not crashes) when a concurrent rerun is refused 409", async () => {
    confirmMock.mockResolvedValue(true);
    rerunOne.mutateAsync = vi.fn().mockRejectedValue(new ApiError(409, "rerun_already_in_progress", "A rerun of this item is already in progress."));
    const branches = [branch(0, "Failure"), branch(1, "Success")];
    renderControl(branches, branches[0]);

    fireEvent.click(screen.getByRole("button", { name: /rerun item/i }));

    await waitFor(() => expect(alertMock).toHaveBeenCalledWith(expect.objectContaining({ title: "Rerun already in progress" })));
  });

  it("opens the dropdown with 'Rerun entire run'", async () => {
    const branches = [branch(0, "Failure"), branch(1, "Success")];
    renderControl(branches, branches[0]);

    fireEvent.click(screen.getByRole("button", { name: /more rerun options/i }));

    expect(await screen.findByRole("menuitem", { name: /rerun entire run/i })).toBeInTheDocument();
  });
});
