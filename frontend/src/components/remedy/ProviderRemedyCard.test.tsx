import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

import type { MeTeam, ProviderInstanceSummary, ProviderRemedy, RemedyAction, RemedyActionKind } from "@/api/types";
import { DialogProvider } from "@/components/dialog";

import { ProviderRemedyCard } from "./ProviderRemedyCard";

/**
 * The remediation card renders the BACKEND's remedy: its words, its list of actions, and for each whether the person looking may press
 * it. It decides nothing about permission and maps no code to copy of its own — these tests hand it remedies shaped as the API sends them
 * (an Owner's, a Member's, a Viewer's, a fixed one) and pin that each button does what its label says, against the endpoint that does it.
 */
const team: MeTeam = { id: "t1", slug: "acme", name: "Acme", kind: "Workspace", role: "Admin", permissions: ["repos.manage", "credentials.manage"], memberCount: 3, repositoryCount: 1, projectCount: 1, workflowCount: 0 };
const instance: ProviderInstanceSummary = { id: "pi1", teamId: "t1", provider: "GitLab", displayName: "Acme GitLab", baseUrl: "http://gitlab.test", createdDate: "2026-01-01T00:00:00Z", oauthEnabled: true };

const action = (kind: RemedyActionKind, over: Partial<RemedyAction> = {}): RemedyAction => ({ kind, label: LABELS[kind], enabled: true, ...over });
const LABELS: Record<RemedyActionKind, string> = { relink: "Re-link repository", reconnect: "Reconnect GitLab", replace_token: "Replace token", edit_provider_url: "Edit provider address", open_providers: "Open Providers", rerun: "Re-run" };

const remedy = (over: Partial<ProviderRemedy> = {}): ProviderRemedy => ({
  code: "credential_needs_reconnect",
  title: "GitLab no longer accepts the credential this run used",
  cause: "GitLab refused to renew the sign-in behind “GitLab · nacho” so acme/api cannot authenticate.",
  resolved: false,
  credential: { id: "c-dead", displayName: "GitLab · nacho", ownership: "Personal", ownerUserId: "u9", ownerName: "Nacho", authType: "OAuth", status: "Error", health: "rejected", retired: false, providerInstanceId: "pi1" },
  repositories: [{ id: "r1", fullPath: "acme/api", status: "Error", credentialId: "c-dead", credentialWorks: false }],
  providerInstance: { id: "pi1", displayName: "Acme GitLab", provider: "GitLab", baseUrl: "http://gitlab.test" },
  actions: [action("reconnect"), action("relink"), action("open_providers"), action("rerun", { enabled: false, disabledReason: "Fix the connection first, then run it again." })],
  askPeople: [],
  ...over,
});

type Call = { method: string; url: string; body: unknown };

