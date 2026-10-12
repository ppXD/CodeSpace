import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

import type { TeamInvitationSummary } from "@/api/teams";
import type { MeResponse, MeTeam } from "@/api/types";
import { DialogProvider } from "@/components/dialog";
import { MembersSettings } from "./MembersSettings";

/**
 * The page's job is to show a person what they may do — so the assertions are about what is ABSENT.
 *
 * <p>A control that is present and answers 403 tells the user they may do something and then refuses
 * them; the 403 is the floor that catches a forged request, not the way a role is communicated. Every
 * gate reads `me.permissions`, expanded server-side from the matrix the API refuses on, so these tests
 * drive the component by varying that list rather than by varying a role name.</p>
 */
describe("members settings", () => {
  const team = (role: MeTeam["role"], permissions: string[], over: Partial<MeTeam> = {}): MeTeam => ({
    id: "t1", slug: "acme", name: "Acme", kind: "Workspace", role, permissions,
    memberCount: 3, repositoryCount: 0, projectCount: 0, workflowCount: 0, ...over,
  });

  const me = (t: MeTeam): MeResponse => ({ id: "u-viewer", email: "v@test.local", name: "Viewer", teams: [t], permissions: [], passwordMustChange: false });

  const roster = [
    { userId: "u-owner", name: "Mars P", email: "mars@test.local", avatarUrl: null, isBot: false, role: "Owner" as const, joinedAt: null },
    { userId: "u-other", name: "Alex Kim", email: "alex@test.local", avatarUrl: null, isBot: false, role: "Viewer" as const, joinedAt: "2026-01-01T00:00:00Z" },
    { userId: "u-bot", name: "CodeSpace", email: "bot@test.local", avatarUrl: null, isBot: true, role: null, joinedAt: null },
  ];

  function stub(routes: Record<string, unknown>) {
    vi.stubGlobal("fetch", vi.fn(async (input: RequestInfo | URL) => {
      const url = typeof input === "string" ? input : input.toString();
      const key = Object.keys(routes).find((k) => url.includes(k));
      const body = key === undefined ? undefined : routes[key];
      return new Response(body === undefined ? "" : JSON.stringify(body), {
        status: body === undefined ? 404 : 200,
        headers: { "Content-Type": "application/json" },
      });
    }));
  }

  function renderAs(t: MeTeam, invitations: TeamInvitationSummary[] = []) {
    localStorage.setItem("codespace.jwt", "test-jwt");
    localStorage.setItem("codespace.activeTeamId", t.id);

    stub({ "/api/users/me": me(t), "/api/teams/members": roster, "/api/teams/invitations": invitations });

    const client = new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: 0 } } });

    // DialogProvider because the destructive actions ask before they act — the app mounts it at the
    // root, so a component test without it is testing a shape the product never renders.
    return render(
      <QueryClientProvider client={client}>
        <DialogProvider><MembersSettings /></DialogProvider>
      </QueryClientProvider>,
    );
  }

  afterEach(() => { localStorage.clear(); vi.unstubAllGlobals(); });

  it("offers a viewer no way to manage anyone", async () => {
    renderAs(team("Viewer", []));

    await waitFor(() => expect(screen.getByText("Mars P")).toBeTruthy());

    expect(screen.queryByRole("button", { name: /invite/i })).toBeNull();
    expect(screen.queryByRole("combobox")).toBeNull();
    expect(screen.queryByRole("button", { name: /^Actions for/ })).toBeNull();
  });

  it("does not show a viewer who has been invited", async () => {
    // Who has been offered a seat is management information about people not in the team yet — and
    // the endpoint refuses a Viewer, so requesting it would only produce a failed query.
    renderAs(team("Viewer", []));

    await waitFor(() => expect(screen.getByText("Mars P")).toBeTruthy());

    expect(screen.queryByText("Pending invitations")).toBeNull();
  });

  it("gives an admin the invite action and the pending list", async () => {
    renderAs(team("Admin", ["members.manage"]));

    await waitFor(() => expect(screen.getByText("Mars P")).toBeTruthy());

    expect(screen.getByRole("button", { name: /invite/i })).toBeTruthy();
    expect(screen.getByText("Pending invitations")).toBeTruthy();
  });

  it("will not let an admin edit the owner", async () => {
    // Rank is a reach: never above, never across. The control is not offered rather than offered and
    // refused, which is the same rule the server enforces.
    renderAs(team("Admin", ["members.manage"]));

    await waitFor(() => expect(screen.getByText("Mars P")).toBeTruthy());

    // Exactly one editable row — the admin's own. The owner renders as text, never a control.
    expect(screen.getAllByRole("combobox").length).toBe(1);
    expect(screen.getByText(/^Owner/)).toBeTruthy();
  });

  it("locks the last owner's role and says why", async () => {
    renderAs(team("Owner", ["members.manage", "team.manage"]));

    await waitFor(() => expect(screen.getByText("Mars P")).toBeTruthy());

    expect(screen.getByText(/Owner · locked/)).toBeTruthy();
  });

  it("asks before it removes someone", async () => {
    // The menu used to act on click. Removing a person is not something to discover by having done
    // it, so the click opens a question and the mutation waits for the answer.
    renderAs(team("Owner", ["members.manage", "team.manage"]));

    await waitFor(() => expect(screen.getByText("Alex Kim")).toBeTruthy());

    fireEvent.click(screen.getByRole("button", { name: "Actions for Alex Kim" }));
    fireEvent.click(screen.getByRole("button", { name: "Remove" }));

    await waitFor(() => expect(screen.getByText("Remove Alex Kim?")).toBeTruthy());

    const deletes = () => (globalThis.fetch as ReturnType<typeof vi.fn>).mock.calls
      .filter(([, init]) => (init as RequestInit | undefined)?.method === "DELETE");

    expect(deletes()).toHaveLength(0);

    fireEvent.click(screen.getByRole("button", { name: "Cancel" }));

    await waitFor(() => expect(screen.queryByText("Remove Alex Kim?")).toBeNull());
    expect(deletes()).toHaveLength(0);

    fireEvent.click(screen.getByRole("button", { name: "Actions for Alex Kim" }));
    fireEvent.click(screen.getByRole("button", { name: "Remove" }));

    await waitFor(() => expect(screen.getByText("Remove Alex Kim?")).toBeTruthy());
    fireEvent.click(screen.getAllByRole("button", { name: "Remove" }).at(-1)!);

    await waitFor(() => expect(deletes()).toHaveLength(1));
    expect(deletes()[0][0]).toContain("/api/teams/members/u-other");
  });

  const invitation = (email: string, role: TeamInvitationSummary["role"], invitedByUserId: string): TeamInvitationSummary => ({
    id: `inv-${email}`, email, role, invitedByName: "someone", invitedByUserId, expiresAt: "2099-01-01T00:00:00Z", isExpired: false,
  });

  /** The actions an invitation's menu offers, or null when it offers no menu at all. */
  function actionsFor(email: string): string[] | null {
    const menu = screen.queryByRole("button", { name: `Actions for ${email}` });

    if (menu == null) return null;

    fireEvent.click(menu);
    const offered = ["Regenerate link", "Revoke"].filter((name) => screen.queryByRole("button", { name }) != null);
    fireEvent.click(menu);

    return offered;
  }

  it("offers an admin a new link only within their reach, and revoke up to their own rank", async () => {
    // Regenerating hands the caller a working link to the role the invitation grants, so it follows the member reach
    // rule: below you, or at your own role only when you sent it. Revoking raises nobody, so it reaches every
    // invitation up to your own role, and any you sent. The server answers anything else with invitation_outranks_actor.
    renderAs(team("Admin", ["members.manage"]), [
      invitation("member@test.local", "Member", "u-owner"),
      invitation("own-admin@test.local", "Admin", "u-viewer"),
      invitation("other-admin@test.local", "Admin", "u-owner"),
      invitation("owner@test.local", "Owner", "u-owner"),
      invitation("own-owner@test.local", "Owner", "u-viewer"),   // sent as Owner, demoted since
    ]);

    await waitFor(() => expect(screen.getByText("other-admin@test.local")).toBeTruthy());

    expect(actionsFor("member@test.local")).toEqual(["Regenerate link", "Revoke"]);
    expect(actionsFor("own-admin@test.local")).toEqual(["Regenerate link", "Revoke"]);
    expect(actionsFor("other-admin@test.local")).toEqual(["Revoke"]);
    expect(actionsFor("owner@test.local")).toBeNull();
    expect(actionsFor("own-owner@test.local")).toEqual(["Revoke"]);
  });

  it("offers an owner every invitation, another owner's owner invitation included", async () => {
    // Nobody ranks above an Owner to arbitrate. Without this a leaked Owner link sent by a co-Owner — or by an Owner
    // who has since left — had nobody able to withdraw it.
    renderAs(team("Owner", ["members.manage", "team.manage"]), [
      invitation("co-owner-sent@test.local", "Owner", "u-owner"),
      invitation("departed-sent@test.local", "Owner", "u-gone"),
    ]);

    await waitFor(() => expect(screen.getByText("departed-sent@test.local")).toBeTruthy());

    expect(actionsFor("co-owner-sent@test.local")).toEqual(["Regenerate link", "Revoke"]);
    expect(actionsFor("departed-sent@test.local")).toEqual(["Regenerate link", "Revoke"]);
  });

  it("offers no invite in a personal space", async () => {
    // Its owner genuinely holds members.manage — the matrix expands it from Owner like anywhere else
    // — but the server refuses to invite into a solo space. An Invite button here could only fail.
    renderAs(team("Owner", ["members.manage", "team.manage"], { kind: "Personal" }));

    await waitFor(() => expect(screen.getByText("Mars P")).toBeTruthy());

    expect(screen.queryByRole("button", { name: /Invite/ })).toBeNull();
    expect(screen.queryByText("Pending invitations")).toBeNull();
    expect(screen.getByText(/This is your own space/)).toBeTruthy();
  });

  it("gives the bot no role and no menu", async () => {
    // It is not a person. A dropdown would invite someone to try.
    renderAs(team("Owner", ["members.manage", "team.manage"]));

    await waitFor(() => expect(screen.getByText("CodeSpace")).toBeTruthy());

    expect(screen.queryByRole("button", { name: "Actions for CodeSpace" })).toBeNull();
  });
});
