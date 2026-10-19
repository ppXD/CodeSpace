import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render, screen, waitFor } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

import type { MeResponse, MeTeam, ProviderInstanceSummary, RepositorySummary } from "@/api/types";
import { DialogProvider } from "@/components/dialog";
import { RepositoryListPage } from "@/_imported/ai-code-space/content";

// Not co-located with the source: vite.config.ts excludes `**/_imported/**` from test discovery.

/**
 * Adding (a bind), unbinding and re-linking a repository take repos.manage on the server. A control that is shown and then answers 403
 * tells someone they may do a thing and then refuses them, so below that tier the controls are absent — and the gate
 * reads the server's own expansion of its matrix (`me.teams[].permissions`), never a role name.
 */
describe("repository list controls", () => {
  const team = (role: MeTeam["role"], permissions: string[]): MeTeam => ({
    id: "t1", slug: "acme", name: "Acme", kind: "Workspace", role, permissions,
    memberCount: 3, repositoryCount: 2, projectCount: 1, workflowCount: 0,
  });

  const me = (t: MeTeam): MeResponse => ({ id: "u1", email: "u@test.local", name: "Maya", teams: [t], permissions: [], passwordMustChange: false });

  const instance: ProviderInstanceSummary = { id: "pi1", teamId: "t1", provider: "GitLab", displayName: "GitLab", baseUrl: "https://gitlab.test", createdDate: "2026-01-01T00:00:00Z", oauthEnabled: true };

  const repo = (over: Partial<RepositorySummary>): RepositorySummary => ({
    id: "r1", teamId: "t1", providerInstanceId: "pi1", credentialId: "c1", fullPath: "acme/api", name: "api", defaultBranch: "main",
    visibility: "Private", status: "Active", webUrl: "https://gitlab.test/acme/api", createdDate: "2026-01-01T00:00:00Z", ...over,
  });

  const repositories = [
    repo({}),
    repo({ id: "r2", fullPath: "acme/web", name: "web", status: "Error", lastError: "credential revoked" }),
  ];

  // c1 is what both repositories run through (a working credential: the broken one is broken by its STATUS, not its credential); c2 is a spare.
  const credentials = [
    { id: "c1", teamId: "t1", providerInstanceId: "pi1", ownerUserId: "u2", ownerUserName: "Ada", ownership: "Personal", authType: "Pat", displayName: "Ada's token", status: "Active", createdDate: "2026-01-01T00:00:00Z" },
    { id: "c2", teamId: "t1", providerInstanceId: "pi1", ownerUserId: "u1", ownerUserName: "Maya", ownership: "Personal", authType: "Pat", displayName: "Maya's token", status: "Active", createdDate: "2026-01-01T00:00:00Z" },
  ];

  function renderAs(t: MeTeam, over: { repositories?: RepositorySummary[]; credentials?: unknown[] } = {}) {
    localStorage.setItem("codespace.jwt", "test-jwt");
    localStorage.setItem("codespace.activeTeamId", t.id);

    const routes: Record<string, unknown> = { "/api/users/me": me(t), "/api/repositories": over.repositories ?? repositories, "/api/provider-instances": [instance], "/api/credentials": over.credentials ?? credentials };

    vi.stubGlobal("fetch", vi.fn(async (input: RequestInfo | URL) => {
      const url = typeof input === "string" ? input : input.toString();
      const key = Object.keys(routes).find((k) => url.includes(k));
      const body = key === undefined ? undefined : routes[key];
      return new Response(body === undefined ? "" : JSON.stringify(body), { status: body === undefined ? 404 : 200, headers: { "Content-Type": "application/json" } });
    }));

    const client = new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: 0 } } });

    return render(
      <QueryClientProvider client={client}>
        <DialogProvider>
          <RepositoryListPage tab="all" query="" onTabChange={() => {}} onQueryChange={() => {}} onOpenRepo={() => {}} />
        </DialogProvider>
      </QueryClientProvider>,
    );
  }

  afterEach(() => { localStorage.clear(); vi.unstubAllGlobals(); });

  it.each([
    ["Viewer", []],
    ["Member", ["workflows.write"]],
  ] as const)("offers a %s no add, no unbind and no re-link", async (role, permissions) => {
    renderAs(team(role, [...permissions]));

    await waitFor(() => expect(screen.getByText("acme/web")).toBeTruthy());
    await waitFor(() => expect(screen.getAllByTitle("Open in provider")).toHaveLength(2));

    expect(screen.queryByTitle("Unbind")).toBeNull();
    expect(screen.queryByRole("button", { name: /Re-link credential|Connect a credential to fix/ })).toBeNull();
    expect(screen.queryByRole("button", { name: /Add repository/ })).toBeNull();
    expect(screen.getByRole("button", { name: /Connect remote/ })).toBeTruthy();   // a personal token is self-service
  });

  it("gives an admin add, unbind on every row and re-link on the broken one", async () => {
    renderAs(team("Admin", ["repos.manage", "credentials.manage", "members.manage"]));

    await waitFor(() => expect(screen.getAllByTitle("Unbind")).toHaveLength(2));

    expect(screen.getByRole("button", { name: /Re-link credential/ })).toBeTruthy();
    expect(screen.getByRole("button", { name: /Add repository/ })).toBeTruthy();
  });

  // ── Connection health: the chip belongs on EVERY repository whose credential is not healthy, not only the ones flipped to Error ──

  const admin = () => team("Admin", ["repos.manage", "credentials.manage", "members.manage"]);
  const credential = (id: string, over: Record<string, unknown> = {}) => ({ ...credentials[1], id, displayName: `cred ${id}`, ...over });
  const onCredential = (id: string | null, over: Partial<RepositorySummary> = {}) => repo({ id: `r-${id ?? "none"}`, fullPath: `acme/${id ?? "none"}`, name: String(id ?? "none"), credentialId: id, ...over });

  it("flags a repository still marked Active whose credential was refused, disconnected or is gone", async () => {
    renderAs(admin(), {
      credentials: [credential("rejected", { status: "Error", authType: "OAuth", lastError: "invalid_grant" }), credential("gone", { status: "Revoked" }), credential("left", { health: "owner_left" }), credential("fine")],
      repositories: [onCredential("rejected"), onCredential("gone"), onCredential("left"), onCredential("fine"), onCredential("retired")],   // "retired" is in no list: soft-deleted
    });

    await waitFor(() => expect(screen.getByText("acme/retired")).toBeTruthy());

    const chips = Array.from(document.querySelectorAll("[data-issue]:not(tr)")).map((e) => [e.getAttribute("data-issue"), e.textContent?.trim()]);
    expect(chips).toEqual([["rejected", "needs reconnect"], ["disconnected", "credential disconnected"], ["owner_left", "owner left"], ["retired", "credential disconnected"]]);
    expect(screen.getByText("4 repositories need a credential")).toBeTruthy();
  });

  it("says nothing about a repository whose credential works, and a credential list that has not arrived says nothing either", async () => {
    renderAs(admin(), { credentials: [credential("fine")], repositories: [onCredential("fine")] });

    await waitFor(() => expect(screen.getByText("acme/fine")).toBeTruthy());

    expect(document.querySelector("[data-issue]")).toBeNull();
    expect(screen.queryByText(/need a credential/)).toBeNull();
  });

  it("offers the fix on the repository: reconnect the credential, and re-link to another", async () => {
    renderAs(admin(), { credentials: [credential("rejected", { status: "Error", authType: "OAuth" }), credential("spare")], repositories: [onCredential("rejected")] });

    await waitFor(() => expect(screen.getByRole("button", { name: "Reconnect" })).toBeTruthy());

    expect(screen.getByRole("button", { name: /Re-link credential/ })).toBeTruthy();
  });

  it("offers a pasted token a Replace token, not a Reconnect", async () => {
    renderAs(admin(), { credentials: [credential("rejected", { status: "Error", authType: "Pat" })], repositories: [onCredential("rejected")] });

    await waitFor(() => expect(screen.getByRole("button", { name: "Replace token" })).toBeTruthy());

    expect(screen.queryByRole("button", { name: "Reconnect" })).toBeNull();
  });

  it("offers a member who owns a refused pasted token its Replace token but not the re-link, and tells another member who to ask", async () => {
    const mine = credential("mine", { status: "Error", authType: "Pat", ownerUserId: "u1" });
    const theirs = credential("theirs", { status: "Error", authType: "Pat", ownerUserId: "u2", ownerUserName: "Ada" });
    renderAs(team("Member", ["workflows.write"]), { credentials: [mine, theirs], repositories: [onCredential("mine"), onCredential("theirs")] });

    await waitFor(() => expect(screen.getByText("acme/theirs")).toBeTruthy());

    expect(screen.getAllByRole("button", { name: "Replace token" })).toHaveLength(1);   // only the one that is theirs: PUT /credentials/{id}/token passes an owner on membership
    expect(screen.queryByRole("button", { name: /Re-link credential/ })).toBeNull();
    expect(screen.getByText("Ask a team Admin or Owner to fix this connection.")).toBeTruthy();
  });

  it.each([
    ["Member", ["workflows.write"]],
    ["Viewer", []],
  ] as const)("offers a %s Reconnect on their own refused sign-in but not on a teammate's: starting a reconnect passes the credential's own person on membership, anyone else needs credentials.manage", async (role, permissions) => {
    const mine = credential("mine", { status: "Error", authType: "OAuth", ownerUserId: "u1" });
    const theirs = credential("theirs", { status: "Error", authType: "OAuth", ownerUserId: "u2", ownerUserName: "Ada" });
    renderAs(team(role, [...permissions]), { credentials: [mine, theirs], repositories: [onCredential("mine"), onCredential("theirs")] });

    await waitFor(() => expect(screen.getByText("acme/theirs")).toBeTruthy());

    expect(screen.getAllByRole("button", { name: "Reconnect" })).toHaveLength(1);   // only the one that is theirs
    expect(screen.queryByRole("button", { name: /Re-link credential/ })).toBeNull();
    expect(screen.getByText("Ask a team Admin or Owner to fix this connection.")).toBeTruthy();   // for the teammate's row
  });

  it("offers an admin a Reconnect on a teammate's refused sign-in, because credentials.manage reaches every credential of the team", async () => {
    const theirs = credential("theirs", { status: "Error", authType: "OAuth", ownerUserId: "u2", ownerUserName: "Ada" });
    renderAs(admin(), { credentials: [theirs], repositories: [onCredential("theirs")] });

    await waitFor(() => expect(screen.getByRole("button", { name: "Reconnect" })).toBeTruthy());
  });

  // ── What the server says the caller may repair beats what the role suggests: the reach over a credential's person is the server's to know ──

  it("offers a Reconnect only where the server says the caller may — an admin has credentials.manage and still cannot sign in as a peer Admin or an Owner", async () => {
    const peers = credential("peers", { status: "Error", authType: "OAuth", ownerUserId: "u2", ownerUserName: "Ada", callerMayRepair: false });
    const members = credential("members", { status: "Error", authType: "OAuth", ownerUserId: "u3", ownerUserName: "Milo", callerMayRepair: true });
    renderAs(admin(), { credentials: [peers, members], repositories: [onCredential("peers"), onCredential("members")] });

    await waitFor(() => expect(screen.getByText("acme/peers")).toBeTruthy());

    expect(screen.getAllByRole("button", { name: "Reconnect" })).toHaveLength(1);   // only the Member's sign-in
    expect(screen.getAllByRole("button", { name: /Re-link credential/ })).toHaveLength(2);   // moving off a credential is still the admin's, for both rows
  });

  it("offers no Replace token on a token whose person left, however the role reads, and no Reconnect on a sign-in the server refuses", async () => {
    const left = credential("left", { status: "Error", authType: "Pat", ownerUserId: "u2", ownerUserName: "Ada", callerMayRepair: false });
    const outranked = credential("outranked", { status: "Error", authType: "OAuth", ownerUserId: "u3", ownerUserName: "Olive", callerMayRepair: false });
    renderAs(admin(), { credentials: [left, outranked], repositories: [onCredential("left"), onCredential("outranked")] });

    await waitFor(() => expect(screen.getByText("acme/outranked")).toBeTruthy());

    expect(screen.queryByRole("button", { name: "Replace token" })).toBeNull();
    expect(screen.queryByRole("button", { name: "Reconnect" })).toBeNull();
  });

  it("tells a member who may not repair a credential who to ask, whatever they own, when the server says no", async () => {
    const mine = credential("mine", { status: "Error", authType: "OAuth", ownerUserId: "u1", callerMayRepair: false });
    renderAs(team("Member", ["workflows.write"]), { credentials: [mine], repositories: [onCredential("mine")] });

    await waitFor(() => expect(screen.getByText("acme/mine")).toBeTruthy());

    expect(screen.queryByRole("button", { name: "Reconnect" })).toBeNull();
    expect(screen.getByText("Ask a team Admin or Owner to fix this connection.")).toBeTruthy();
  });

  it("offers a teammate's sign-in to a member the server says may repair it, as it says nothing else about roles", async () => {
    const theirs = credential("theirs", { status: "Error", authType: "OAuth", ownerUserId: "u2", ownerUserName: "Ada", callerMayRepair: true });
    renderAs(team("Member", ["workflows.write"]), { credentials: [theirs], repositories: [onCredential("theirs")] });

    await waitFor(() => expect(screen.getByRole("button", { name: "Reconnect" })).toBeTruthy());
  });
});