function renderCard(r: ProviderRemedy, props: { onRerun?: () => void; rawDetail?: string; onOpenRun?: (runId: string) => void } = {}, answers: Record<string, unknown> = {}) {
  localStorage.setItem("codespace.jwt", "test-jwt");
  localStorage.setItem("codespace.activeTeamId", team.id);

  const calls: Call[] = [];
  const credentials = [
    { id: "c-dead", teamId: "t1", providerInstanceId: "pi1", ownerUserId: "u9", ownerUserName: "Nacho", ownership: "Personal", authType: "OAuth", displayName: "GitLab · nacho", status: "Error", health: "rejected", createdDate: "2026-01-01T00:00:00Z" },
    { id: "c-mine", teamId: "t1", providerInstanceId: "pi1", ownerUserId: "u1", ownerUserName: "Maya", ownership: "Personal", authType: "OAuth", displayName: "Maya's GitLab", status: "Active", health: "active", createdDate: "2026-02-01T00:00:00Z" },
    { id: "c-team", teamId: "t1", providerInstanceId: "pi1", ownerUserId: null, ownerUserName: null, ownership: "TeamService", authType: "GroupAccessToken", displayName: "Acme group token", status: "Active", health: "active", createdDate: "2026-02-02T00:00:00Z" },
    { id: "c-ada", teamId: "t1", providerInstanceId: "pi1", ownerUserId: "u2", ownerUserName: "Ada", ownership: "Personal", authType: "Pat", displayName: "Ada's token", status: "Active", health: "active", createdDate: "2026-02-03T00:00:00Z" },
    { id: "c-other-host", teamId: "t1", providerInstanceId: "pi2", ownerUserId: "u1", ownerUserName: "Maya", ownership: "Personal", authType: "Pat", displayName: "Maya's GitHub", status: "Active", health: "active", createdDate: "2026-02-04T00:00:00Z" },
    { id: "c-revoked", teamId: "t1", providerInstanceId: "pi1", ownerUserId: "u1", ownerUserName: "Maya", ownership: "Personal", authType: "Pat", displayName: "Maya's old token", status: "Revoked", health: "revoked", createdDate: "2025-02-04T00:00:00Z" },
  ];
  // A test's own answers come first so a specific route wins over a prefix ("/api/credentials/oauth/init" over "/api/credentials"); one that names a base route replaces it.
  const base: Record<string, unknown> = {
    "/api/users/me": { id: "u1", email: "u@test.local", name: "Maya", teams: [team], permissions: [], passwordMustChange: false },
    "/relink-suggestions": [{ credentialId: "c-team", displayName: "Acme group token", authType: "GroupAccessToken", ownership: "TeamService", kind: "TeamServiceToken", createdDate: "2026-02-02T00:00:00Z", connectedBeforeDisconnect: false }],
    // The Re-link dialog's list, as the backend computes it: the caller's own and the team's tokens that may take the repository, in the order to offer them.
    "/relink-candidates": [
      { credentialId: "c-team", displayName: "Acme group token", authType: "GroupAccessToken", ownership: "TeamService", kind: "TeamServiceToken", createdDate: "2026-02-02T00:00:00Z", connectedBeforeDisconnect: false, providerInstanceId: "pi1", providerInstanceName: "Acme GitLab" },
      { credentialId: "c-mine", displayName: "Maya's GitLab", authType: "OAuth", ownership: "Personal", kind: "YourConnection", createdDate: "2026-02-01T00:00:00Z", connectedBeforeDisconnect: false, providerInstanceId: "pi1", providerInstanceName: "Acme GitLab" },
    ],
    "/capabilities": { credentialId: "any", grantedScopes: [], capabilities: [] },
    "/api/provider-instances": [instance],
    "/api/credentials": credentials,
  };
  const routes: Record<string, unknown> = { ...answers, ...Object.fromEntries(Object.entries(base).filter(([key]) => !(key in answers))) };

  vi.stubGlobal("fetch", vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = typeof input === "string" ? input : input.toString();
    calls.push({ method: init?.method ?? "GET", url, body: init?.body ? JSON.parse(String(init.body)) : undefined });

    const key = Object.keys(routes).find((k) => url.includes(k));
    const body = key === undefined ? undefined : routes[key];

    // A route a test leaves unanswered is one this server does not have, and the real server says so the way Startup's MapFallback does: a 404 WITH a
    // JSON body whose code is "not_found" — not an empty 404, which no CodeSpace API produces.
    if (body === undefined) return new Response(JSON.stringify({ code: "not_found", message: `This server has no ${new URL(url, "http://host").pathname}. Check the host — the API and the web app are different origins.` }), { status: 404, headers: { "Content-Type": "application/json" } });

    return new Response(JSON.stringify(body), { status: 200, headers: { "Content-Type": "application/json" } });
  }));

  render(
    <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: 0 } } })}>
      <DialogProvider><ProviderRemedyCard remedy={r} {...props} /></DialogProvider>
    </QueryClientProvider>,
  );

  return calls;
}

afterEach(() => { localStorage.clear(); vi.unstubAllGlobals(); vi.restoreAllMocks(); });

