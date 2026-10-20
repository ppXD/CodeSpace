import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { act, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

import type { MeResponse, MeTeam, ProviderInstanceSummary } from "@/api/types";
import { DialogProvider } from "@/components/dialog";
import { ConnectRemoteModal } from "@/_imported/ai-code-space/connect-remote-modal";

// Not co-located with the source: vite.config.ts excludes `**/_imported/**` from test discovery.

/**
 * Revoking a team service token takes credentials.manage; disconnecting your OWN personal credential takes nothing
 * beyond membership. The modal must offer exactly that: a Member keeps Disconnect on the Personal tab — which lists
 * only their own credentials — and sees team tokens without a Revoke that could only answer 403.
 *
 * <p>The same for connecting: adding a team token and starting a NEW OAuth connect (which mints a credential) take
 * credentials.manage, and editing or removing a provider takes repos.manage. Linking a personal access token takes only
 * membership, so "Use a token" stays for everyone. Reconnecting a credential you own is not a new connect — see below.</p>
 */
describe("connect remote modal credential controls", () => {
  const team = (role: MeTeam["role"], permissions: string[]): MeTeam => ({
    id: "t1", slug: "acme", name: "Acme", kind: "Workspace", role, permissions,
    memberCount: 3, repositoryCount: 0, projectCount: 1, workflowCount: 0,
  });

  const me = (t: MeTeam): MeResponse => ({ id: "u1", email: "u@test.local", name: "Maya", teams: [t], permissions: [], passwordMustChange: false });

  const instance: ProviderInstanceSummary = { id: "pi1", teamId: "t1", provider: "GitLab", displayName: "Acme GitLab", baseUrl: "https://gitlab.test", createdDate: "2026-01-01T00:00:00Z", oauthEnabled: true };

  const credentials = [
    { id: "own", teamId: "t1", providerInstanceId: "pi1", ownerUserId: "u1", ownerUserName: "Maya", ownership: "Personal", authType: "Pat", displayName: "Maya's token", status: "Active", createdDate: "2026-01-01T00:00:00Z" },
    { id: "team", teamId: "t1", providerInstanceId: "pi1", ownerUserId: null, ownerUserName: null, ownership: "TeamService", authType: "GroupAccessToken", displayName: "Acme group token", status: "Active", createdDate: "2026-01-01T00:00:00Z" },
  ];

  function renderAs(t: MeTeam, held = credentials, specific: Record<string, unknown> = {}) {
    localStorage.setItem("codespace.jwt", "test-jwt");
    localStorage.setItem("codespace.activeTeamId", t.id);

    // `specific` routes come first: a route is matched by substring, in order, and "/api/credentials" is a prefix of every credential URL.
    const routes: Record<string, unknown> = { ...specific, "/api/users/me": me(t), "/capabilities": { warnings: [] }, "/api/provider-instances": [instance], "/api/credentials": held };

    vi.stubGlobal("fetch", vi.fn(async (input: RequestInfo | URL) => {
      const url = typeof input === "string" ? input : input.toString();
      const key = Object.keys(routes).find((k) => url.includes(k));
      const body = key === undefined ? undefined : routes[key];
      return new Response(body === undefined ? "" : JSON.stringify(body), { status: body === undefined ? 404 : 200, headers: { "Content-Type": "application/json" } });
    }));

    const client = new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: 0 } } });

    return render(
      <QueryClientProvider client={client}>
        <DialogProvider><ConnectRemoteModal onClose={() => {}} /></DialogProvider>
      </QueryClientProvider>,
    );
  }

  afterEach(() => { localStorage.clear(); vi.unstubAllGlobals(); });

  it.each([
    ["Viewer", []],
    ["Member", ["workflows.write"]],
  ] as const)("lets a %s disconnect their own credential but offers no revoke on a team token", async (role, permissions) => {
    renderAs(team(role, [...permissions]));

    await waitFor(() => expect(screen.getByRole("button", { name: "Disconnect" })).toBeTruthy());

    fireEvent.click(screen.getByRole("tab", { name: "Team" }));

    await waitFor(() => expect(screen.getByText("Acme group token")).toBeTruthy());
    expect(screen.queryByRole("button", { name: "Revoke" })).toBeNull();
  });

  it.each([
    ["Viewer", []],
    ["Member", ["workflows.write"]],
  ] as const)("offers a %s a personal token to connect, and nothing that would answer 403", async (role, permissions) => {
    renderAs(team(role, [...permissions]), credentials.filter((c) => c.ownership === "TeamService"));

    await waitFor(() => expect(screen.getByRole("button", { name: /Use a token/ })).toBeTruthy());

    expect(screen.queryByRole("button", { name: /Connect with OAuth/ })).toBeNull();
    expect(screen.queryByTitle("More actions")).toBeNull();

    fireEvent.click(screen.getByRole("tab", { name: "Team" }));

    await waitFor(() => expect(screen.getByText("Acme group token")).toBeTruthy());
    expect(screen.queryByRole("button", { name: /Add team token/ })).toBeNull();
  });

  it("gives an admin OAuth connect, the provider menu and a team token to add", async () => {
    renderAs(team("Admin", ["repos.manage", "credentials.manage", "members.manage"]), credentials.filter((c) => c.ownership === "TeamService"));

    await waitFor(() => expect(screen.getByRole("button", { name: /Connect with OAuth/ })).toBeTruthy());

    expect(screen.getByRole("button", { name: /Use a token/ })).toBeTruthy();
    expect(screen.getByTitle("More actions")).toBeTruthy();

    fireEvent.click(screen.getByRole("tab", { name: "Team" }));

    await waitFor(() => expect(screen.getByRole("button", { name: /Add team token/ })).toBeTruthy());
  });

  /**
   * The confirm dialog quotes the usage preview. With a Disconnect that carries the repositories to the person's one other
   * connection, a warning that they "will be marked as needing a new credential" is about something that will not happen;
   * and an API that predates the move sends no such fields, so the old wording stays for it.
   */
  it.each([
    ["moves them", { credentialId: "own", activeRepositoryCount: 0, willMoveRepositoryCount: 2, willMoveToDisplayName: "Maya's OAuth" }, ["2 repositories bound through it will move to your other connection, Maya's OAuth, and keep working."], ["needing a new credential"]],
    ["moves the only one", { credentialId: "own", activeRepositoryCount: 0, willMoveRepositoryCount: 1, willMoveToDisplayName: "Maya's OAuth" }, ["1 repository bound through it will move to your other connection"], ["needing a new credential"]],
    ["would break them", { credentialId: "own", activeRepositoryCount: 3, willMoveRepositoryCount: 0, willMoveToDisplayName: null }, ["3 repositories bound through this credential will be marked as needing a new credential"], ["will move to"]],
    ["comes from an API that predates the move", { credentialId: "own", activeRepositoryCount: 1 }, ["1 repository bound through this credential will be marked as needing a new credential"], ["will move to"]],
    ["has nothing bound", { credentialId: "own", activeRepositoryCount: 0, willMoveRepositoryCount: 0, willMoveToDisplayName: null }, ["This credential will be revoked. You can reconnect at any time."], ["needing a new credential", "will move to"]],
  ] as const)("tells the disconnect confirm what the disconnect will do when it %s", async (_case, usage, expected, absent) => {
    renderAs(team("Member", ["workflows.write"]), credentials, { "/api/credentials/own/usage": usage, "/api/credentials/own/capabilities": { credentialId: "own", grantedScopes: [], capabilities: [] } });

    await waitFor(() => expect(screen.getByRole("button", { name: "Disconnect" })).toBeTruthy());

    fireEvent.click(screen.getByRole("button", { name: "Disconnect" }));

    const dialog = await screen.findByRole("alertdialog");
    for (const text of expected) expect(dialog.textContent).toContain(text);
    for (const text of absent) expect(dialog.textContent).not.toContain(text);
  });

  it("gives an admin revoke on a team token", async () => {
    renderAs(team("Admin", ["repos.manage", "credentials.manage", "members.manage"]));

    await waitFor(() => expect(screen.getByRole("button", { name: "Disconnect" })).toBeTruthy());

    fireEvent.click(screen.getByRole("tab", { name: "Team" }));

    await waitFor(() => expect(screen.getByRole("button", { name: "Revoke" })).toBeTruthy());
  });
});

