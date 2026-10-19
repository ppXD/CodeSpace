import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

import type { MeTeam, RelinkSuggestion } from "@/api/types";
import { DialogProvider } from "@/components/dialog";
import type { ConnectionIssue } from "@/lib/credentialHealth";

import { RelinkDialog } from "./RelinkDialog";
import { RepositoryConnectionFix } from "./RepositoryConnectionIssue";

/**
 * The Re-link dialog shows the list the BACKEND computed for the repository's provider connection — the caller's own and the team's service
 * tokens, on that connection and on any other connection to the same host — and filters nothing of its own. A team that signs in to one GitLab
 * through two OAuth applications holds two connections to it; the dialog used to keep only the credentials whose connection was the repository's
 * and told a person who had just connected GitLab that nothing could take the repository. Only against an API that predates the list (a 404
 * on the route — with the JSON body the real server's fallback writes, not an empty one) does it filter the credential list itself, as it did.
 */
const team: MeTeam = { id: "t1", slug: "acme", name: "Acme", kind: "Workspace", role: "Admin", permissions: ["repos.manage", "credentials.manage"], memberCount: 3, repositoryCount: 1, projectCount: 1, workflowCount: 0 };

const candidate = (id: string, name: string, over: Partial<RelinkSuggestion> = {}): RelinkSuggestion => ({
  credentialId: id, displayName: name, authType: "OAuth", ownership: "Personal", kind: "YourConnection", createdDate: "2026-02-01T00:00:00Z", connectedBeforeDisconnect: false, providerInstanceId: "pi1", providerInstanceName: "GitLab", ...over,
});

type Call = { method: string; url: string; body: unknown };
type Answer = { status?: number; body?: unknown };

const credentialsOnTwoConnections = [
  { id: "c-dead", teamId: "t1", providerInstanceId: "pi1", ownerUserId: "u9", ownerUserName: "Nacho", ownership: "Personal", authType: "OAuth", displayName: "GitLab · nacho", status: "Error", health: "rejected", createdDate: "2026-01-01T00:00:00Z" },
  { id: "c-here", teamId: "t1", providerInstanceId: "pi1", ownerUserId: "u1", ownerUserName: "Maya", ownership: "Personal", authType: "OAuth", displayName: "Maya's GitLab", status: "Active", health: "active", createdDate: "2026-02-01T00:00:00Z" },
  { id: "c-there", teamId: "t1", providerInstanceId: "pi1b", ownerUserId: "u1", ownerUserName: "Maya", ownership: "Personal", authType: "OAuth", displayName: "Maya's GitLab_TED", status: "Active", health: "active", createdDate: "2026-02-09T00:00:00Z" },
];

function stubApi(answers: Record<string, Answer | unknown>) {
  localStorage.setItem("codespace.jwt", "test-jwt");
  localStorage.setItem("codespace.activeTeamId", team.id);

  const calls: Call[] = [];
  const routes: Record<string, Answer | unknown> = { "/api/users/me": { id: "u1", email: "u@test.local", name: "Maya", teams: [team], permissions: [], passwordMustChange: false }, ...answers };

  vi.stubGlobal("fetch", vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = typeof input === "string" ? input : input.toString();
    calls.push({ method: init?.method ?? "GET", url, body: init?.body ? JSON.parse(String(init.body)) : undefined });

    const key = Object.keys(routes).find((k) => url.includes(k));

    // A route nothing here answers is one this server does not have, and the real server says so the way Startup's MapFallback does: a 404 WITH a
    // JSON body whose code is "not_found" — not an empty 404, which no CodeSpace API produces.
    if (key === undefined) return new Response(JSON.stringify({ code: "not_found", message: `This server has no ${new URL(url, "http://host").pathname}. Check the host — the API and the web app are different origins.` }), { status: 404, headers: { "Content-Type": "application/json" } });

    const answer = routes[key];
    const explicit = answer !== null && typeof answer === "object" && ("status" in (answer as object) || "body" in (answer as object)) ? (answer as Answer) : { body: answer };

    return new Response(explicit.body === undefined ? "" : JSON.stringify(explicit.body), { status: explicit.status ?? 200, headers: { "Content-Type": "application/json" } });
  }));

  return calls;
}

function renderDialog(props: Partial<React.ComponentProps<typeof RelinkDialog>> = {}) {
  const onClose = vi.fn();
  const onRelinked = vi.fn();

  render(
    <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: 0 } } })}>
      <DialogProvider><RelinkDialog repositories={[{ id: "r1", fullPath: "acme/api" }]} providerInstanceId="pi1" fromCredentialId="c-dead" onClose={onClose} onRelinked={onRelinked} {...props} /></DialogProvider>
    </QueryClientProvider>,
  );

  return { onClose, onRelinked };
}