describe("ProviderRemedyCard — what it says", () => {
  it("shows the backend's title and cause, the credential and the repository the failure names, and each action", () => {
    renderCard(remedy(), { onRerun: vi.fn() });

    expect(screen.getByText("GitLab no longer accepts the credential this run used")).toBeTruthy();
    expect(screen.getByText(/refused to renew the sign-in behind/)).toBeTruthy();
    expect(screen.getByText("GitLab · nacho")).toBeTruthy();
    expect(screen.getByText("Nacho")).toBeTruthy();   // whose credential it is
    expect(screen.getByText("acme/api")).toBeTruthy();
    // Re-run is held back until the fix shows, so it is not a dead button beside the live ones.
    expect(screen.getAllByRole("button").map((b) => b.textContent?.trim())).toEqual(["Reconnect GitLab", "Re-link repository", "Open Providers"]);
  });

  it("leads with the first action that is the caller's, in the primary style", () => {
    renderCard(remedy());

    expect(screen.getByRole("button", { name: "Reconnect GitLab" }).className).toContain("room-btn-primary");
    expect(screen.getByRole("button", { name: "Re-link repository" }).className).not.toContain("room-btn-primary");
  });

  it("keeps a raw error behind a toggle, as the diagnostic it stands in for does", () => {
    renderCard(remedy(), { rawDetail: "The credential 'GitLab · nacho' (c-dead) cannot authenticate: its provider refused it (invalid_grant)." });

    expect(screen.queryByText(/invalid_grant/)).toBeNull();

    fireEvent.click(screen.getByRole("button", { name: /Show raw error/ }));

    expect(screen.getByText(/invalid_grant/)).toBeTruthy();
  });
});

describe("ProviderRemedyCard — a failure that happened in a sub-workflow", () => {
  const origin = { runId: "1a2b3c4d-0000-4000-8000-000000000000", workflowId: "w-child", workflowName: "Review the pull requests", nodeId: "list" };
  const carried = (over: Partial<ProviderRemedy> = {}) => remedy({
    cause: "This failed in the sub-workflow “Review the pull requests” (run 1a2b3c4d), at node 'list'. GitLab refused to renew the sign-in behind “GitLab · nacho”.",
    origin,
    ...over,
  });

  it("names the sub-workflow and its run, and opens that run from the link", () => {
    const onOpenRun = vi.fn();

    renderCard(carried(), { onRerun: vi.fn(), onOpenRun });

    const link = screen.getByRole("button", { name: "In sub-workflow Review the pull requests (run 1a2b3c4d)" });

    expect(link.getAttribute("title")).toBe("Open the sub-workflow run");

    fireEvent.click(link);

    expect(onOpenRun).toHaveBeenCalledTimes(1);
    expect(onOpenRun).toHaveBeenCalledWith(origin.runId);
  });

  it("names the run as text, with no link, where no run can be opened", () => {
    renderCard(carried());

    expect(screen.getByText("In sub-workflow Review the pull requests (run 1a2b3c4d)")).toBeTruthy();
    expect(screen.queryByRole("button", { name: /In sub-workflow/ })).toBeNull();
  });

  it("keeps the credential, the repository and every action exactly as the backend sent them, and Re-run is the card's own", () => {
    const onRerun = vi.fn();
    const onOpenRun = vi.fn();

    renderCard(carried({ resolved: true, actions: [action("rerun"), action("reconnect"), action("open_providers")] }), { onRerun, onOpenRun });

    expect(screen.getByText("GitLab · nacho")).toBeTruthy();
    expect(screen.getByText("acme/api")).toBeTruthy();
    expect(screen.getAllByRole("button").map((b) => b.textContent?.trim())).toEqual(["In sub-workflow Review the pull requests (run 1a2b3c4d)", "Re-run", "Reconnect GitLab", "Open Providers"]);   // the line that says where sits above the actions, which are the backend's list as sent

    fireEvent.click(screen.getByRole("button", { name: "Re-run" }));

    expect(onRerun).toHaveBeenCalledTimes(1);
    expect(onOpenRun).not.toHaveBeenCalled();   // re-running is the parent's: it never opens the child
  });

  it.each([undefined, null])("says nothing about a sub-workflow when the failure happened in the run itself (origin %s)", (value) => {
    renderCard(remedy({ origin: value }), { onOpenRun: vi.fn() });

    expect(screen.queryByText(/sub-workflow/i)).toBeNull();
    expect(screen.getAllByRole("button").map((b) => b.textContent?.trim())).toEqual(["Reconnect GitLab", "Re-link repository", "Open Providers"]);
  });

  it("names a run whose workflow has no node recorded exactly the same way", () => {
    renderCard(carried({ origin: { ...origin, nodeId: null } }));

    expect(screen.getByText("In sub-workflow Review the pull requests (run 1a2b3c4d)")).toBeTruthy();
  });
});

