import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

import type { StorageProfileSummary } from "@/api/storage";
import type { StorageRouteDetail, StorageRouteRevisionDetail, StorageRouteSummary } from "@/api/storageRoutes";

import { StorageRouteSettings } from "./StorageRouteSettings";

const primaryProfile: StorageProfileSummary = {
  id: "profile-1",
  stableName: "primary",
  state: "Active",
  currentRevision: 3,
  xmin: 11,
  providerTypeKey: "local-rwx/v1",
  createdDate: "2026-08-14T10:00:00Z",
  lastModifiedDate: "2026-08-15T10:00:00Z",
};

const archiveProfile: StorageProfileSummary = {
  ...primaryProfile,
  id: "profile-2",
  stableName: "archive",
  currentRevision: 2,
  xmin: 12,
};

const disabledProfile: StorageProfileSummary = {
  ...primaryProfile,
  id: "profile-disabled",
  stableName: "disabled-store",
  state: "Disabled",
};

const route: StorageRouteSummary = {
  id: "route-1",
  dataClassTypeKey: "artifact-cas/v1",
  state: "Draft",
  currentRevision: 3,
  xmin: 21,
  storageProfileId: primaryProfile.id,
  storageProfileStableName: primaryProfile.stableName,
  profileRevisionMode: "CurrentAtWrite",
  pinnedProfileRevision: null,
  createdDate: "2026-08-14T10:00:00Z",
  lastModifiedDate: "2026-08-15T10:00:00Z",
};

function revision(revisionNumber: number, overrides: Partial<StorageRouteRevisionDetail> = {}): StorageRouteRevisionDetail {
  return {
    id: `route-revision-${revisionNumber}`,
    revision: revisionNumber,
    storageProfileId: primaryProfile.id,
    storageProfileStableName: primaryProfile.stableName,
    profileRevisionMode: "CurrentAtWrite",
    pinnedProfileRevision: null,
    createdDate: `2026-08-${10 + revisionNumber}T10:00:00Z`,
    createdBy: "user-1",
    ...overrides,
  };
}

function detail(overrides: Partial<StorageRouteDetail> = {}): StorageRouteDetail {
  return {
    id: route.id,
    dataClassTypeKey: route.dataClassTypeKey,
    state: route.state,
    currentRevision: route.currentRevision,
    xmin: route.xmin,
    createdDate: route.createdDate,
    createdBy: "user-1",
    lastModifiedDate: route.lastModifiedDate,
    lastModifiedBy: "user-1",
    currentTarget: revision(3),
    revisionPage: { items: [revision(3), revision(2)], nextCursor: "revision-cursor" },
    ...overrides,
  };
}

function json(body: unknown, status = 200) {
  return new Response(JSON.stringify(body), { status, statusText: status === 409 ? "Conflict" : "", headers: { "Content-Type": "application/json" } });
}

type FetchHandler = (url: URL, init: RequestInit) => Response | Promise<Response>;

function renderRoutes(handler: FetchHandler, profiles: StorageProfileSummary[] = [primaryProfile, archiveProfile, disabledProfile]) {
  localStorage.setItem("codespace.jwt", "test-jwt");
  localStorage.setItem("codespace.activeTeamId", "team-1");
  vi.stubGlobal("fetch", vi.fn(async (input: RequestInfo | URL, init: RequestInit = {}) => {
    const raw = typeof input === "string" ? input : input.toString();
    return handler(new URL(raw, "http://test.local"), init);
  }));
  const client = new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: 0 }, mutations: { retry: false } } });
  return render(<QueryClientProvider client={client}><StorageRouteSettings profiles={profiles} /></QueryClientProvider>);
}

afterEach(() => { localStorage.clear(); vi.unstubAllGlobals(); });