afterEach(() => { localStorage.clear(); vi.unstubAllGlobals(); vi.restoreAllMocks(); });

describe("RelinkDialog — the backend's candidate set", () => {
  it("renders exactly the list the backend computed, in its order, naming the connection of each when they differ, and moves the repository onto the one picked", async () => {
    const calls = stubApi({
      "/relink-candidates": [
        candidate("c-here", "Maya's GitLab"),
        candidate("c-team", "Acme group token", { ownership: "TeamService", kind: "TeamServiceToken", authType: "GroupAccessToken" }),
        candidate("c-there", "Maya's GitLab_TED", { providerInstanceId: "pi1b", providerInstanceName: "GitLab_TED" }),
      ],
      "/api/repositories/r1/relink-credential": {},
    });
    const { onClose, onRelinked } = renderDialog();

    const dialog = await screen.findByRole("dialog", { name: "Re-link credential" });
    await waitFor(() => expect(within(dialog).getByText("Maya's GitLab_TED")).toBeTruthy());

    expect(within(dialog).getAllByRole("listitem").map((e) => e.textContent)).toEqual([
      expect.stringContaining("Maya's GitLab"),
      expect.stringContaining("Acme group token"),
      expect.stringContaining("Maya's GitLab_TED"),
    ]);
    expect(within(dialog).getByText("on GitLab_TED")).toBeTruthy();
    expect(within(dialog).getAllByRole("listitem")[2].textContent, "the row that changes the connection says so, in words beside it").toContain("Moves the repository to GitLab_TED, another connection to the same host.");
    expect(within(dialog).getAllByRole("listitem")[0].textContent).not.toMatch(/Moves/);
    expect(within(dialog).getAllByText("on GitLab")).toHaveLength(2);

    fireEvent.click(within(dialog).getByText("Maya's GitLab_TED"));

    await waitFor(() => expect(onRelinked).toHaveBeenCalled());
    expect(calls.find((c) => c.url.endsWith("/api/repositories/r1/relink-credential"))!.body).toEqual({ newCredentialId: "c-there" });
    expect(onClose).toHaveBeenCalled();
    expect(calls.map((c) => c.url).find((u) => u.includes("/relink-candidates"))).toMatch(/\/api\/provider-instances\/pi1\/relink-candidates\?fromCredentialId=c-dead$/);
    expect(calls.some((c) => c.url.endsWith("/api/credentials")), "the dialog read no credential list: there is nothing for it to filter").toBe(false);
    expect(calls.some((c) => c.url.includes("/relink-suggestions")), "and asked for no suggestions to order by").toBe(false);
  });

  it("names the connection and says the pick moves the repository when every candidate is on one other connection — the owner's own case", async () => {
    stubApi({
      "/relink-candidates": [candidate("c-there", "mars", { providerInstanceId: "pi-ted", providerInstanceName: "GitLab_TED" })],
      "/api/repositories/r1/relink-credential": {},
    });
    renderDialog({ repositories: [{ id: "r1", fullPath: "solar/OMEProject-Api" }] });

    const dialog = await screen.findByRole("dialog", { name: "Re-link credential" });
    const row = (await within(dialog).findByText("mars")).closest("button")!;

    expect(within(row).getByText("on GitLab_TED"), "the row says which connection it is on, though no other row differs").toBeTruthy();
    expect(row.textContent).toMatch(/Moves the repository to GitLab_TED/);
  });

  it("says in the plural that a pick moves every repository when several are re-linked at once", async () => {
    stubApi({ "/relink-candidates": [candidate("c-there", "mars", { providerInstanceId: "pi-ted", providerInstanceName: "GitLab_TED" })] });
    renderDialog({ repositories: [{ id: "r1", fullPath: "acme/api" }, { id: "r2", fullPath: "acme/web" }] });

    const dialog = await screen.findByRole("dialog", { name: "Re-link credential" });

    expect((await within(dialog).findByText("mars")).closest("button")!.textContent).toMatch(/Moves these repositories to GitLab_TED/);
  });

  it("says nothing about a move for a credential on the repository's own connection, even when it is the only one the backend offers", async () => {
    stubApi({ "/relink-candidates": [candidate("c-here", "Maya's GitLab")] });
    renderDialog();

    const dialog = await screen.findByRole("dialog", { name: "Re-link credential" });
    const row = (await within(dialog).findByText("Maya's GitLab")).closest("button")!;

    expect(within(row).queryByText(/^on /)).toBeNull();
    expect(row.textContent).not.toMatch(/moves/);
  });

  it("says whose acting-as a move strands, by name, on the row that would strand it — and only on that row", async () => {
    stubApi({
      "/relink-candidates": [
        candidate("c-here", "Maya's GitLab"),
        candidate("c-there", "Maya's GitLab_TED", { providerInstanceId: "pi1b", providerInstanceName: "GitLab_TED", strandedActAs: ["Nacho", "Ada"] }),
        candidate("c-team-there", "Acme token on TED", { ownership: "TeamService", kind: "TeamServiceToken", providerInstanceId: "pi1b", providerInstanceName: "GitLab_TED", strandedActAs: [] }),
      ],
    });
    renderDialog();

    const dialog = await screen.findByRole("dialog", { name: "Re-link credential" });
    const rows = await within(dialog).findAllByRole("listitem");

    expect(rows[0].textContent).not.toMatch(/acting as/i);
    expect(rows[1].textContent).toContain("Acting as Nacho and Ada on this repository stops working until they connect GitLab_TED.");
    expect(rows[2].textContent, "a candidate that strands nobody says nothing of it").not.toMatch(/acting as/i);
  });

  it("shortens a long list of stranded people to three names and a count", async () => {
    stubApi({ "/relink-candidates": [candidate("c-there", "Maya's GitLab_TED", { providerInstanceId: "pi1b", providerInstanceName: "GitLab_TED", strandedActAs: ["Ada", "Bo", "Cy", "Di", "Ed"] })] });
    renderDialog();

    const dialog = await screen.findByRole("dialog", { name: "Re-link credential" });

    expect((await within(dialog).findByText("Maya's GitLab_TED")).closest("button")!.textContent).toContain("Acting as Ada, Bo, Cy and 2 others on this repository stops working until they connect GitLab_TED.");
  });

  it("shows no connection names when every candidate is on the repository's own connection, and asks for no credential to leave when there is none", async () => {
    const calls = stubApi({ "/relink-candidates": [candidate("c-here", "Maya's GitLab"), candidate("c-team", "Acme group token", { ownership: "TeamService", kind: "TeamServiceToken" })] });
    renderDialog({ fromCredentialId: null });

    const dialog = await screen.findByRole("dialog", { name: "Re-link credential" });
    await waitFor(() => expect(within(dialog).getByText("Acme group token")).toBeTruthy());

    expect(within(dialog).queryByText(/^on /)).toBeNull();
    expect(within(dialog).getByText("Team service")).toBeTruthy();
    expect(calls.map((c) => c.url).find((u) => u.includes("/relink-candidates"))).toMatch(/\/api\/provider-instances\/pi1\/relink-candidates$/);
  });

  it("flags the connection made before the old one was disconnected", async () => {
    stubApi({ "/relink-candidates": [candidate("c-here", "Maya's GitLab", { connectedBeforeDisconnect: true })] });
    renderDialog();

    const dialog = await screen.findByRole("dialog", { name: "Re-link credential" });

    await waitFor(() => expect(within(dialog).getByText("connected before the old one was disconnected")).toBeTruthy());
  });

  it("says nothing can take the repository only when the backend offers nothing", async () => {
    stubApi({ "/relink-candidates": [] });
    const onOpenProviders = vi.fn();
    renderDialog({ onOpenProviders });

    const dialog = await screen.findByRole("dialog", { name: "Re-link credential" });
    await waitFor(() => expect(within(dialog).getByText("No other working credential")).toBeTruthy());

    fireEvent.click(within(dialog).getByRole("button", { name: /Open Providers/ }));
    expect(onOpenProviders).toHaveBeenCalled();
  });

  it("shows a refusal from the move and stays open, so another credential can be picked", async () => {
    stubApi({
      "/relink-candidates": [candidate("c-there", "Maya's GitLab_TED", { providerInstanceId: "pi1b", providerInstanceName: "GitLab_TED" })],
      "/api/repositories/r1/relink-credential": { status: 409, body: { code: "invalid_operation", message: "Credential 'Maya's GitLab_TED' cannot read 'acme/api'. Nothing was moved." } },
    });
    const { onClose, onRelinked } = renderDialog();

    const dialog = await screen.findByRole("dialog", { name: "Re-link credential" });
    fireEvent.click(await within(dialog).findByText("Maya's GitLab_TED"));

    await waitFor(() => expect(within(dialog).getByRole("alert").textContent).toContain("cannot read 'acme/api'"));
    expect(onClose).not.toHaveBeenCalled();
    expect(onRelinked).not.toHaveBeenCalled();
    expect(within(dialog).getByText("Maya's GitLab_TED")).toBeTruthy();
  });

  it("shows an error that is not a missing route as an error, not as having no credential", async () => {
    stubApi({ "/relink-candidates": { status: 500, body: { code: "internal_error", message: "An unexpected error occurred." } } });
    renderDialog();

    const dialog = await screen.findByRole("dialog", { name: "Re-link credential" });

    await waitFor(() => expect(within(dialog).getByRole("alert").textContent).toContain("An unexpected error occurred."));
    expect(within(dialog).queryByText("No other working credential")).toBeNull();
  });
});