describe("ProviderRemedyCard — a failure that is nobody's credential to repair", () => {
  it("says what the provider did and offers only what the backend offered: no repair button for a renewal that was not answered", () => {
    renderCard(remedy({
      code: "oauth_exchange_failed",
      title: "Renewing the sign-in this run used did not work",
      cause: "GitLab did not renew the sign-in behind “GitLab · nacho” when acme/api needed it (it answered http_503). That says nothing about the credential itself.",
      actions: [action("open_providers"), action("rerun")],
    }), { onRerun: vi.fn() });

    expect(screen.getByText("Renewing the sign-in this run used did not work")).toBeTruthy();
    expect(screen.getByText(/it answered http_503/)).toBeTruthy();
    expect(screen.getAllByRole("button").map((b) => b.textContent?.trim())).toEqual(["Open Providers", "Re-run"]);   // in the order the backend gave, which is its judgement of what to try first
    expect(screen.queryByRole("note")).toBeNull();
  });

  it("a redirect the server would refuse to edit away shows its cause and no Edit provider address button", () => {
    renderCard(remedy({
      code: "provider_base_url_redirects",
      title: "The provider's address redirects somewhere else",
      cause: "Requests to http://gitlab.test are redirected to https://gitlab.example.com, which is another host. The provider's address cannot be changed while 1 credential is connected through it. Disconnect them first.",
      redirectsTo: "https://gitlab.example.com",
      credential: null,
      repositories: [],
      actions: [action("open_providers"), action("rerun", { enabled: false, disabledReason: "Fix the connection first, then run it again." })],
    }), { onRerun: vi.fn() });

    expect(screen.getByText(/Disconnect them first/)).toBeTruthy();
    expect(screen.queryByRole("button", { name: "Edit provider address" })).toBeNull();
  });
});

describe("ProviderRemedyCard — who may press what", () => {
  const memberView = remedy({
    actions: [
      action("reconnect", { enabled: false, disabledReason: "Only Nacho, or a team Admin, can do this." }),
      action("relink", { enabled: false, disabledReason: "Needs the Admin or Owner role." }),
      action("open_providers"),
      action("rerun", { enabled: false, disabledReason: "Fix the connection first, then run it again." }),
    ],
    askPeople: [{ userId: "u1", name: "Nacho", role: "Owner of the credential" }, { userId: "u3", name: "Olive", role: "Owner" }, { userId: "u2", name: "Ada", role: "Admin" }],
  });

  it("gives a Member no dead buttons: what is not theirs is a line saying which fix, what it takes and who to ask", () => {
    renderCard(memberView, { onRerun: vi.fn() });

    expect(screen.getAllByRole("button").map((b) => b.textContent?.trim())).toEqual(["Open Providers"]);   // no disabled Reconnect, Re-link or Re-run

    const ask = screen.getByRole("note");
    expect(ask.textContent).toContain("Reconnect GitLab — Only Nacho, or a team Admin, can do this.");
    expect(ask.textContent).toContain("Re-link repository — Needs the Admin or Owner role.");
    expect(ask.textContent).toContain("Ask Nacho (Owner of the credential), Olive (Owner) or Ada (Admin).");
  });

  it("tells an Admin an Owner's sign-in is the Owner's alone, keeps their re-link, and names the Owner — not a peer who could not do it either", () => {
    // An Admin holds credentials.manage and still cannot sign in as an Owner; what the server sends is shown as sent.
    renderCard(remedy({
      actions: [
        action("reconnect", { enabled: false, disabledReason: "Only Nacho can reconnect their own sign-in." }),
        action("relink"),
        action("open_providers"),
        action("rerun", { enabled: false, disabledReason: "Fix the connection first, then run it again." }),
      ],
      askPeople: [{ userId: "u9", name: "Nacho", role: "Owner of the credential" }],
    }));

    expect(screen.getAllByRole("button").filter((b) => !(b as HTMLButtonElement).disabled).map((b) => b.textContent?.trim())).toEqual(["Re-link repository", "Open Providers"]);

    const ask = screen.getByRole("note");
    expect(ask.textContent).toContain("Reconnect GitLab — Only Nacho can reconnect their own sign-in.");
    expect(ask.textContent).toContain("Ask Nacho (Owner of the credential).");
  });

  it("says nothing about who to ask when every fix is the caller's", () => {
    renderCard(remedy({ askPeople: [] }));

    expect(screen.queryByRole("note")).toBeNull();
  });

  it("does not offer Providers as the primary action when it is all the caller can do", () => {
    renderCard(memberView);

    expect(screen.getByRole("button", { name: "Open Providers" }).className).not.toContain("room-btn-primary");
  });
});

