import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { fireEvent, render, screen, waitFor } from "@testing-library/react";
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
 * <p>The same for connecting: adding a team token and starting an OAuth connect take credentials.manage, and editing
 * or removing a provider takes repos.manage. Linking a personal access token takes only membership, so "Use a token"
 * stays for everyone.</p>
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