describe("RelinkDialog — against an API that predates the list", () => {
  it("answers the real older server — a 404 whose body says the route does not exist — with the list it builds itself, not with an error", async () => {
    const calls = stubApi({
      "/api/credentials/c-dead/relink-suggestions": [],
      "/api/credentials": credentialsOnTwoConnections,
    });
    renderDialog();

    const dialog = await screen.findByRole("dialog", { name: "Re-link credential" });
    await waitFor(() => expect(within(dialog).getByText("Maya's GitLab")).toBeTruthy());

    expect(within(dialog).queryByRole("alert"), "a missing route is not an error a person has to read").toBeNull();
    expect(within(dialog).getAllByRole("listitem")).toHaveLength(1);
    expect(calls.some((c) => c.url.includes("/relink-candidates"))).toBe(true);
  });

  it("builds the list itself: the repository's own connection's credentials the caller may bind with, ordered by the suggestions — and never another connection's", async () => {
    const calls = stubApi({
      "/api/credentials/c-dead/relink-suggestions": [{ credentialId: "c-here", displayName: "Maya's GitLab", authType: "OAuth", ownership: "Personal", kind: "YourConnection", createdDate: "2026-02-01T00:00:00Z", connectedBeforeDisconnect: true }],
      "/api/credentials": credentialsOnTwoConnections,
    });
    renderDialog();

    const dialog = await screen.findByRole("dialog", { name: "Re-link credential" });
    await waitFor(() => expect(within(dialog).getByText("Maya's GitLab")).toBeTruthy());

    expect(within(dialog).getAllByRole("listitem").map((e) => e.textContent)).toEqual([expect.stringContaining("Maya's GitLab")]);
    expect(within(dialog).queryByText("Maya's GitLab_TED"), "an older server cannot say a sibling connection's credential may take it").toBeNull();
    expect(within(dialog).getByText("connected before the old one was disconnected")).toBeTruthy();
    expect(calls.some((c) => c.url.includes("/relink-candidates"))).toBe(true);
    expect(calls.some((c) => c.url.includes("/api/credentials/c-dead/relink-suggestions"))).toBe(true);
  });
});

