import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

import type { StorageProfileSummary } from "@/api/storage";
import { DialogProvider } from "@/components/dialog";
import type { RoutedDataClass, StorageRouteSummary } from "@/api/storageRoutes";

import { WhatLandsHereDialog } from "./WhatLandsHereDialog";

const profile: StorageProfileSummary = {
  id: "p1", stableName: "codespace-artifacts", state: "Active", currentRevision: 3, xmin: 11,
  providerTypeKey: "aliyun-oss/v1", createdDate: "2026-08-28T08:57:00Z", lastModifiedDate: "2026-09-01T14:22:00Z", health: null,
};

const dataClasses: RoutedDataClass[] = [
  { typeKey: "workflow-artifact/v1", displayName: "Workflow artifacts", hasLocalFallback: true },
  { typeKey: "agent-run-log/v1", displayName: "Agent run logs", hasLocalFallback: false },
];

function route(overrides: Partial<StorageRouteSummary> = {}): StorageRouteSummary {
  return {
    id: "rt-artifacts", dataClassTypeKey: "workflow-artifact/v1", state: "Active", currentRevision: 1, xmin: 3,
    storageProfileId: profile.id, storageProfileStableName: profile.stableName,
    profileRevisionMode: "CurrentAtWrite", pinnedProfileRevision: null,
    createdDate: profile.createdDate, lastModifiedDate: profile.lastModifiedDate, ...overrides,
  };
}

interface Call { path: string; method: string; body: unknown }

function json(body: unknown) {
  return new Response(JSON.stringify(body), { status: 200, headers: { "Content-Type": "application/json" } });
}

function renderDialog(routes: StorageRouteSummary[]) {
  const calls: Call[] = [];
  localStorage.setItem("codespace.jwt", "test-jwt");
  vi.stubGlobal("fetch", vi.fn(async (input: RequestInfo | URL, init: RequestInit = {}) => {
    const path = new URL(typeof input === "string" ? input : input.toString(), "http://test.local").pathname;
    const method = init.method ?? "GET";
    if (method !== "GET") calls.push({ path, method, body: init.body ? JSON.parse(String(init.body)) : null });

    // The client's parser refuses a detail whose current target is absent from the first revision page, so the
    // fixture has to answer the way the server does.
    const detail = (id: string, state: string, revision: number) => {
      const target = { id: `rv-${revision}`, revision, storageProfileId: profile.id, storageProfileStableName: profile.stableName, profileRevisionMode: "CurrentAtWrite", pinnedProfileRevision: null, createdDate: profile.createdDate, createdBy: "u1" };
      return json({
        id, dataClassTypeKey: "workflow-artifact/v1", state, currentRevision: revision, xmin: 42,
        createdDate: profile.createdDate, createdBy: "u1", lastModifiedDate: profile.lastModifiedDate, lastModifiedBy: "u1",
        currentTarget: target,
        revisionPage: { items: [target], nextCursor: null },
      });
    };

    if (path === "/api/storage/routes") return detail("rt-new", "Draft", 1);
    if (/\/revisions$/.test(path)) return detail(path.split("/")[4], "Active", 2);
    if (/\/state$/.test(path)) return detail(path.split("/")[4], String((JSON.parse(String(init.body)) as { state: string }).state), 1);
    return json({ items: [], nextCursor: null });
  }));

  const client = new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: 0 }, mutations: { retry: false } } });
  render(
    <QueryClientProvider client={client}>
      <DialogProvider>
        <WhatLandsHereDialog profile={profile} routes={routes} dataClasses={dataClasses} onClose={() => {}} />
      </DialogProvider>
    </QueryClientProvider>,
  );
  return calls;
}

afterEach(() => { localStorage.clear(); vi.unstubAllGlobals(); });

