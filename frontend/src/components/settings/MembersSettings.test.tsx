import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

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
  const team = (role: MeTeam["role"], permissions: string[]): MeTeam => ({
    id: "t1", slug: "acme", name: "Acme", kind: "Workspace", role, permissions,
    memberCount: 3, repositoryCount: 0, projectCount: 0, workflowCount: 0,
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

  function renderAs(t: MeTeam) {
    localStorage.setItem("codespace.jwt", "test-jwt");
    localStorage.setItem("codespace.activeTeamId", t.id);

    stub({ "/api/users/me": me(t), "/api/teams/members": roster, "/api/teams/invitations": [] });

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

  it("gives the bot no role and no menu", async () => {
    // It is not a person. A dropdown would invite someone to try.
    renderAs(team("Owner", ["members.manage", "team.manage"]));

    await waitFor(() => expect(screen.getByText("CodeSpace")).toBeTruthy());

    expect(screen.queryByRole("button", { name: "Actions for CodeSpace" })).toBeNull();
  });
});