describe("storage data routing settings", () => {
  it("loads bounded keyset pages, de-duplicates identities, and creates a Draft only against an Active profile", async () => {
    const secondRoute = { ...route, id: "route-2", dataClassTypeKey: "workflow-run-model-call/v1" };
    const routableDataClasses = [
      { typeKey: "agent-run-log/v1", displayName: "Agent run logs" },
      { typeKey: "workflow-artifact/v1", displayName: "Workflow artifacts" },
    ];
    const requests: Array<{ url: URL; init: RequestInit }> = [];
    let listReads = 0;
    let createBody: Record<string, unknown> | undefined;
    renderRoutes(async (url, init) => {
      requests.push({ url, init });
      const method = init.method ?? "GET";
      if (url.pathname === "/api/storage/routes/page") {
        listReads++;
        return json(listReads === 1
          ? { items: [route], nextCursor: "route-cursor" }
          : { items: [route, secondRoute], nextCursor: null });
      }
      if (url.pathname === "/api/storage/data-classes") return json(routableDataClasses);
      if (url.pathname === "/api/storage/routes" && method === "POST") {
        createBody = JSON.parse(String(init.body)) as Record<string, unknown>;
        return json(detail({ id: "route-3", dataClassTypeKey: "workflow-artifact/v1", currentRevision: 1, currentTarget: revision(1), revisionPage: { items: [revision(1)], nextCursor: null } }));
      }
      return json({ message: `Unexpected request ${method} ${url.pathname}` }, 500);
    });

    expect(await screen.findByRole("heading", { name: "Data routing" })).toBeInTheDocument();
    expect(await screen.findByText("artifact-cas/v1")).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Load more data routes" }));

    expect(await screen.findByText("workflow-run-model-call/v1")).toBeInTheDocument();
    expect(screen.getAllByText("artifact-cas/v1")).toHaveLength(1);
    expect(requests[0].url.searchParams.get("limit")).toBe("50");
    expect(requests[0].init.signal).toBeInstanceOf(AbortSignal);
    expect(requests.some(({ url }) => url.searchParams.get("cursor") === "route-cursor")).toBe(true);

    fireEvent.click(screen.getByRole("button", { name: "Create data route" }));
    const dialog = await screen.findByRole("dialog", { name: "Create data route" });
    expect(within(dialog).queryByRole("option", { name: /disabled-store/i })).not.toBeInTheDocument();

    // The data class is chosen from what this deployment reads. A free-text key let an operator create a route for a
    // class nothing asks for — an Active-looking row that routes nothing.
    const dataClassSelect = await within(dialog).findByLabelText("Data class");
    await waitFor(() => expect(within(dataClassSelect).getAllByRole("option").map((option) => option.getAttribute("value"))).toEqual([
      "agent-run-log/v1", "workflow-artifact/v1",
    ]));
    fireEvent.change(dataClassSelect, { target: { value: "workflow-artifact/v1" } });
    fireEvent.change(within(dialog).getByLabelText("Storage profile"), { target: { value: archiveProfile.id } });
    fireEvent.click(within(dialog).getByRole("button", { name: "Create Draft" }));

    await waitFor(() => expect(createBody).toEqual({
      dataClassTypeKey: "workflow-artifact/v1",
      storageProfileId: archiveProfile.id,
      profileRevisionMode: "CurrentAtWrite",
      pinnedProfileRevision: null,
    }));
  });

  it("loads descending revision pages without duplicates and appends an exact pinned profile revision", async () => {
    const requests: Array<{ url: URL; body?: Record<string, unknown>; signal?: AbortSignal | null }> = [];
    let current = detail();
    let detailReads = 0;
    renderRoutes(async (url, init) => {
      const method = init.method ?? "GET";
      if (url.pathname === "/api/storage/routes/page") return json({ items: [route], nextCursor: null });
      if (url.pathname === `/api/storage/routes/${route.id}` && method === "GET") {
        detailReads++;
        requests.push({ url, signal: init.signal });
        if (url.searchParams.get("revisionCursor") === "revision-cursor") {
          return json({ ...current, revisionPage: { items: [revision(2), revision(1)], nextCursor: null } });
        }
        return json(current);
      }
      if (url.pathname === `/api/storage/routes/${route.id}/revisions` && method === "POST") {
        const body = JSON.parse(String(init.body)) as Record<string, unknown>;
        requests.push({ url, body });
        current = detail({
          currentRevision: 4,
          xmin: 22,
          currentTarget: revision(4, { storageProfileId: archiveProfile.id, storageProfileStableName: archiveProfile.stableName, profileRevisionMode: "Pinned", pinnedProfileRevision: 2 }),
          revisionPage: { items: [revision(4), revision(3), revision(2), revision(1)], nextCursor: null },
        });
        return json(current);
      }
      return json({ message: `Unexpected request ${method} ${url.pathname}` }, 500);
    });

    await screen.findByText("artifact-cas/v1");
    fireEvent.click(screen.getByRole("button", { name: "Manage artifact-cas/v1" }));
    const dialog = await screen.findByRole("dialog", { name: "Manage data route artifact-cas/v1" });
    expect(await within(dialog).findByText("Current target")).toBeInTheDocument();
    expect(within(dialog).getAllByText(/primary · current at write/i).length).toBeGreaterThan(0);
    fireEvent.click(within(dialog).getByRole("button", { name: "Load more route revisions" }));

    const history = await within(dialog).findByRole("list", { name: "Data route revision history" });
    await waitFor(() => expect(within(history).getAllByText(/^Revision [123]$/)).toHaveLength(3));
    expect(within(history).getAllByText("Revision 2")).toHaveLength(1);
    expect(requests.some(({ url }) => url.searchParams.get("revisionCursor") === "revision-cursor" && url.searchParams.get("revisionLimit") === "25")).toBe(true);
    expect(requests.filter(({ signal }) => signal != null).every(({ signal }) => signal instanceof AbortSignal)).toBe(true);

    fireEvent.change(within(dialog).getByLabelText("Revision storage profile"), { target: { value: archiveProfile.id } });
    fireEvent.change(within(dialog).getByLabelText("Profile revision mode"), { target: { value: "Pinned" } });
    fireEvent.change(within(dialog).getByLabelText("Exact profile revision"), { target: { value: "2" } });
    fireEvent.click(within(dialog).getByRole("button", { name: "Append route revision" }));

    await waitFor(() => expect(requests.find(({ body }) => body != null)?.body).toEqual({
      expectedXmin: 21,
      expectedCurrentRevision: 3,
      storageProfileId: archiveProfile.id,
      profileRevisionMode: "Pinned",
      pinnedProfileRevision: 2,
    }));
    await waitFor(() => expect(detailReads).toBeGreaterThanOrEqual(3));
    expect(await within(dialog).findByText(/archive · pinned profile revision 2/i)).toBeInTheDocument();
  });

  it("refetches on a 409 with an actionable message, then requires explicit confirmation for terminal retirement", async () => {
    const stateBodies: Record<string, unknown>[] = [];
    let current = detail();
    let detailReads = 0;
    let appendAttempts = 0;
    renderRoutes(async (url, init) => {
      const method = init.method ?? "GET";
      if (url.pathname === "/api/storage/routes/page") return json({ items: [{ ...route, state: current.state, currentRevision: current.currentRevision, xmin: current.xmin }], nextCursor: null });
      if (url.pathname === `/api/storage/routes/${route.id}` && method === "GET") {
        detailReads++;
        return json(current);
      }
      if (url.pathname === `/api/storage/routes/${route.id}/revisions` && method === "POST") {
        appendAttempts++;
        current = detail({ currentRevision: 4, xmin: 22, currentTarget: revision(4), revisionPage: { items: [revision(4), revision(3)], nextCursor: null } });
        return json({ code: "storage_route_conflict", message: "stale" }, 409);
      }
      if (url.pathname === `/api/storage/routes/${route.id}/state` && method === "PUT") {
        const body = JSON.parse(String(init.body)) as Record<string, unknown>;
        stateBodies.push(body);
        current = detail({ ...current, state: body.state as StorageRouteDetail["state"], xmin: current.xmin + 1 });
        return json(current);
      }
      return json({ message: `Unexpected request ${method} ${url.pathname}` }, 500);
    });

    await screen.findByText("artifact-cas/v1");
    fireEvent.click(screen.getByRole("button", { name: "Manage artifact-cas/v1" }));
    let dialog = await screen.findByRole("dialog", { name: "Manage data route artifact-cas/v1" });
    fireEvent.click(await within(dialog).findByRole("button", { name: "Append route revision" }));

    expect(await within(dialog).findByRole("alert")).toHaveTextContent(/changed elsewhere.*reloaded.*review.*try again/i);
    await waitFor(() => expect(detailReads).toBeGreaterThanOrEqual(2));
    expect(appendAttempts).toBe(1);
    dialog = await screen.findByRole("dialog", { name: "Manage data route artifact-cas/v1" });
    expect(within(dialog).getByText("Current revision 4")).toBeInTheDocument();

    fireEvent.click(within(dialog).getByRole("button", { name: "Set Active" }));
    await waitFor(() => expect(stateBodies[0]).toEqual({ expectedXmin: 22, expectedCurrentRevision: 4, state: "Active" }));
    expect(await within(dialog).findByText("Active", { selector: ".cn-status" })).toBeInTheDocument();
    fireEvent.click(within(dialog).getByRole("button", { name: "Set Disabled" }));
    await waitFor(() => expect(stateBodies[1]).toEqual({ expectedXmin: 23, expectedCurrentRevision: 4, state: "Disabled" }));
    expect(await within(dialog).findByText("Disabled", { selector: ".cn-status" })).toBeInTheDocument();

    fireEvent.click(within(dialog).getByRole("button", { name: "Retire route" }));
    const confirmation = await screen.findByRole("alertdialog", { name: "Retire artifact-cas/v1?" });
    expect(confirmation).toHaveTextContent(/terminal/i);
    expect(stateBodies).toHaveLength(2);
    fireEvent.click(within(confirmation).getByRole("button", { name: "Retire permanently" }));

    await waitFor(() => expect(stateBodies[2]).toEqual({ expectedXmin: 24, expectedCurrentRevision: 4, state: "Retired" }));
    expect(await within(dialog).findByText("Retired", { selector: ".cn-status" })).toBeInTheDocument();
  });

  it("renders only safe route projections and fails closed on an unknown route state", async () => {
    const unsafeRoute = {
      ...route,
      state: "Migrating",
      nonSecretConfig: { rootPath: "/private/storage" },
      credentialRef: "db:secret-reference",
      secret: "provider-secret",
    };
    renderRoutes(async (url) => url.pathname === "/api/storage/routes/page"
      ? json({ items: [unsafeRoute], nextCursor: null })
      : json({}, 500));

    const alert = await screen.findByRole("alert");
    expect(alert).toHaveTextContent("Couldn't load data routes");
    expect(alert).toHaveTextContent(/unsupported storage route state/i);
    expect(document.body).not.toHaveTextContent("/private/storage");
    expect(document.body).not.toHaveTextContent("db:secret-reference");
    expect(document.body).not.toHaveTextContent("provider-secret");
    expect(screen.queryByRole("button", { name: /Manage artifact-cas/ })).not.toBeInTheDocument();
  });

  it("fails closed when a detail contains an unknown target mode without rendering provider material", async () => {
    const unsafeDetail = {
      ...detail(),
      currentTarget: {
        ...revision(3),
        profileRevisionMode: "RollingFuture",
        nonSecretConfig: { rootPath: "/private/storage" },
        credentialRef: "db:secret-reference",
      },
    };
    renderRoutes(async (url) => {
      if (url.pathname === "/api/storage/routes/page") return json({ items: [route], nextCursor: null });
      if (url.pathname === `/api/storage/routes/${route.id}`) return json(unsafeDetail);
      return json({}, 500);
    });

    await screen.findByText("artifact-cas/v1");
    fireEvent.click(screen.getByRole("button", { name: "Manage artifact-cas/v1" }));
    const dialog = await screen.findByRole("dialog", { name: "Manage data route artifact-cas/v1" });
    const alert = await within(dialog).findByRole("alert");
    expect(alert).toHaveTextContent(/unsupported storage profile revision mode/i);
    expect(document.body).not.toHaveTextContent("/private/storage");
    expect(document.body).not.toHaveTextContent("db:secret-reference");
  });
});