describe("WhatLandsHereDialog", () => {
  it("shows what lands here now as already ticked", () => {
    renderDialog([route()]);

    const boxes = screen.getAllByRole("checkbox") as HTMLInputElement[];
    expect(boxes[0].checked).toBe(true);
    expect(boxes[1].checked).toBe(false);
  });

  /**
   * Stopping a class is destructive and the dialog has to say so before it acts.
   * `RoutedDestinationResolver.LocalApplies` admits a local home for exactly two dispositions - no route at all, and
   * a route created and never activated - and a route an operator STOPPED is neither. So this does not send writes
   * back to the server's own disk: it makes them fail.
   */
  it("asks before stopping a class, and says the writes will fail rather than fall back", async () => {
    const calls = renderDialog([route()]);

    fireEvent.click(screen.getAllByRole("checkbox")[0]);
    fireEvent.click(screen.getByRole("button", { name: "Apply" }));

    const asked = await screen.findByRole("alertdialog");
    expect(asked).toHaveTextContent(/will FAIL/);
    expect(asked).toHaveTextContent(/no way back to the local home/i);
    expect(calls).toHaveLength(0);

    fireEvent.click(within(asked).getByRole("button", { name: "Stop storing them" }));

    await waitFor(() => expect(calls).toHaveLength(1));
    expect(calls[0]).toMatchObject({ path: "/api/storage/routes/rt-artifacts/state", method: "PUT", body: { expectedXmin: 3, expectedCurrentRevision: 1, state: "Disabled" } });
  });

  // Declining has to leave the pointer exactly as it was - the untick is a proposal until it is confirmed.
  it("changes nothing when the operator declines", async () => {
    const calls = renderDialog([route()]);

    fireEvent.click(screen.getAllByRole("checkbox")[0]);
    fireEvent.click(screen.getByRole("button", { name: "Apply" }));
    const asked = await screen.findByRole("alertdialog");
    fireEvent.click(within(asked).getByRole("button", { name: "Cancel" }));

    await waitFor(() => expect(screen.queryByRole("alertdialog")).not.toBeInTheDocument());
    expect(calls).toHaveLength(0);
  });

  // Ticking is not destructive and must not ask: it moves where the next write goes and nothing else.
  it("does not ask before sending a new kind of data here", async () => {
    const calls = renderDialog([]);

    fireEvent.click(screen.getAllByRole("checkbox")[1]);
    fireEvent.click(screen.getByRole("button", { name: "Apply" }));

    await waitFor(() => expect(calls.length).toBeGreaterThan(0));
    expect(screen.queryByRole("alertdialog")).not.toBeInTheDocument();
  });

  // A data class carries exactly one pointer for the life of the team, so claiming one another destination holds is
  // a repoint. Creating a second would be refused outright.
  it("repoints a class another destination holds instead of creating a second pointer", async () => {
    const calls = renderDialog([route({ storageProfileId: "other", storageProfileStableName: "archive-bucket", xmin: 7, currentRevision: 4 })]);

    fireEvent.click(screen.getAllByRole("checkbox")[0]);
    fireEvent.click(screen.getByRole("button", { name: "Apply" }));

    await waitFor(() => expect(calls.length).toBeGreaterThan(0));
    expect(calls[0]).toMatchObject({ path: "/api/storage/routes/rt-artifacts/revisions", method: "POST", body: { expectedXmin: 7, expectedCurrentRevision: 4, storageProfileId: profile.id } });
    expect(calls.some((call) => call.path === "/api/storage/routes" && call.method === "POST")).toBe(false);
  });

  // A class nobody ever routed has no pointer to move, so this is the one case that creates one - and it is inert
  // until activated, which is the step that writes and discards a real object at the destination.
  it("creates and activates a pointer for a class nobody has routed", async () => {
    const calls = renderDialog([]);

    fireEvent.click(screen.getAllByRole("checkbox")[1]);
    fireEvent.click(screen.getByRole("button", { name: "Apply" }));

    await waitFor(() => expect(calls).toHaveLength(2));
    expect(calls[0]).toMatchObject({ path: "/api/storage/routes", body: { dataClassTypeKey: "agent-run-log/v1", storageProfileId: profile.id } });
    expect(calls[1]).toMatchObject({ path: "/api/storage/routes/rt-new/state", body: { state: "Active" } });
  });

  /**
   * The two classes differ about being UNROUTED - one has a local home, the other does not - but they do not differ
   * about being STOPPED, and the dialog used to claim they did. Saying "back to this server's own disk" of a stopped
   * class is the most expensive kind of wrong: it reads as safe.
   */
  it("says a stopped class's writes will fail, for both classes, and never that they fall back", () => {
    renderDialog([route(), route({ id: "rt-logs", dataClassTypeKey: "agent-run-log/v1" })]);

    fireEvent.click(screen.getAllByRole("checkbox")[0]);
    fireEvent.click(screen.getAllByRole("checkbox")[1]);

    expect(screen.getAllByText(/Writes for these will FAIL/)).toHaveLength(2);
    expect(screen.queryByText(/goes back to this server's own disk/i)).not.toBeInTheDocument();
  });

  it("has nothing to apply until something is ticked or unticked", () => {
    renderDialog([route()]);

    expect(screen.getByRole("button", { name: "Apply" })).toBeDisabled();
  });
});