describe("ProviderRemedyCard — the same failure for an Owner, a Member and a Viewer", () => {
  const forRole = (reconnect: boolean, relink: boolean, askPeople: ProviderRemedy["askPeople"]) => remedy({
    actions: [
      action("reconnect", { enabled: reconnect, disabledReason: reconnect ? null : "Only Nacho, or a team Admin, can do this." }),
      action("relink", { enabled: relink, disabledReason: relink ? null : "Needs the Admin or Owner role." }),
      action("open_providers"),
    ],
    askPeople,
  });
  const who = [{ userId: "u3", name: "Olive", role: "Owner" }, { userId: "u2", name: "Ada", role: "Admin" }];

  it.each([
    ["Owner", forRole(true, true, []), ["Reconnect GitLab", "Re-link repository", "Open Providers"], false],
    ["Member", forRole(false, false, who), ["Open Providers"], true],
    ["Viewer", forRole(false, false, who), ["Open Providers"], true],
    ["Member whose credential it is", forRole(true, false, who), ["Reconnect GitLab", "Open Providers"], true],   // the credential's own person reconnects it; re-linking the repository is still not theirs
  ] as const)("%s: presses only what is enabled, and is told who to ask when something is not theirs", (_role, r, enabledButtons, asked) => {
    renderCard(r);

    const enabled = screen.getAllByRole("button").filter((b) => !(b as HTMLButtonElement).disabled).map((b) => b.textContent?.trim());

    expect(enabled).toEqual(enabledButtons);
    expect(screen.queryByRole("note") !== null).toBe(asked);
  });

  it("a retired credential leaves re-linking the only repair and says how many repositories it moves", () => {
    renderCard(remedy({
      credential: { ...remedy().credential!, retired: true, status: "Revoked", health: "revoked" },
      repositories: [{ id: "r1", fullPath: "acme/api", status: "Error", credentialId: "c-dead", credentialWorks: false }, { id: "r2", fullPath: "acme/web", status: "Error", credentialId: "c-dead", credentialWorks: false }],
      actions: [action("relink", { label: "Re-link repositories" }), action("open_providers")],
    }));

    expect(screen.getAllByRole("button").map((b) => b.textContent?.trim())).toEqual(["Re-link repositories", "Open Providers"]);
  });

  it("re-linking several repositories moves each of them", async () => {
    const calls = renderCard(remedy({
      repositories: [{ id: "r1", fullPath: "acme/api", status: "Error", credentialId: "c-dead", credentialWorks: false }, { id: "r2", fullPath: "acme/web", status: "Error", credentialId: "c-dead", credentialWorks: false }],
      actions: [action("relink", { label: "Re-link repositories" })],
    }), {}, { "/relink-credential": {} });

    fireEvent.click(screen.getByRole("button", { name: "Re-link repositories" }));

    const dialog = await screen.findByRole("dialog", { name: "Re-link credential" });
    await waitFor(() => expect(within(dialog).getByText("Acme group token")).toBeTruthy());
    expect(within(dialog).getByText("Re-link 2 repositories")).toBeTruthy();

    fireEvent.click(within(dialog).getByText("Acme group token"));

    await waitFor(() => expect(calls.filter((c) => c.url.endsWith("/relink-credential"))).toHaveLength(2));
    expect(calls.filter((c) => c.url.endsWith("/relink-credential")).map((c) => c.url.match(/repositories\/([^/]+)\//)?.[1])).toEqual(["r1", "r2"]);
  });

  it("only moves the repositories that still need it", async () => {
    const calls = renderCard(remedy({
      repositories: [{ id: "r1", fullPath: "acme/api", status: "Error", credentialId: "c-dead", credentialWorks: false }, { id: "r2", fullPath: "acme/web", status: "Active", credentialId: "c-team", credentialWorks: true }],
      actions: [action("relink")],
    }), {}, { "/relink-credential": {} });

    fireEvent.click(screen.getByRole("button", { name: "Re-link repository" }));

    const dialog = await screen.findByRole("dialog", { name: "Re-link credential" });
    await waitFor(() => expect(within(dialog).getByText("Acme group token")).toBeTruthy());
    fireEvent.click(within(dialog).getByText("Acme group token"));

    await waitFor(() => expect(calls.some((c) => c.url.endsWith("/relink-credential"))).toBe(true));
    expect(calls.filter((c) => c.url.endsWith("/relink-credential"))).toHaveLength(1);
  });
});

describe("ProviderRemedyCard — each button does what it says", () => {
  it("Reconnect signs in again to the SAME credential, so nothing bound through it has to move", async () => {
    vi.spyOn(window, "open").mockReturnValue(null);   // a blocked popup ends the flow right after the init request, which is the request under test
    const calls = renderCard(remedy(), {}, { "/api/credentials/oauth/init": { authorizeUrl: "https://gitlab.test/oauth/authorize", state: "s" } });

    fireEvent.click(screen.getByRole("button", { name: "Reconnect GitLab" }));

    await waitFor(() => expect(calls.some((c) => c.url.endsWith("/api/credentials/oauth/init"))).toBe(true));
    const init = calls.find((c) => c.url.endsWith("/api/credentials/oauth/init"))!;
    expect(init.method).toBe("POST");
    expect(init.body).toMatchObject({ providerInstanceId: "pi1", displayName: "GitLab · nacho", replaceCredentialId: "c-dead" });
  });

  it("Replace token puts a pasted token into the same credential", async () => {
    const calls = renderCard(
      remedy({ credential: { ...remedy().credential!, authType: "Pat" }, actions: [action("replace_token"), action("open_providers")] }),
      {},
      { "/api/credentials/c-dead/token": { credentialId: "c-dead", providerUsername: "nacho" } },
    );

    fireEvent.click(screen.getByRole("button", { name: "Replace token" }));

    const dialog = await screen.findByRole("dialog", { name: "Replace token" });
    fireEvent.change(within(dialog).getByPlaceholderText(/glpat/i), { target: { value: "glpat-fresh" } });
    fireEvent.click(within(dialog).getByRole("button", { name: "Replace token" }));

    await waitFor(() => expect(calls.some((c) => c.method === "PUT")).toBe(true));
    const put = calls.find((c) => c.method === "PUT")!;
    expect(put.url).toMatch(/\/api\/credentials\/c-dead\/token$/);
    expect(put.body).toEqual({ token: "glpat-fresh" });
  });

  it("Re-link offers what the backend says may take the repository, in its order, and moves the repository", async () => {
    const calls = renderCard(remedy(), {}, { "/api/repositories/r1/relink-credential": {} });

    fireEvent.click(screen.getByRole("button", { name: "Re-link repository" }));

    const dialog = await screen.findByRole("dialog", { name: "Re-link credential" });
    await waitFor(() => expect(within(dialog).getByText("Acme group token")).toBeTruthy());

    const offered = within(dialog).getAllByRole("listitem").map((e) => e.textContent);
    expect(offered).toEqual([expect.stringContaining("Acme group token"), expect.stringContaining("Maya's GitLab")]);   // the backend's list, untouched: the dialog drops and reorders nothing

    fireEvent.click(within(dialog).getByText("Acme group token"));

    await waitFor(() => expect(calls.some((c) => c.method === "POST" && c.url.endsWith("/api/repositories/r1/relink-credential"))).toBe(true));
    expect(calls.find((c) => c.url.endsWith("/api/repositories/r1/relink-credential"))!.body).toEqual({ newCredentialId: "c-team" });
    expect(calls.some((c) => c.url.includes("/api/provider-instances/pi1/relink-candidates?fromCredentialId=c-dead"))).toBe(true);
    expect(calls.some((c) => c.url.includes("/relink-suggestions"))).toBe(false);
    await waitFor(() => expect(screen.queryByRole("dialog", { name: "Re-link credential" })).toBeNull());
  });

  it("Re-link against an API that predates the list offers only the caller's own and the team's credentials on the repository's provider, best suggestion first", async () => {
    const calls = renderCard(remedy(), {}, { "/relink-candidates": undefined, "/api/repositories/r1/relink-credential": {} });

    fireEvent.click(screen.getByRole("button", { name: "Re-link repository" }));

    const dialog = await screen.findByRole("dialog", { name: "Re-link credential" });
    await waitFor(() => expect(within(dialog).getByText("Acme group token")).toBeTruthy());

    const offered = within(dialog).getAllByRole("listitem").map((e) => e.textContent);
    expect(offered).toEqual([expect.stringContaining("Acme group token"), expect.stringContaining("Maya's GitLab")]);   // the suggestion first; never Ada's personal token, the dead one, a revoked one or another host's
    expect(within(dialog).queryByText("Ada's token")).toBeNull();
    expect(within(dialog).queryByText("Maya's GitHub")).toBeNull();

    fireEvent.click(within(dialog).getByText("Acme group token"));

    await waitFor(() => expect(calls.some((c) => c.method === "POST" && c.url.endsWith("/api/repositories/r1/relink-credential"))).toBe(true));
    expect(calls.find((c) => c.url.endsWith("/api/repositories/r1/relink-credential"))!.body).toEqual({ newCredentialId: "c-team" });
    expect(calls.some((c) => c.url.includes("/api/credentials/c-dead/relink-suggestions"))).toBe(true);
    await waitFor(() => expect(screen.queryByRole("dialog", { name: "Re-link credential" })).toBeNull());
  });

  it("Re-link says what to do when no credential can take the repository", async () => {
    renderCard(remedy(), {}, { "/relink-candidates": [] });

    fireEvent.click(screen.getByRole("button", { name: "Re-link repository" }));

    const dialog = await screen.findByRole("dialog", { name: "Re-link credential" });
    await waitFor(() => expect(within(dialog).getByText("No other working credential")).toBeTruthy());
    expect(within(dialog).getByRole("button", { name: /Open Providers/ })).toBeTruthy();
  });

  it("Edit provider address opens the provider's edit form starting at the address it answered on", async () => {
    renderCard(remedy({
      code: "provider_base_url_redirects",
      title: "The provider's address redirects somewhere else",
      cause: "Requests to http://gitlab.test are redirected to https://gitlab.test.",
      credential: null, repositories: [], redirectsTo: "https://gitlab.test",
      actions: [action("edit_provider_url"), action("open_providers"), action("rerun", { enabled: false, disabledReason: "Fix the connection first, then run it again." })],
    }));

    fireEvent.click(screen.getByRole("button", { name: "Edit provider address" }));

    const dialog = await screen.findByRole("dialog");
    await waitFor(() => expect(within(dialog).getByText(/Edit GitLab provider/)).toBeTruthy());
    expect((within(dialog).getByDisplayValue("https://gitlab.test") as HTMLInputElement).value).toBe("https://gitlab.test");   // not the stored http:// one
  });

  it("Open Providers opens the Providers dialog", async () => {
    renderCard(remedy());

    fireEvent.click(screen.getByRole("button", { name: "Open Providers" }));

    const dialog = await screen.findByRole("dialog");
    await waitFor(() => expect(within(dialog).getByText("Providers")).toBeTruthy());
  });
});

describe("ProviderRemedyCard — once it is fixed", () => {
  const fixed = remedy({
    resolved: true,
    title: "This connection works again",
    cause: "“GitLab · nacho” works again, so acme/api can authenticate. Run it again to pick up where this stopped.",
    actions: [action("rerun"), action("reconnect"), action("open_providers")],
    repositories: [{ id: "r1", fullPath: "acme/api", status: "Active", credentialId: "c-dead", credentialWorks: true }],
  });

  it("says so, puts Re-run first, and runs it again", () => {
    const onRerun = vi.fn();
    renderCard(fixed, { onRerun });

    expect(screen.getByRole("status").textContent).toContain("This connection works again");
    expect(screen.getAllByRole("button")[0].textContent).toContain("Re-run");
    expect(screen.getAllByRole("button")[0].className).toContain("room-btn-primary");

    fireEvent.click(screen.getByRole("button", { name: "Re-run" }));

    expect(onRerun).toHaveBeenCalledOnce();
  });

  it("offers no Re-run where there is no run behind the card (a refused delivery started none)", () => {
    renderCard(fixed);

    expect(screen.queryByRole("button", { name: "Re-run" })).toBeNull();
  });

  it("holds Re-run back until the fix shows — there is no button for it, because nothing about the run changes until the connection does", () => {
    renderCard(remedy(), { onRerun: vi.fn() });

    expect(screen.queryByRole("button", { name: "Re-run" })).toBeNull();
  });
});