describe("RepositoryConnectionFix — the same list", () => {
  const issue: ConnectionIssue = { kind: "disconnected", label: "credential disconnected", title: "“GitLab · nacho” was disconnected." };

  it("opens the dialog for the repository's own connection and offers the credential on the sibling connection", async () => {
    const calls = stubApi({ "/relink-candidates": [candidate("c-there", "Maya's GitLab_TED", { providerInstanceId: "pi1b", providerInstanceName: "GitLab_TED" })], "/api/credentials": credentialsOnTwoConnections, "/api/provider-instances": [] });

    render(
      <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: 0 } } })}>
        <DialogProvider>
          <RepositoryConnectionFix repository={{ id: "r1", fullPath: "solar/OMEProject-Api", providerInstanceId: "pi1", credentialId: "c-dead", status: "Error", lastError: null }} issue={{ ...issue, credential: { ...credentialsOnTwoConnections[0], teamId: "t1" } as never }} />
        </DialogProvider>
      </QueryClientProvider>,
    );

    fireEvent.click(await screen.findByRole("button", { name: /Re-link credential/ }));

    const dialog = await screen.findByRole("dialog", { name: "Re-link credential" });
    await waitFor(() => expect(within(dialog).getByText("Maya's GitLab_TED")).toBeTruthy());

    expect(within(dialog).getByText("Re-link solar/OMEProject-Api")).toBeTruthy();
    expect(within(dialog).queryByText("No other working credential")).toBeNull();
    expect(calls.map((c) => c.url).find((u) => u.includes("/relink-candidates"))).toMatch(/provider-instances\/pi1\/relink-candidates\?fromCredentialId=c-dead$/);
  });
});
