import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";

import type { AssistantTurnBlock, DeliveryBlock, RoomAction } from "@/api/sessions";
import { DialogProvider } from "@/components/dialog/dialog-context";
import { PrCard, TurnActions } from "./SessionRoomView";

const openPullRequest = vi.hoisted(() => vi.fn());

vi.mock("@/hooks/use-workflows", async (importOriginal) => {
  const actual = await importOriginal<typeof import("@/hooks/use-workflows")>();
  return { ...actual, useOpenPullRequest: () => ({ mutateAsync: openPullRequest, isPending: false }) };
});

const action: RoomAction = { kind: "OpenPullRequest", label: "Open PRs", enabled: true };
const turn: AssistantTurnBlock = {
  id: "turn-1",
  seq: 1,
  type: "assistant_turn",
  turnIndex: 1,
  turnRunId: "11111111-1111-1111-1111-111111111111",
  runId: "11111111-1111-1111-1111-111111111111",
  status: "Success",
  blocks: [],
  actions: [action],
};

function renderActions() {
  render(
    <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
      <DialogProvider>
        <TurnActions actions={[action]} turn={turn} onOpenRun={() => {}} />
      </DialogProvider>
    </QueryClientProvider>,
  );
}

describe("multi-repository pull request outcomes", () => {
  beforeEach(() => openPullRequest.mockReset());

  it("keeps every repository result visible when one opens and another fails", async () => {
    openPullRequest.mockResolvedValue({ pullRequests: [
      { repositoryId: "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", alias: "api", disposition: "Opened", number: 42, url: "https://example.test/api/pull/42" },
      { repositoryId: "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb", alias: "web", disposition: "Failed", error: "credential cannot create pull requests" },
    ] });
    const externalOpen = vi.spyOn(window, "open").mockImplementation(() => null);
    renderActions();

    await userEvent.click(screen.getByRole("button", { name: "Open PRs" }));

    expect(externalOpen).not.toHaveBeenCalled();
    expect(screen.getByRole("link", { name: /api.*View PR/ })).toHaveAttribute("href", "https://example.test/api/pull/42");
    expect(screen.getByText("web")).toBeInTheDocument();
    expect(screen.getByText("credential cannot create pull requests")).toBeInTheDocument();
    externalOpen.mockRestore();
  });

  it("keeps already-opened and skipped repositories visible with their reason", async () => {
    openPullRequest.mockResolvedValue({ pullRequests: [
      { repositoryId: "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", alias: "api", disposition: "AlreadyOpened", number: 7, url: "https://example.test/api/pull/7" },
      { repositoryId: "cccccccc-cccc-cccc-cccc-cccccccccccc", alias: "docs", disposition: "Skipped", error: "no published source branch" },
    ] });
    renderActions();

    await userEvent.click(screen.getByRole("button", { name: "Open PRs" }));

    expect(screen.getByText("Already open")).toBeInTheDocument();
    expect(screen.getByRole("link", { name: /api.*View PR/ })).toHaveAttribute("href", "https://example.test/api/pull/7");
    expect(screen.getByText("Skipped")).toBeInTheDocument();
    expect(screen.getByText("no published source branch")).toBeInTheDocument();
  });

  it("renders persisted success and failure cards after the operation result is reloaded", () => {
    const deliveries: DeliveryBlock[] = [
      { id: "delivery-api", seq: 7, type: "delivery", title: "Ship repositories", repositoryId: "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", repositoryAlias: "api", disposition: "Opened", reference: "#42", branchHead: "codespace/api", branchBase: "main", url: "https://example.test/api/pull/42" },
      { id: "delivery-web", seq: 7, type: "delivery", title: "Ship repositories", repositoryId: "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb", repositoryAlias: "web", disposition: "Failed", error: "credential cannot create pull requests" },
    ];

    render(<>{deliveries.map(delivery => <PrCard key={delivery.id} delivery={delivery} />)}</>);

    expect(screen.getByText("api")).toBeInTheDocument();
    expect(screen.getByRole("link", { name: "View PR" })).toHaveAttribute("href", "https://example.test/api/pull/42");
    expect(screen.getByText("web")).toBeInTheDocument();
    expect(screen.getByText("Failed")).toBeInTheDocument();
    expect(screen.getByText("credential cannot create pull requests")).toBeInTheDocument();
  });
});
