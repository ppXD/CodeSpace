import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";

import { ApiError } from "@/api/request";

const rerunOne = { mutateAsync: vi.fn(), isPending: false };
const rerunSet = { mutateAsync: vi.fn(), isPending: false };
const fromNode = { mutateAsync: vi.fn(), isPending: false };
const replay = { mutateAsync: vi.fn(), isPending: false };
const confirmMock = vi.fn<(o: { title: string; message?: string }) => Promise<boolean>>();
const alertMock = vi.fn<(o: { title: string; message?: string }) => Promise<void>>();
vi.mock("@/hooks/use-workflows", () => ({
  useRerunMapBranch: () => rerunOne,
  useRerunMapBranches: () => rerunSet,
  useRerunFromNode: () => fromNode,
  useReplayRun: () => replay,
}));
vi.mock("@/components/dialog", () => ({ useConfirm: () => confirmMock, useAlert: () => alertMock }));

import { RerunMenu, type RerunTarget } from "./RerunMenu";
import { RunActionsContext } from "./runActionsContext";
import { RunOpenContext } from "./runOpenContext";

const mapItem: RerunTarget = { kind: "mapItem", mapNodeId: "map", focusedIndex: 0, failedIndices: [0, 2], totalCount: 4 };
const node: RerunTarget = { kind: "node", nodeId: "build" };

function renderMenu(target: RerunTarget, opts: { isTerminal?: boolean; onOpenRun?: (id: string) => void; bare?: boolean } = {}) {
  return render(
    <RunActionsContext.Provider value={{ runId: "r1", isTerminal: opts.isTerminal ?? true }}>
      <RunOpenContext.Provider value={opts.onOpenRun ?? null}>
        <RerunMenu target={target} bare={opts.bare} />
      </RunOpenContext.Provider>
    </RunActionsContext.Provider>,
  );
}

beforeEach(() => {
  rerunOne.mutateAsync = vi.fn().mockResolvedValue({ runId: "fork" });
  rerunSet.mutateAsync = vi.fn().mockResolvedValue({ runId: "fork" });
  fromNode.mutateAsync = vi.fn().mockResolvedValue({ runId: "fork" });
  replay.mutateAsync = vi.fn().mockResolvedValue({ runId: "fork" });
  confirmMock.mockReset().mockResolvedValue(true);
  alertMock.mockReset().mockResolvedValue(undefined);
});

describe("RerunMenu", () => {
  it("renders nothing on a still-live run", () => {
    const { container } = renderMenu(mapItem, { isTerminal: false });
    expect(container.firstChild).toBeNull();
  });

  it("map item: primary reruns the focused item with an operation id and navigates to the fork", async () => {
    const onOpenRun = vi.fn();
    renderMenu(mapItem, { onOpenRun });

    fireEvent.click(screen.getByRole("button", { name: /^rerun item$/i }));

    await waitFor(() => expect(rerunOne.mutateAsync).toHaveBeenCalledWith(expect.objectContaining({ mapNodeId: "map", branchIndex: 0, operationId: expect.any(String) })));
    await waitFor(() => expect(onOpenRun).toHaveBeenCalledWith("fork"));
  });

  it("map item: the dropdown suggests 'Rerun all failed items' and reruns the failed set", async () => {
    renderMenu(mapItem);

    fireEvent.click(screen.getByRole("button", { name: /more rerun options/i }));
    const all = await screen.findByRole("menuitem", { name: /rerun all failed items/i });
    expect(all).toBeInTheDocument();
    fireEvent.click(all);

    await waitFor(() => expect(rerunSet.mutateAsync).toHaveBeenCalledWith(expect.objectContaining({ mapNodeId: "map", branchIndices: [0, 2] })));
  });

  it("map item: the dropdown replays the whole run", async () => {
    renderMenu(mapItem);

    fireEvent.click(screen.getByRole("button", { name: /more rerun options/i }));
    fireEvent.click(await screen.findByRole("menuitem", { name: /rerun entire run/i }));

    await waitFor(() => expect(replay.mutateAsync).toHaveBeenCalledWith("r1"));
  });

  it("node: reruns from the node and navigates", async () => {
    const onOpenRun = vi.fn();
    renderMenu(node, { onOpenRun });

    fireEvent.click(screen.getByRole("button", { name: /rerun from here/i }));

    await waitFor(() => expect(fromNode.mutateAsync).toHaveBeenCalledWith({ fromNodeId: "build" }));
    await waitFor(() => expect(onOpenRun).toHaveBeenCalledWith("fork"));
  });

  it("does not offer 'Rerun all failed' when only one item failed", () => {
    renderMenu({ kind: "mapItem", mapNodeId: "map", focusedIndex: 0, failedIndices: [0], totalCount: 4 });
    fireEvent.click(screen.getByRole("button", { name: /more rerun options/i }));
    expect(screen.queryByRole("menuitem", { name: /rerun all failed/i })).not.toBeInTheDocument();
  });

  it("surfaces a 409 conflict as an alert", async () => {
    rerunOne.mutateAsync = vi.fn().mockRejectedValue(new ApiError(409, "rerun_already_in_progress", "busy"));
    renderMenu(mapItem);

    fireEvent.click(screen.getByRole("button", { name: /^rerun item$/i }));

    await waitFor(() => expect(alertMock).toHaveBeenCalledWith(expect.objectContaining({ title: "Rerun already in progress" })));
  });

  it("bare: renders only the primary button — no caret, no dropdown — and still reruns the item", async () => {
    renderMenu(mapItem, { bare: true });

    expect(screen.queryByRole("button", { name: /more rerun options/i })).not.toBeInTheDocument();

    fireEvent.click(screen.getByRole("button", { name: /^rerun item$/i }));

    await waitFor(() => expect(rerunOne.mutateAsync).toHaveBeenCalledWith(expect.objectContaining({ mapNodeId: "map", branchIndex: 0 })));
  });
});