/**
 * A credential that stopped working is brought back IN PLACE: Reconnect signs in again to the same credential (its id goes
 * to the API as replaceCredentialId, so what is bound through it needs no moving), and Replace token pastes a new token into
 * it. Both are the credential's own person's on membership alone, or credentials.manage's for anyone else's: the Personal tab lists
 * only the caller's own credentials, so both are always offered there whatever the role. A plain Connect with OAuth mints a
 * credential and stays credentials.manage's.
 */
describe("connect remote modal re-authorizing a credential in place", () => {
  const admin: MeTeam = {
    id: "t1", slug: "acme", name: "Acme", kind: "Workspace", role: "Admin", permissions: ["repos.manage", "credentials.manage", "members.manage"],
    memberCount: 3, repositoryCount: 0, projectCount: 1, workflowCount: 0,
  };
  const member: MeTeam = { ...admin, role: "Member", permissions: ["workflows.write"] };

  const instance: ProviderInstanceSummary = { id: "pi1", teamId: "t1", provider: "GitLab", displayName: "Acme GitLab", baseUrl: "https://gitlab.test", createdDate: "2026-01-01T00:00:00Z", oauthEnabled: true };
  const credential = (id: string, authType: string, extra: Record<string, unknown> = {}) => ({ id, teamId: "t1", providerInstanceId: "pi1", ownerUserId: "u1", ownerUserName: "Maya", ownership: "Personal", authType, displayName: `Maya's ${id}`, status: "Active", createdDate: "2026-01-01T00:00:00Z", ...extra });

  type Call = { method: string; url: string; body: unknown };

  function renderAs(team: MeTeam, held: unknown[], answers: Record<string, unknown> = {}) {
    localStorage.setItem("codespace.jwt", "test-jwt");
    localStorage.setItem("codespace.activeTeamId", team.id);

    const calls: Call[] = [];
    const routes: Record<string, unknown> = { ...answers, "/api/users/me": { id: "u1", email: "u@test.local", name: "Maya", teams: [team], permissions: [], passwordMustChange: false }, "/capabilities": { credentialId: "any", grantedScopes: [], capabilities: [] }, "/api/provider-instances": [instance], "/api/credentials": held };

    vi.stubGlobal("fetch", vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = typeof input === "string" ? input : input.toString();
      calls.push({ method: init?.method ?? "GET", url, body: init?.body ? JSON.parse(String(init.body)) : undefined });

      const key = Object.keys(routes).find((k) => url.includes(k));
      const body = key === undefined ? undefined : routes[key];
      return new Response(body === undefined ? "" : JSON.stringify(body), { status: body === undefined ? 404 : 200, headers: { "Content-Type": "application/json" } });
    }));

    const client = new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: 0 } } });

    render(
      <QueryClientProvider client={client}>
        <DialogProvider><ConnectRemoteModal onClose={() => {}} /></DialogProvider>
      </QueryClientProvider>,
    );

    return calls;
  }

  afterEach(() => { localStorage.clear(); vi.unstubAllGlobals(); vi.restoreAllMocks(); });

  /** A working sign-in keeps no Reconnect button of its own: re-authorizing it is in its row's overflow menu. */
  async function reauthorizeFromTheMenu() {
    await waitFor(() => expect(screen.getByRole("button", { name: /More actions for/ })).toBeTruthy());
    fireEvent.click(screen.getByRole("button", { name: /More actions for/ }));
    fireEvent.click(await screen.findByRole("button", { name: "Re-authorize" }));
  }

  it("starts the OAuth flow naming the credential, under the credential's own name", async () => {
    // A blocked popup ends the flow right after the init request, which is the request under test.
    vi.spyOn(window, "open").mockReturnValue(null);
    const calls = renderAs(admin, [credential("oauth-1", "OAuth")], { "/api/credentials/oauth/init": { authorizeUrl: "https://gitlab.test/oauth/authorize", state: "s" } });

    await reauthorizeFromTheMenu();

    await waitFor(() => expect(calls.some((c) => c.url.endsWith("/api/credentials/oauth/init"))).toBe(true));
    const init = calls.find((c) => c.url.endsWith("/api/credentials/oauth/init"))!;
    expect(init.method).toBe("POST");
    expect(init.body).toMatchObject({ providerInstanceId: "pi1", displayName: "Maya's oauth-1", replaceCredentialId: "oauth-1" });
  });

  it("starts a plain connect, naming nothing, from Connect with OAuth", async () => {
    vi.spyOn(window, "open").mockReturnValue(null);
    const calls = renderAs(admin, [], { "/api/credentials/oauth/init": { authorizeUrl: "https://gitlab.test/oauth/authorize", state: "s" } });

    await waitFor(() => expect(screen.getByRole("button", { name: /Connect with OAuth/ })).toBeTruthy());
    fireEvent.click(screen.getByRole("button", { name: /Connect with OAuth/ }));

    await waitFor(() => expect(calls.some((c) => c.url.endsWith("/api/credentials/oauth/init"))).toBe(true));
    const init = calls.find((c) => c.url.endsWith("/api/credentials/oauth/init"))!;
    expect(init.body).toMatchObject({ providerInstanceId: "pi1", displayName: "Maya's Acme GitLab" });
    expect(JSON.stringify(init.body)).not.toContain("replaceCredentialId");
  });

  it.each([
    ["Member", member],
    ["Viewer", { ...admin, role: "Viewer", permissions: [] } as MeTeam],
  ] as const)("offers a %s Re-authorize on their own sign-in and Replace token on their own token: the credential's own person passes on membership", async (_role, asker) => {
    vi.spyOn(window, "open").mockReturnValue(null);
    const calls = renderAs(asker, [credential("oauth-1", "OAuth"), credential("pat-1", "Pat")], { "/api/credentials/oauth/init": { authorizeUrl: "https://gitlab.test/oauth/authorize", state: "s" } });

    await waitFor(() => expect(screen.getAllByRole("button", { name: "Disconnect" })).toHaveLength(2));

    expect(screen.getAllByRole("button", { name: "Replace token" })).toHaveLength(1);
    expect(screen.queryByRole("button", { name: "Reconnect" })).toBeNull();   // a working sign-in is re-authorized from its menu
    await reauthorizeFromTheMenu();

    await waitFor(() => expect(calls.some((c) => c.url.endsWith("/api/credentials/oauth/init"))).toBe(true));
    expect(calls.find((c) => c.url.endsWith("/api/credentials/oauth/init"))!.body).toMatchObject({ providerInstanceId: "pi1", replaceCredentialId: "oauth-1" });
  });

  it("still offers a member no plain Connect with OAuth, which would mint a credential and answer 403", async () => {
    renderAs(member, []);

    await waitFor(() => expect(screen.getByRole("button", { name: /Use a token/ })).toBeTruthy());

    expect(screen.queryByRole("button", { name: /Connect with OAuth/ })).toBeNull();
  });

  it("puts a pasted token into the credential in place and closes", async () => {
    const calls = renderAs(member, [credential("pat-1", "Pat")], { "/api/credentials/pat-1/token": { credentialId: "pat-1", providerUsername: "nacho" } });

    await waitFor(() => expect(screen.getByRole("button", { name: "Replace token" })).toBeTruthy());
    fireEvent.click(screen.getByRole("button", { name: "Replace token" }));

    const dialog = await screen.findByRole("dialog", { name: "Replace token" });
    fireEvent.change(within(dialog).getByPlaceholderText(/glpat/i), { target: { value: "glpat-fresh" } });
    fireEvent.click(within(dialog).getByRole("button", { name: "Replace token" }));

    await waitFor(() => expect(calls.some((c) => c.method === "PUT")).toBe(true));
    const put = calls.find((c) => c.method === "PUT")!;
    expect(put.url).toMatch(/\/api\/credentials\/pat-1\/token$/);
    expect(put.body).toEqual({ token: "glpat-fresh" });
    await waitFor(() => expect(screen.queryByPlaceholderText(/glpat/i)).toBeNull());
  });

  it("keeps a credential the provider refused in the list, marked, with the action that brings it back", async () => {
    renderAs(admin, [credential("oauth-1", "OAuth", { status: "Error", lastError: "OAuth exchange rejected: invalid_grant" }), credential("pat-1", "Pat", { status: "Error" })]);

    await waitFor(() => expect(screen.getByRole("button", { name: "Reconnect" })).toBeTruthy());

    expect(screen.getByText("needs reconnect").closest("span")?.getAttribute("title")).toBe("OAuth exchange rejected: invalid_grant");
    expect(screen.getByText("needs a new token")).toBeTruthy();
    expect(screen.getByRole("button", { name: "Replace token" })).toBeTruthy();
    expect(screen.getAllByRole("button", { name: "Disconnect" })).toHaveLength(2);
    expect(screen.queryByRole("button", { name: /Connect with OAuth/ })).toBeNull();
  });

  it("lists a disconnected credential too, marked, with how many repositories are still bound and a way back — and no second Disconnect", async () => {
    renderAs(admin, [credential("oauth-1", "OAuth", { status: "Revoked", boundRepositoryCount: 3 }), credential("pat-1", "Pat", { status: "Revoked", boundRepositoryCount: 1 })]);

    await waitFor(() => expect(screen.getAllByText("disconnected")).toHaveLength(2));

    expect(screen.getAllByTestId("credential-dependents").map((e) => e.textContent)).toEqual(["3 repositories still use it", "1 repository still uses it"]);
    expect(screen.getByRole("button", { name: "Reconnect" })).toBeTruthy();
    expect(screen.getByRole("button", { name: "Replace token" })).toBeTruthy();
    expect(screen.queryByRole("button", { name: "Disconnect" })).toBeNull();
    expect(screen.queryByText("connected")).toBeNull();   // a disconnected credential is listed, but it is not a connection
  });

  it("puts the credentials that need a person first and says how many repositories each holds", async () => {
    renderAs(admin, [credential("pat-1", "Pat", { status: "Active", boundRepositoryCount: 2, createdDate: "2026-03-01T00:00:00Z" }), credential("oauth-1", "OAuth", { status: "Error", boundRepositoryCount: 5, createdDate: "2026-01-01T00:00:00Z" })]);

    await waitFor(() => expect(screen.getByText("needs reconnect")).toBeTruthy());

    const rows = screen.getAllByTestId("credential-dependents").map((e) => e.textContent);
    expect(rows).toEqual(["5 repositories still use it", "2 repositories bound"]);   // the refused one leads although it is older
  });

  it("marks a token whose expiry has passed as needing a new one although nothing has marked the row yet", async () => {
    // The row is still Active — the sweep has not looked — but the backend derives `rejected` from the recorded expiry.
    renderAs(member, [credential("pat-1", "Pat", { status: "Active", health: "rejected", expiresDate: "2026-01-02T00:00:00Z", daysToExpiry: 0 })]);

    await waitFor(() => expect(screen.getByText("needs a new token")).toBeTruthy());

    expect(screen.getByRole("button", { name: "Replace token" })).toBeTruthy();
  });

  it.each([
    [3, "expires in 3 days"],
    [1, "expires in 1 day"],
    [0, "expires today"],
  ])("says how long a token that expires in %i days has left, and still lets it be replaced in place", async (days, label) => {
    renderAs(member, [credential("pat-1", "Pat", { health: "expiring", daysToExpiry: days })]);

    await waitFor(() => expect(screen.getByText(label)).toBeTruthy());

    expect(screen.queryByText("needs a new token")).toBeNull();
    expect(screen.getByRole("button", { name: "Replace token" })).toBeTruthy();
  });

  it("marks a credential that is fine as active, with no warning", async () => {
    renderAs(member, [credential("oauth-1", "OAuth", { health: "active" }), credential("pat-1", "Pat", { health: "active", daysToExpiry: null })]);

    await waitFor(() => expect(screen.getAllByRole("button", { name: "Disconnect" })).toHaveLength(2));

    expect(screen.getAllByText("active")).toHaveLength(2);
    expect(screen.queryByText(/needs reconnect|needs a new token|expires|disconnected|owner left/)).toBeNull();
  });

  it("says a credential lent by someone who left the team is about to be retired", async () => {
    renderAs(admin, [credential("pat-1", "Pat", { health: "owner_left" })]);

    await waitFor(() => expect(screen.getByText("owner left")).toBeTruthy());
  });

  it("marks a team token the provider refused, beside the Replace token that brings it back", async () => {
    const teamToken = credential("team-1", "GroupAccessToken", { ownerUserId: null, ownerUserName: null, ownership: "TeamService", displayName: "Acme group token", status: "Error", health: "rejected", lastError: "token rejected by GitLab" });

    renderAs(admin, [teamToken]);
    await waitFor(() => expect(screen.getByRole("tab", { name: "Team" })).toBeTruthy());
    fireEvent.click(screen.getByRole("tab", { name: "Team" }));

    await waitFor(() => expect(screen.getByText("needs a new token")).toBeTruthy());
    expect(screen.getByText("needs a new token").closest("span")?.getAttribute("title")).toBe("token rejected by GitLab");
    expect(screen.getByRole("button", { name: "Replace token" })).toBeTruthy();
  });

  it("gives an admin Replace token on a team token and a member none", async () => {
    const teamToken = credential("team-1", "GroupAccessToken", { ownerUserId: null, ownerUserName: null, ownership: "TeamService", displayName: "Acme group token" });

    renderAs(admin, [teamToken]);
    await waitFor(() => expect(screen.getByRole("tab", { name: "Team" })).toBeTruthy());
    fireEvent.click(screen.getByRole("tab", { name: "Team" }));
    await waitFor(() => expect(screen.getByRole("button", { name: "Replace token" })).toBeTruthy());
  });

  it("offers a member no Replace token on a team token", async () => {
    const teamToken = credential("team-1", "GroupAccessToken", { ownerUserId: null, ownerUserName: null, ownership: "TeamService", displayName: "Acme group token" });

    renderAs(member, [teamToken]);
    await waitFor(() => expect(screen.getByRole("tab", { name: "Team" })).toBeTruthy());
    fireEvent.click(screen.getByRole("tab", { name: "Team" }));
    await waitFor(() => expect(screen.getByText("Acme group token")).toBeTruthy());

    expect(screen.queryByRole("button", { name: "Replace token" })).toBeNull();
  });
});

