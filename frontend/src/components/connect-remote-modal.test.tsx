import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { fireEvent, render, screen, waitFor, within } from "@testing-library/react";
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

  it("starts the OAuth flow naming the credential, under the credential's own name", async () => {
    // A blocked popup ends the flow right after the init request, which is the request under test.
    vi.spyOn(window, "open").mockReturnValue(null);
    const calls = renderAs(admin, [credential("oauth-1", "OAuth")], { "/api/credentials/oauth/init": { authorizeUrl: "https://gitlab.test/oauth/authorize", state: "s" } });

    await waitFor(() => expect(screen.getByRole("button", { name: "Reconnect" })).toBeTruthy());
    fireEvent.click(screen.getByRole("button", { name: "Reconnect" }));

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
  ] as const)("offers a %s Reconnect on their own sign-in and Replace token on their own token: the credential's own person passes on membership", async (_role, asker) => {
    vi.spyOn(window, "open").mockReturnValue(null);
    const calls = renderAs(asker, [credential("oauth-1", "OAuth"), credential("pat-1", "Pat")], { "/api/credentials/oauth/init": { authorizeUrl: "https://gitlab.test/oauth/authorize", state: "s" } });

    await waitFor(() => expect(screen.getAllByRole("button", { name: "Disconnect" })).toHaveLength(2));

    expect(screen.getAllByRole("button", { name: "Replace token" })).toHaveLength(1);
    fireEvent.click(screen.getByRole("button", { name: "Reconnect" }));

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

    expect(screen.getAllByTestId("bound-repositories").map((e) => e.textContent)).toEqual(["3 repositories bound", "1 repository bound"]);
    expect(screen.getByRole("button", { name: "Reconnect" })).toBeTruthy();
    expect(screen.getByRole("button", { name: "Replace token" })).toBeTruthy();
    expect(screen.queryByRole("button", { name: "Disconnect" })).toBeNull();
    expect(screen.queryByText("connected")).toBeNull();   // a disconnected credential is listed, but it is not a connection
  });

  it("puts the credentials that need a person first and says how many repositories each holds", async () => {
    renderAs(admin, [credential("pat-1", "Pat", { status: "Active", boundRepositoryCount: 2, createdDate: "2026-03-01T00:00:00Z" }), credential("oauth-1", "OAuth", { status: "Error", boundRepositoryCount: 5, createdDate: "2026-01-01T00:00:00Z" })]);

    await waitFor(() => expect(screen.getByText("needs reconnect")).toBeTruthy());

    const rows = screen.getAllByTestId("bound-repositories").map((e) => e.textContent);
    expect(rows).toEqual(["5 repositories bound", "2 repositories bound"]);   // the refused one leads although it is older
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