/**
 * A Providers dialog that says what it did and does not pile up history. Every Disconnect leaves a dead row, so a person who reconnected a few times saw four
 * rows for one account and a Reconnect that, on the working one, re-authorized in place and changed nothing they could see. Now: dead rows with nothing leaning
 * on them fold into one quiet line (and can be removed), a dead row that still has repositories says so and leads with moving them to the person's working
 * connection, every row says when the provider last confirmed it, a working sign-in is re-authorized from its menu, and each Reconnect / Replace token that lands
 * is confirmed in words.
 */
describe("connect remote modal — dead rows folded, repositories moved, actions confirmed", () => {
  const DAY = 86_400_000;
  const ago = (ms: number) => new Date(Date.now() - ms).toISOString();

  const admin: MeTeam = {
    id: "t1", slug: "acme", name: "Acme", kind: "Workspace", role: "Admin", permissions: ["repos.manage", "credentials.manage", "members.manage"],
    memberCount: 3, repositoryCount: 0, projectCount: 1, workflowCount: 0,
  };
  const member: MeTeam = { ...admin, role: "Member", permissions: ["workflows.write"] };

  const instance: ProviderInstanceSummary = { id: "pi1", teamId: "t1", provider: "GitLab", displayName: "GitLab_TED", baseUrl: "https://gitlab.sjfood.us", createdDate: "2026-01-01T00:00:00Z", oauthEnabled: true };

  type Row = Record<string, unknown> & { id: string };
  const credential = (id: string, authType: string, extra: Record<string, unknown> = {}): Row => ({ id, teamId: "t1", providerInstanceId: "pi1", ownerUserId: "u1", ownerUserName: "Maya", ownership: "Personal", authType, displayName: `Maya's ${id}`, status: "Active", createdDate: ago(30 * DAY), ...extra });
  const dead = (id: string, extra: Record<string, unknown> = {}) => credential(id, "OAuth", { status: "Revoked", dependentCount: 0, boundRepositoryCount: 0, ...extra });
  const teamToken = (id: string, extra: Record<string, unknown> = {}) => credential(id, "GroupAccessToken", { ownerUserId: null, ownerUserName: null, ownership: "TeamService", displayName: "Acme group token", ...extra });

  type Call = { method: string; url: string; path: string; body: unknown };
  type Reply = { status?: number; body?: unknown };
  type Respond = (call: Call, state: { credentials: Row[] }) => Reply | undefined;

  /** The dialog over a fake API whose credential list is `state.credentials`, which a responder may change as the server would. `respond` answers first. */
  function renderDialog(team: MeTeam, held: Row[], respond: Respond = () => undefined, providerOver: Partial<ProviderInstanceSummary> = {}) {
    localStorage.setItem("codespace.jwt", "test-jwt");
    localStorage.setItem("codespace.activeTeamId", team.id);

    const state = { credentials: held };
    const calls: Call[] = [];

    const defaults = (call: Call): Reply => {
      if (call.path === "/api/users/me") return { body: { id: "u1", email: "u@test.local", name: "Maya", teams: [team], permissions: [], passwordMustChange: false } };
      if (call.path === "/api/provider-instances") return { body: [{ ...instance, ...providerOver }] };
      if (call.path === "/api/credentials") return { body: state.credentials };
      if (call.path.endsWith("/capabilities")) return { body: { credentialId: "any", grantedScopes: [], capabilities: [] } };

      return {};
    };

    vi.stubGlobal("fetch", vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = typeof input === "string" ? input : input.toString();
      const call: Call = { method: init?.method ?? "GET", url, path: new URL(url, "http://localhost").pathname, body: init?.body ? JSON.parse(String(init.body)) : undefined };

      calls.push(call);

      const reply = respond(call, state) ?? defaults(call);

      return new Response(reply.body === undefined ? "" : JSON.stringify(reply.body), { status: reply.status ?? (reply.body === undefined ? 404 : 200), headers: { "Content-Type": "application/json" } });
    }));

    const client = new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: 0 } } });

    render(
      <QueryClientProvider client={client}>
        <DialogProvider><ConnectRemoteModal onClose={() => {}} /></DialogProvider>
      </QueryClientProvider>,
    );

    return { calls, state, client };
  }

  afterEach(() => { localStorage.clear(); vi.unstubAllGlobals(); vi.restoreAllMocks(); });

  /** Pop the provider's window and have it answer, as /oauth-callback does through window.opener. */
  function answerPopup(message: Record<string, unknown>) {
    vi.spyOn(window, "open").mockReturnValue({ closed: false, close: vi.fn() } as unknown as Window);

    return () => window.dispatchEvent(new MessageEvent("message", { origin: window.location.origin, data: { type: "codespace:oauth-result", ...message } }));
  }

  const init: Respond = (call) => call.path === "/api/credentials/oauth/init" ? { body: { authorizeUrl: "https://gitlab.sjfood.us/oauth/authorize", state: "s" } } : undefined;

  const earlier = (count: number) => new RegExp(`${count} earlier connections? — disconnected`);

  describe("folding the history", () => {
    it("folds the disconnected credentials nothing leans on into one quiet line, and lists the one that works", async () => {
      renderDialog(admin, [credential("oauth-live", "OAuth", { lastValidatedDate: ago(60_000) }), dead("old-1", { createdDate: ago(2 * DAY) }), dead("old-2", { createdDate: ago(10 * DAY) }), dead("old-3", { createdDate: ago(21 * DAY) })]);

      const line = await screen.findByRole("button", { name: earlier(3) });

      expect(screen.getAllByRole("button", { name: "Disconnect" })).toHaveLength(1);   // the working one is the only row
      expect(screen.queryByText("disconnected")).toBeNull();                          // no dead row wears its chip
      expect(screen.queryByRole("button", { name: "Remove" })).toBeNull();            // and nothing is removable until the line is opened
      expect(line.getAttribute("aria-expanded")).toBe("false");

      fireEvent.click(line);

      expect(line.getAttribute("aria-expanded")).toBe("true");
      expect(screen.getAllByRole("button", { name: "Remove" })).toHaveLength(3);
      expect(screen.getByText("connected 21 d ago")).toBeTruthy();                    // each says when it was connected, so which is which
    });

    it("says 'earlier connection' for one", async () => {
      renderDialog(admin, [dead("old-1")]);

      expect(await screen.findByRole("button", { name: earlier(1) })).toBeTruthy();
    });

    it("keeps a disconnected credential something still leans on as a row, and folds only the rest", async () => {
      renderDialog(admin, [dead("busy", { dependentCount: 2, boundRepositoryCount: 2 }), dead("quiet")]);

      await screen.findByRole("button", { name: earlier(1) });

      expect(screen.getAllByText("disconnected")).toHaveLength(1);                    // the busy one, still a row, with its chip
      expect(screen.getByTestId("credential-dependents").textContent).toBe("2 repositories still use it");
    });

    it("keeps a disconnected credential only a group hook leans on a row, and says what holds it: the count covers more than repositories", async () => {
      renderDialog(admin, [dead("hooked", { dependentCount: 1, boundRepositoryCount: 0, boundConnectionWebhookCount: 1 })]);

      await waitFor(() => expect(screen.getByText("disconnected")).toBeTruthy());

      expect(screen.getByTestId("credential-dependents").textContent).toBe("1 group hook still uses it");
      expect(screen.queryByRole("button", { name: /earlier connection/ })).toBeNull();
      expect(screen.queryByRole("button", { name: "Remove" })).toBeNull();
    });

    it("names an act-as identity that holds a disconnected credential too", async () => {
      renderDialog(admin, [dead("identified", { dependentCount: 1, boundRepositoryCount: 0, actAsIdentityCount: 1 })]);

      await waitFor(() => expect(screen.getByText("disconnected")).toBeTruthy());

      expect(screen.getByTestId("credential-dependents").textContent).toBe("1 act-as identity still uses it");
      expect(screen.queryByRole("button", { name: /earlier connection/ })).toBeNull();
    });

    it("lists every credential as it always did when the server sends no count — and offers no removal it could not do", async () => {
      renderDialog(admin, [credential("oauth-live", "OAuth"), dead("old-1", { dependentCount: undefined }), dead("old-2", { dependentCount: undefined })]);

      await waitFor(() => expect(screen.getAllByText("disconnected")).toHaveLength(2));

      expect(screen.queryByRole("button", { name: /earlier connection/ })).toBeNull();
      expect(screen.queryByRole("button", { name: "Remove" })).toBeNull();
    });

    it("never folds a credential the provider refused or one that works", async () => {
      renderDialog(admin, [credential("oauth-refused", "OAuth", { status: "Error", dependentCount: 0 }), credential("pat-ok", "Pat", { dependentCount: 0 })]);

      await waitFor(() => expect(screen.getByText("needs reconnect")).toBeTruthy());

      expect(screen.queryByRole("button", { name: /earlier connection/ })).toBeNull();
    });
  });

  describe("removing a row", () => {
    const removalOf = (path: string, state: { credentials: Row[] }) => {
      const id = path.split("/").pop()!;

      state.credentials = state.credentials.filter((c) => c.id !== id);

      return { body: { credentialId: id } };
    };

    it("removes the row through the API and the line counts one fewer", async () => {
      const { calls } = renderDialog(admin, [dead("old-1"), dead("old-2"), dead("old-3")], (call, state) => call.method === "DELETE" ? removalOf(call.path, state) : undefined);

      fireEvent.click(await screen.findByRole("button", { name: earlier(3) }));
      fireEvent.click(screen.getAllByRole("button", { name: "Remove" })[0]);

      await waitFor(() => expect(screen.getByRole("button", { name: earlier(2) })).toBeTruthy());

      const deletes = calls.filter((c) => c.method === "DELETE");

      expect(deletes).toHaveLength(1);
      expect(deletes[0].path).toMatch(/^\/api\/credentials\/old-[123]$/);
      expect(screen.getAllByRole("button", { name: "Remove" })).toHaveLength(2);       // the line stays open as rows leave it
    });

    it("shows the server's refusal beside the row and keeps the row", async () => {
      const refusal = "Credential 'Maya's old-1' still has 1 repository leaning on it. Move or unlink them first.";

      renderDialog(admin, [dead("old-1")], (call) => call.method === "DELETE" ? { status: 400, body: { code: "invalid_request", message: refusal } } : undefined);

      fireEvent.click(await screen.findByRole("button", { name: earlier(1) }));
      fireEvent.click(screen.getByRole("button", { name: "Remove" }));

      expect((await screen.findByRole("alert")).textContent).toContain(refusal);
      expect(screen.getByRole("button", { name: earlier(1) })).toBeTruthy();
    });

    it("reads the list again when the server refuses, so a row that changed under the person stops reading as history", async () => {
      const refusal = "Credential 'Maya's old-1' was reconnected or changed while it was being removed, so nothing was removed. Look at it again.";
      const listReads = (calls: Call[]) => calls.filter((c) => c.method === "GET" && c.path === "/api/credentials").length;

      const { calls } = renderDialog(admin, [dead("old-1")], (call, state) => {
        if (call.method !== "DELETE") return undefined;

        state.credentials = state.credentials.map((c) => ({ ...c, status: "Active", lastValidatedDate: new Date().toISOString() }));   // the reconnect that won the race

        return { status: 400, body: { code: "invalid_request", message: refusal } };
      });

      fireEvent.click(await screen.findByRole("button", { name: earlier(1) }));
      expect(listReads(calls)).toBe(1);
      fireEvent.click(screen.getByRole("button", { name: "Remove" }));

      expect((await screen.findByRole("alert")).textContent).toContain(refusal);                  // the refusal is still said, on the row it was for
      await waitFor(() => expect(screen.getByRole("button", { name: "Disconnect" })).toBeTruthy());   // which is now a working row, not a line in the fold
      expect(screen.queryByRole("button", { name: /earlier connection/ })).toBeNull();
      expect(listReads(calls)).toBeGreaterThan(1);
    });

    it("lets a member remove their own history but gives them no Remove on a team token", async () => {
      renderDialog(member, [dead("old-1"), teamToken("team-old", { status: "Revoked", dependentCount: 0, boundRepositoryCount: 0 })]);

      fireEvent.click(await screen.findByRole("button", { name: earlier(1) }));
      expect(screen.getAllByRole("button", { name: "Remove" })).toHaveLength(1);

      fireEvent.click(screen.getByRole("tab", { name: "Team" }));
      fireEvent.click(await screen.findByRole("button", { name: earlier(1) }));

      expect(screen.queryByRole("button", { name: "Remove" })).toBeNull();
    });

    it("gives an admin Remove on a disconnected team token nothing leans on, which is not also listed as a token", async () => {
      const held = [teamToken("team-live", { displayName: "Acme live token" }), teamToken("team-old", { displayName: "Acme old token", status: "Revoked", dependentCount: 0, boundRepositoryCount: 0 })];
      const { calls } = renderDialog(admin, held, (call, state) => call.method === "DELETE" ? removalOf(call.path, state) : undefined);

      fireEvent.click(await screen.findByRole("tab", { name: "Team" }));
      const line = await screen.findByRole("button", { name: earlier(1) });

      expect(screen.getByText("Acme live token")).toBeTruthy();
      expect(screen.queryByText("Acme old token")).toBeNull();                       // folded, not a row beside the live token
      expect(document.querySelectorAll(".cn-tg-cred")).toHaveLength(1);

      fireEvent.click(line);
      expect(screen.queryByText("Acme old token")).toBeNull();                       // and still not one once the line is open
      expect(document.querySelectorAll(".cn-tg-cred")).toHaveLength(1);
      fireEvent.click(screen.getByRole("button", { name: "Remove" }));

      await waitFor(() => expect(calls.some((c) => c.method === "DELETE" && c.path === "/api/credentials/team-old")).toBe(true));
    });
  });

  describe("moving what a dead credential still holds", () => {
    const mine = (over: Record<string, unknown> = {}) => ({ credentialId: "oauth-live", displayName: "Maya's GitLab", authType: "OAuth", ownership: "Personal", kind: "YourConnection", createdDate: ago(DAY), connectedBeforeDisconnect: true, ...over });
    const suggestingTo = (...suggestions: unknown[]): Respond => (call) => call.path.endsWith("/relink-suggestions") ? { body: suggestions } : undefined;

    const holder = (extra: Record<string, unknown> = {}) => dead("old-1", { dependentCount: 3, boundRepositoryCount: 3, ...extra });
    const live = () => credential("oauth-live", "OAuth", { displayName: "Maya's GitLab", lastValidatedDate: ago(60_000) });

    /**
     * The suggestions for a credential have been read and applied to its row. A negative assertion ("no Move") means something only after this: Reconnect, which
     * the row shows from the credential list alone, appears before the suggestions are back.
     */
    async function suggestionsRead(client: QueryClient, credentialId: string) {
      await waitFor(() => expect(client.getQueryState(["relink-suggestions", credentialId])?.status).toBe("success"));
      await act(async () => { await new Promise((resolve) => setTimeout(resolve, 0)); });   // the row renders what the cache now holds
    }

    const rowOf = (connected: string) => screen.getByText(connected).closest(".cn-cred") as HTMLElement;

    it("leads with moving the repositories to the person's own working connection, and keeps Reconnect as the other way", async () => {
      renderDialog(admin, [holder(), live()], suggestingTo(mine()));

      const move = await screen.findByRole("button", { name: "Move them to Maya's GitLab" });
      const reconnect = screen.getByRole("button", { name: "Reconnect" });

      expect(screen.getByTestId("credential-dependents").textContent).toBe("3 repositories still use it");
      expect(move.className).toContain("btn-primary");
      expect(reconnect.className).not.toContain("btn-primary");
      expect(move.compareDocumentPosition(reconnect) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();   // primary first
    });

    it("moves them in one request, says what moved, and the emptied row folds away", async () => {
      const { calls } = renderDialog(admin, [holder(), live()], (call, state) => {
        if (call.path.endsWith("/relink-suggestions")) return { body: [mine()] };
        if (call.method !== "POST" || !call.path.endsWith("/relink-bindings")) return undefined;

        state.credentials = state.credentials.map((c) => c.id === "old-1" ? { ...c, dependentCount: 0, boundRepositoryCount: 0 } : c);

        return { body: { fromCredentialId: "old-1", toCredentialId: "oauth-live", movedRepositoryCount: 3, movedConnectionWebhookCount: 0 } };
      });

      fireEvent.click(await screen.findByRole("button", { name: "Move them to Maya's GitLab" }));

      expect((await screen.findByText(/Moved 3 repositories to Maya's GitLab/)).closest("[role=status]")).toBeTruthy();   // said where it stays: the row it came from has folded
      await waitFor(() => expect(screen.getByRole("button", { name: earlier(1) })).toBeTruthy());

      const post = calls.find((c) => c.method === "POST" && c.path.endsWith("/relink-bindings"))!;

      expect(post.path).toBe("/api/credentials/old-1/relink-bindings");
      expect(post.body).toEqual({ toCredentialId: "oauth-live" });
    });

    it("also counts the group hooks it moved", async () => {
      renderDialog(admin, [holder(), live()], (call) => {
        if (call.path.endsWith("/relink-suggestions")) return { body: [mine()] };
        if (call.method === "POST" && call.path.endsWith("/relink-bindings")) return { body: { fromCredentialId: "old-1", toCredentialId: "oauth-live", movedRepositoryCount: 1, movedConnectionWebhookCount: 2 } };

        return undefined;
      });

      fireEvent.click(await screen.findByRole("button", { name: "Move them to Maya's GitLab" }));

      expect(await screen.findByText(/Moved 1 repository and 2 group hooks to Maya's GitLab/)).toBeTruthy();
    });

    it("shows the server's refusal and leaves the row as it was", async () => {
      const refusal = "Credential 'Maya's GitLab' cannot read acme/app. Nothing was moved.";

      renderDialog(admin, [holder(), live()], (call) => {
        if (call.path.endsWith("/relink-suggestions")) return { body: [mine()] };
        if (call.method === "POST" && call.path.endsWith("/relink-bindings")) return { status: 400, body: { code: "invalid_request", message: refusal } };

        return undefined;
      });

      fireEvent.click(await screen.findByRole("button", { name: "Move them to Maya's GitLab" }));

      expect((await screen.findByRole("alert")).textContent).toContain(refusal);
      expect(screen.getByRole("button", { name: "Move them to Maya's GitLab" })).toBeTruthy();
      expect(screen.getByTestId("credential-dependents").textContent).toBe("3 repositories still use it");
    });

    it("offers it for a credential the provider refused, which relink-bindings also takes bindings from", async () => {
      renderDialog(admin, [credential("oauth-refused", "OAuth", { status: "Error", dependentCount: 2, boundRepositoryCount: 2 }), live()], suggestingTo(mine()));

      expect(await screen.findByRole("button", { name: "Move them to Maya's GitLab" })).toBeTruthy();
      expect(screen.getByRole("button", { name: "Reconnect" })).toBeTruthy();
      expect(screen.getByTestId("credential-dependents").textContent).toBe("2 repositories still use it");
    });

    it.each([
      ["has no other working connection", [] as unknown[]],
      ["has only a team token to offer", [mine({ credentialId: "team-1", displayName: "Acme group token", kind: "TeamServiceToken", ownership: "TeamService" })]],
    ])("offers no move when the person %s, and Reconnect stays the way back", async (_why, suggestions) => {
      const { client } = renderDialog(admin, [holder()], suggestingTo(...suggestions));

      await suggestionsRead(client, "old-1");
      await waitFor(() => expect(screen.getByRole("button", { name: "Reconnect" })).toBeTruthy());

      expect(screen.queryByRole("button", { name: /Move them to/ })).toBeNull();
    });

    it("reads each dead credential's own suggestions: two rows with different candidates offer different moves", async () => {
      const own = (id: string) => (call: Call) => call.path === `/api/credentials/${id}/relink-suggestions`;
      const first = holder({ createdDate: ago(5 * DAY) });
      const second = dead("old-2", { dependentCount: 1, boundRepositoryCount: 1, createdDate: ago(9 * DAY) });

      const { calls, client } = renderDialog(admin, [first, second, live()], (call) => {
        if (own("old-1")(call)) return { body: [mine({ credentialId: "oauth-live", displayName: "Maya's GitLab" })] };
        if (own("old-2")(call)) return { body: [] };   // nothing to move this one to

        return undefined;
      });

      await suggestionsRead(client, "old-1");
      await suggestionsRead(client, "old-2");

      expect(within(rowOf("connected 5 d ago")).getByRole("button", { name: "Move them to Maya's GitLab" })).toBeTruthy();
      expect(within(rowOf("connected 9 d ago")).queryByRole("button", { name: /Move them to/ })).toBeNull();
      expect(calls.filter((c) => c.path.endsWith("/relink-suggestions")).map((c) => c.path).sort()).toEqual(["/api/credentials/old-1/relink-suggestions", "/api/credentials/old-2/relink-suggestions"]);
    });

    it("reads the suggestions of the rows that remain again after a move, which changes what they can be offered", async () => {
      const first = holder({ createdDate: ago(5 * DAY) });
      const second = dead("old-2", { dependentCount: 1, boundRepositoryCount: 1, createdDate: ago(9 * DAY) });
      const readsOf = (calls: Call[], id: string) => calls.filter((c) => c.method === "GET" && c.path === `/api/credentials/${id}/relink-suggestions`).length;

      const { calls, client } = renderDialog(admin, [first, second, live()], (call) => {
        if (call.path.endsWith("/relink-suggestions")) return { body: [mine()] };
        if (call.method === "POST" && call.path.endsWith("/relink-bindings")) return { body: { fromCredentialId: "old-1", toCredentialId: "oauth-live", movedRepositoryCount: 3, movedConnectionWebhookCount: 0 } };

        return undefined;
      });

      await suggestionsRead(client, "old-2");
      expect(readsOf(calls, "old-2")).toBe(1);

      fireEvent.click(within(rowOf("connected 5 d ago")).getByRole("button", { name: "Move them to Maya's GitLab" }));

      await waitFor(() => expect(readsOf(calls, "old-2")).toBe(2));
    });

    it("offers to move the group hooks of a dead credential that holds no repository, and says what moved", async () => {
      const hooked = dead("hooked", { dependentCount: 2, boundRepositoryCount: 0, boundConnectionWebhookCount: 2 });

      const { calls } = renderDialog(admin, [hooked, live()], (call) => {
        if (call.path.endsWith("/relink-suggestions")) return { body: [mine()] };
        if (call.method === "POST" && call.path.endsWith("/relink-bindings")) return { body: { fromCredentialId: "hooked", toCredentialId: "oauth-live", movedRepositoryCount: 0, movedConnectionWebhookCount: 2 } };

        return undefined;
      });

      expect((await screen.findByTestId("credential-dependents")).textContent).toBe("2 group hooks still use it");
      fireEvent.click(await screen.findByRole("button", { name: "Move them to Maya's GitLab" }));

      expect(await screen.findByText(/Moved 2 group hooks to Maya's GitLab/)).toBeTruthy();
      expect(calls.find((c) => c.method === "POST" && c.path.endsWith("/relink-bindings"))!.path).toBe("/api/credentials/hooked/relink-bindings");
    });

    it("offers no move for a dead credential held only by an act-as identity, which a move leaves where it is", async () => {
      const { calls } = renderDialog(admin, [dead("identified", { dependentCount: 1, boundRepositoryCount: 0, actAsIdentityCount: 1 }), live()], suggestingTo(mine()));

      await waitFor(() => expect(screen.getByRole("button", { name: "Reconnect" })).toBeTruthy());

      expect(screen.queryByRole("button", { name: /Move them to/ })).toBeNull();
      expect(calls.some((c) => c.path.endsWith("/relink-suggestions"))).toBe(false);
    });

    it("does not ask for suggestions, nor offer a move, to someone who may not relink: it needs repos.manage and credentials.manage", async () => {
      const { calls } = renderDialog(member, [holder(), live()], suggestingTo(mine()));

      await waitFor(() => expect(screen.getByRole("button", { name: "Reconnect" })).toBeTruthy());

      expect(screen.queryByRole("button", { name: /Move them to/ })).toBeNull();
      expect(calls.some((c) => c.path.endsWith("/relink-suggestions"))).toBe(false);
    });

    it("does not offer a move off a credential still in service, however it reads — the server accepts only a dead source", async () => {
      const { calls } = renderDialog(admin, [credential("pat-lapsed", "Pat", { status: "Active", health: "rejected", boundRepositoryCount: 2, dependentCount: 2 }), live()], suggestingTo(mine()));

      await waitFor(() => expect(screen.getByRole("button", { name: "Replace token" })).toBeTruthy());

      expect(screen.queryByRole("button", { name: /Move them to/ })).toBeNull();
      expect(calls.some((c) => c.path.endsWith("/relink-suggestions"))).toBe(false);
    });
  });

  describe("authorized time", () => {
    it("says when the provider last confirmed the authorization, and keeps when it was connected in the tooltip", async () => {
      renderDialog(admin, [credential("oauth-1", "OAuth", { createdDate: ago(21 * DAY), lastValidatedDate: ago(5 * 60_000) })]);

      const since = await screen.findByText("authorized 5 min ago");

      expect(since.getAttribute("title")).toMatch(/^Connected /);
      expect(screen.queryByText(/^connected \d+ d ago$/)).toBeNull();
    });

    it("says when it was connected for a row nothing has confirmed, and for a server that sends no confirmation time", async () => {
      renderDialog(admin, [credential("pat-1", "Pat", { createdDate: ago(3 * DAY) })]);

      expect(await screen.findByText("connected 3 d ago")).toBeTruthy();
      expect(screen.queryByText(/^authorized/)).toBeNull();
    });
  });

  describe("re-authorizing in place", () => {
    it("keeps Reconnect as a visible action for a credential the provider refused, and has no menu for it", async () => {
      renderDialog(admin, [credential("oauth-refused", "OAuth", { status: "Error" })]);

      await waitFor(() => expect(screen.getByRole("button", { name: "Reconnect" })).toBeTruthy());

      expect(screen.queryByRole("button", { name: /More actions for/ })).toBeNull();
    });

    it("keeps Reconnect visible on a disconnected credential that still holds repositories", async () => {
      renderDialog(admin, [dead("old-1", { dependentCount: 2, boundRepositoryCount: 2 })]);

      await waitFor(() => expect(screen.getByRole("button", { name: "Reconnect" })).toBeTruthy());
    });

    it("keeps Reconnect on a folded sign-in for a member, whose only way back it is: a plain Connect with OAuth takes credentials.manage", async () => {
      vi.spyOn(window, "open").mockReturnValue(null);
      const { calls } = renderDialog(member, [dead("old-1")], init);

      fireEvent.click(await screen.findByRole("button", { name: earlier(1) }));

      expect(screen.queryByRole("button", { name: /Connect with OAuth/ })).toBeNull();
      fireEvent.click(await screen.findByRole("button", { name: "Reconnect" }));

      await waitFor(() => expect(calls.some((c) => c.path === "/api/credentials/oauth/init")).toBe(true));
      expect(calls.find((c) => c.path === "/api/credentials/oauth/init")!.body).toMatchObject({ providerInstanceId: "pi1", replaceCredentialId: "old-1" });
    });

    it("offers Reconnect on a folded sign-in to an admin as well, beside the plain connect that would mint another row", async () => {
      renderDialog(admin, [dead("old-1")]);

      fireEvent.click(await screen.findByRole("button", { name: earlier(1) }));

      expect(await screen.findByRole("button", { name: "Reconnect" })).toBeTruthy();
      expect(screen.getByRole("button", { name: /Connect with OAuth/ })).toBeTruthy();
    });

    it("offers no Reconnect on a folded token", async () => {
      const oldToken = credential("pat-old", "Pat", { status: "Revoked", dependentCount: 0, boundRepositoryCount: 0 });

      renderDialog(admin, [oldToken]);
      fireEvent.click(await screen.findByRole("button", { name: earlier(1) }));
      expect(screen.getByRole("button", { name: "Remove" })).toBeTruthy();
      expect(screen.queryByRole("button", { name: "Reconnect" })).toBeNull();
    });

    it("offers no Reconnect on a folded sign-in of a provider with no OAuth app", async () => {
      renderDialog(admin, [dead("old-1")], () => undefined, { oauthEnabled: false });

      fireEvent.click(await screen.findByRole("button", { name: earlier(1) }));

      expect(screen.getByRole("button", { name: "Remove" })).toBeTruthy();
      expect(screen.queryByRole("button", { name: "Reconnect" })).toBeNull();
    });

    it("confirms a Reconnect from the fold: the row comes back as a working connection and leaves the line", async () => {
      const { calls, state } = renderDialog(admin, [dead("old-1"), dead("old-2", { createdDate: ago(40 * DAY) })], init);
      const answer = answerPopup({ status: "success", credentialId: "old-1" });

      fireEvent.click(await screen.findByRole("button", { name: earlier(2) }));
      fireEvent.click((await screen.findAllByRole("button", { name: "Reconnect" }))[0]);
      await waitFor(() => expect(calls.some((c) => c.path === "/api/credentials/oauth/init")).toBe(true));

      state.credentials = state.credentials.map((c) => c.id === "old-1" ? { ...c, status: "Active", lastValidatedDate: new Date().toISOString() } : c);
      answer();

      expect((await screen.findByRole("status")).textContent).toContain("Reconnected — GitLab authorized Maya's old-1 just now");
      await waitFor(() => expect(screen.getByRole("button", { name: earlier(1) })).toBeTruthy());
      expect(screen.getAllByRole("button", { name: "Disconnect" })).toHaveLength(1);
    });

    it("shows a refused Reconnect's own sentence on the folded row it was for", async () => {
      const sentence = "That sign-in is a different GitLab account than the one this credential was connected as.";
      const { calls } = renderDialog(member, [dead("old-1")], init);
      const answer = answerPopup({ status: "error", error: "credential_account_mismatch", description: sentence });

      fireEvent.click(await screen.findByRole("button", { name: earlier(1) }));
      fireEvent.click(await screen.findByRole("button", { name: "Reconnect" }));
      await waitFor(() => expect(calls.some((c) => c.path === "/api/credentials/oauth/init")).toBe(true));
      answer();

      expect((await screen.findByRole("alert")).textContent).toBe(sentence);
      expect(screen.getByRole("button", { name: earlier(1) }).getAttribute("aria-expanded")).toBe("true");   // still folded, still there to try again
    });

    it("puts a working sign-in's Re-authorize in its menu, beside a Disconnect that stays where it was", async () => {
      renderDialog(admin, [credential("oauth-live", "OAuth")]);

      await waitFor(() => expect(screen.getByRole("button", { name: "Disconnect" })).toBeTruthy());

      expect(screen.queryByRole("button", { name: "Reconnect" })).toBeNull();
      expect(screen.queryByRole("button", { name: "Re-authorize" })).toBeNull();   // not until the menu is opened

      fireEvent.click(screen.getByRole("button", { name: /More actions for/ }));

      expect(await screen.findByRole("button", { name: "Re-authorize" })).toBeTruthy();
    });
  });

  describe("saying what a reconnect or a replaced token did", () => {
    async function reauthorize(calls: Call[]) {
      fireEvent.click(await screen.findByRole("button", { name: /More actions for/ }));
      fireEvent.click(await screen.findByRole("button", { name: "Re-authorize" }));
      await waitFor(() => expect(calls.some((c) => c.path === "/api/credentials/oauth/init")).toBe(true));
    }

    it("confirms a Reconnect that landed, and the row's authorized time follows", async () => {
      const { calls, state } = renderDialog(admin, [credential("oauth-1", "OAuth", { lastValidatedDate: ago(2 * DAY) })], init);
      const answer = answerPopup({ status: "success", credentialId: "oauth-1" });

      await screen.findByText("authorized 2 d ago");
      await reauthorize(calls);

      state.credentials = state.credentials.map((c) => ({ ...c, lastValidatedDate: new Date().toISOString() }));
      answer();

      const confirmation = await screen.findByRole("status");

      expect(confirmation.textContent).toContain("Reconnected — GitLab authorized Maya's oauth-1 just now");
      expect(within(confirmation.closest(".cn-cred") as HTMLElement).getByText("authorized just now")).toBeTruthy();   // said on the credential it was for
    });

    it("shows a refused Reconnect's own sentence on the credential it was for", async () => {
      const sentence = "That sign-in is a different GitLab account than the one this credential was connected as.";
      const { calls } = renderDialog(admin, [credential("oauth-1", "OAuth"), credential("oauth-2", "OAuth", { createdDate: ago(40 * DAY) })], init);
      const answer = answerPopup({ status: "error", error: "credential_account_mismatch", description: sentence });

      fireEvent.click((await screen.findAllByRole("button", { name: /More actions for/ }))[0]);
      fireEvent.click(await screen.findByRole("button", { name: "Re-authorize" }));
      await waitFor(() => expect(calls.some((c) => c.path === "/api/credentials/oauth/init")).toBe(true));
      answer();

      const refusal = await screen.findByRole("alert");

      expect(refusal.textContent).toBe(sentence);
      expect(screen.queryByRole("status")).toBeNull();
    });

    it("confirms a replaced token the same way", async () => {
      const { state } = renderDialog(member, [credential("pat-1", "Pat", { lastValidatedDate: ago(2 * DAY) })], (call) => call.method === "PUT" ? { body: { credentialId: "pat-1", providerUsername: "maya" } } : undefined);

      await screen.findByText("authorized 2 d ago");
      fireEvent.click(screen.getByRole("button", { name: "Replace token" }));

      const dialog = await screen.findByRole("dialog", { name: "Replace token" });

      fireEvent.change(within(dialog).getByPlaceholderText(/glpat/i), { target: { value: "glpat-fresh" } });
      state.credentials = state.credentials.map((c) => ({ ...c, lastValidatedDate: new Date().toISOString() }));
      fireEvent.click(within(dialog).getByRole("button", { name: "Replace token" }));

      const confirmation = await screen.findByRole("status");

      expect(confirmation.textContent).toContain("Token replaced — GitLab authorized Maya's pat-1 just now");
      await waitFor(() => expect(screen.getByText("authorized just now")).toBeTruthy());
    });

    it("drops the confirmation when the credential is disconnected afterwards: the row no longer reads as freshly authorized", async () => {
      const held = credential("oauth-1", "OAuth", { lastValidatedDate: ago(2 * DAY), boundRepositoryCount: 2, dependentCount: 2 });

      const { calls, state } = renderDialog(admin, [held], (call, st) => {
        if (call.method === "POST" && call.path === "/api/credentials/oauth-1/revoke") {
          st.credentials = st.credentials.map((c) => ({ ...c, status: "Revoked" }));

          return { body: { credentialId: "oauth-1" } };
        }

        return init(call, st);
      });
      const answer = answerPopup({ status: "success", credentialId: "oauth-1" });

      await screen.findByText("authorized 2 d ago");
      await reauthorize(calls);
      state.credentials = state.credentials.map((c) => ({ ...c, lastValidatedDate: new Date().toISOString() }));
      answer();
      await screen.findByRole("status");

      fireEvent.click(screen.getByRole("button", { name: "Disconnect" }));
      fireEvent.click(within(await screen.findByRole("alertdialog")).getByRole("button", { name: "Disconnect" }));

      await waitFor(() => expect(screen.getByText("disconnected")).toBeTruthy());

      expect(screen.queryByRole("status")).toBeNull();
    });

    it("drops the confirmation of a replaced token when its replacement is opened again", async () => {
      const { state } = renderDialog(member, [credential("pat-1", "Pat", { lastValidatedDate: ago(2 * DAY) })], (call) => call.method === "PUT" ? { body: { credentialId: "pat-1", providerUsername: "maya" } } : undefined);

      const replace = async () => {
        fireEvent.click(screen.getByRole("button", { name: "Replace token" }));

        const dialog = await screen.findByRole("dialog", { name: "Replace token" });

        fireEvent.change(within(dialog).getByPlaceholderText(/glpat/i), { target: { value: "glpat-fresh" } });
        state.credentials = state.credentials.map((c) => ({ ...c, lastValidatedDate: new Date().toISOString() }));
        fireEvent.click(within(dialog).getByRole("button", { name: "Replace token" }));
      };

      await screen.findByText("authorized 2 d ago");
      await replace();
      await screen.findByRole("status");

      fireEvent.click(screen.getByRole("button", { name: "Replace token" }));

      await screen.findByRole("dialog", { name: "Replace token" });
      expect(screen.queryByRole("status")).toBeNull();
    });

    it("confirms a Reconnect of a dead row that still holds repositories, which comes back to life in place", async () => {
      const { calls, state } = renderDialog(admin, [dead("old-1", { dependentCount: 2, boundRepositoryCount: 2 })], init);
      const answer = answerPopup({ status: "success", credentialId: "old-1" });

      fireEvent.click(await screen.findByRole("button", { name: "Reconnect" }));
      await waitFor(() => expect(calls.some((c) => c.path === "/api/credentials/oauth/init")).toBe(true));

      state.credentials = state.credentials.map((c) => ({ ...c, status: "Active", lastValidatedDate: new Date().toISOString() }));
      answer();

      expect((await screen.findByRole("status")).textContent).toContain("Reconnected — GitLab authorized Maya's old-1 just now");
      await waitFor(() => expect(screen.getByText("2 repositories bound")).toBeTruthy());
    });
  });
});

/**
 * Editing a provider's base URL. The list shows the URL with any embedded credentials masked, so the form must not send
 * the masked value back as an edit; and a token or OAuth secret is only ever sent to the host it was entered for, so
 * moving the host asks for the new host's OAuth app and says why the server refuses the move while tokens are connected.
 */
describe("edit provider base URL", () => {
  const admin: MeTeam = {
    id: "t1", slug: "acme", name: "Acme", kind: "Workspace", role: "Admin", permissions: ["repos.manage", "credentials.manage", "members.manage"],
    memberCount: 3, repositoryCount: 0, projectCount: 1, workflowCount: 0,
  };

  function renderEditing(instance: ProviderInstanceSummary) {
    localStorage.setItem("codespace.jwt", "test-jwt");
    localStorage.setItem("codespace.activeTeamId", admin.id);

    const patches: unknown[] = [];
    const routes: Record<string, unknown> = {
      "/api/users/me": { id: "u1", email: "u@test.local", name: "Maya", teams: [admin], permissions: [], passwordMustChange: false },
      "/capabilities": { warnings: [] },
      "/api/provider-instances/defaults": { provider: instance.provider, defaultBaseUrl: "https://gitlab.com", defaultDisplayName: "GitLab", defaultOAuthScopes: ["api"], oAuthCallbackUrl: "https://codespace.test/cb" },
      "/api/provider-instances": [instance],
      "/api/credentials": [],
    };

    vi.stubGlobal("fetch", vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = typeof input === "string" ? input : input.toString();

      if (init?.method === "PATCH") {
        patches.push(JSON.parse(String(init.body)));
        return new Response(null, { status: 204 });
      }

      const key = Object.keys(routes).find((k) => url.includes(k));
      const body = key === undefined ? undefined : routes[key];
      return new Response(body === undefined ? "" : JSON.stringify(body), { status: body === undefined ? 404 : 200, headers: { "Content-Type": "application/json" } });
    }));

    const client = new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: 0 } } });

    render(
      <QueryClientProvider client={client}>
        <DialogProvider><ConnectRemoteModal onClose={() => {}} /></DialogProvider>
      </QueryClientProvider>,
    );

    return patches;
  }

  async function openEditSettings() {
    await waitFor(() => expect(screen.getByTitle("More actions")).toBeTruthy());
    fireEvent.click(screen.getByTitle("More actions"));
    fireEvent.click(await screen.findByRole("button", { name: /Edit settings/ }));
  }

  afterEach(() => { localStorage.clear(); vi.unstubAllGlobals(); });

  it("asks for the new host's OAuth app before a host move can be saved, and says connected tokens block it", async () => {
    renderEditing({ id: "pi1", teamId: "t1", provider: "GitLab", displayName: "Acme GitLab", baseUrl: "https://gitlab.acme.test", createdDate: "2026-01-01T00:00:00Z", oauthEnabled: true });
    await openEditSettings();

    fireEvent.change(screen.getByDisplayValue("https://gitlab.acme.test"), { target: { value: "https://attacker.test" } });

    expect(screen.getByText(/save is refused while any are connected/)).toBeTruthy();
    const save = screen.getByRole("button", { name: /Save/ }) as HTMLButtonElement;
    expect(save.disabled).toBe(true);

    fireEvent.change(screen.getAllByPlaceholderText("leave blank to keep the existing value")[0], { target: { value: "new-host-app" } });
    fireEvent.change(screen.getByPlaceholderText("required for the new host"), { target: { value: "new-host-secret" } });

    expect(save.disabled).toBe(false);
  });

  it("keeps a path change on the same host saveable without the secret", async () => {
    renderEditing({ id: "pi1", teamId: "t1", provider: "GitLab", displayName: "Acme GitLab", baseUrl: "https://gitlab.acme.test", createdDate: "2026-01-01T00:00:00Z", oauthEnabled: true });
    await openEditSettings();

    fireEvent.change(screen.getByDisplayValue("https://gitlab.acme.test"), { target: { value: "https://gitlab.acme.test/gitlab" } });

    expect(screen.queryByText(/save is refused while any are connected/)).toBeNull();
    expect((screen.getByRole("button", { name: /Save/ }) as HTMLButtonElement).disabled).toBe(false);
  });

  it("saves a base URL change on a token-only provider without an OAuth client pair", async () => {
    const patches = renderEditing({ id: "pi1", teamId: "t1", provider: "GitLab", displayName: "Acme GitLab", baseUrl: "https://gitlab.acme.test", createdDate: "2026-01-01T00:00:00Z", oauthEnabled: false });
    await openEditSettings();

    fireEvent.change(screen.getByDisplayValue("https://gitlab.acme.test"), { target: { value: "https://gitlab.moved.test" } });
    fireEvent.click(screen.getByRole("button", { name: /Save/ }));

    await waitFor(() => expect(patches.length).toBe(1));
    expect(patches[0]).toEqual({ baseUrl: "https://gitlab.moved.test" });
  });

  it("never sends the masked base URL back when only the name changes", async () => {
    const patches = renderEditing({ id: "pi1", teamId: "t1", provider: "GitLab", displayName: "Acme GitLab", baseUrl: "https://***:***@gitlab.acme.test", createdDate: "2026-01-01T00:00:00Z", oauthEnabled: true });
    await openEditSettings();

    expect(screen.getByText(/Leave the URL as it is to keep them/)).toBeTruthy();

    fireEvent.change(screen.getByDisplayValue("Acme GitLab"), { target: { value: "Acme GitLab (self-hosted)" } });
    fireEvent.click(screen.getByRole("button", { name: /Save/ }));

    await waitFor(() => expect(patches.length).toBe(1));
    expect(patches[0]).toEqual({ displayName: "Acme GitLab (self-hosted)" });
  });
});
