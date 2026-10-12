import type { QueryClient } from "@tanstack/react-query";
import { Outlet } from "@tanstack/react-router";
import { fireEvent, screen, waitFor } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

import type { MeResponse, MeTeam } from "@/api/types";
import { renderRoute } from "@/test/route-harness";

vi.mock("@/components/AppShell", () => ({ AppShell: () => <Outlet /> }));

/**
 * The three pages that receive a session — sign-in, invitation accept, password change — keep it out of every piece of
 * state the page holds. react-query keeps a mutation's result and its variables in a cache any script in the page can
 * walk to from the DOM (the React root → the QueryClient), for five minutes after the page moves on by default. A token
 * there is the same theft as a token in localStorage; a password there is worse.
 *
 * Every state each mutation passes through is recorded as it happens, so a token that sat in the cache for one render
 * fails the test, not only one still there at the end.
 */
describe("pages that receive a session", () => {
  const TOKEN = "SECRET-SESSION-JWT";
  const PASSWORD = "correct-horse-battery";
  const NEW_PASSWORD = "a-brand-new-passphrase";
  const team: MeTeam = { id: "t-9", slug: "platform-team", name: "Platform Team", kind: "Workspace", role: "Member", permissions: [], memberCount: 2, repositoryCount: 0, projectCount: 0, workflowCount: 0 };
  const user: MeResponse = { id: "u-1", email: "maya@team.dev", name: "Maya", passwordMustChange: false, permissions: [], teams: [team] };
  const preview = { teamName: "Platform Team", invitedByName: "Mars", role: "Member", email: "maya@team.dev", expiresAt: new Date(Date.now() + 86_400_000).toISOString(), forSignedInAccount: true };

  afterEach(() => {
    vi.unstubAllGlobals();
    localStorage.clear();
  });

  /** An API that confirms the session cookie, and — as an API answering a bearer client would — puts the token in every session it mints. */
  function stubApi() {
    const minted = { token: TOKEN, expiresAt: "2026-10-13T09:00:00Z", user };

    vi.stubGlobal("fetch", vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const request = new Request(input instanceof Request ? input : new URL(String(input), "http://spa.test"), init);
      const route = `${request.method} ${new URL(request.url).pathname}`;

      if (route === "POST /api/auth/sign-in" || route === "POST /api/auth/change-password" || route === "POST /api/invitations/good-token/accept") return json(minted);
      if (route === "GET /api/invitations/good-token") return json(preview);
      if (route === "GET /api/users/me") return json(user);
      if (route.endsWith(" /api/auth/session")) return new Response(null, { status: 204 });

      return json({ code: "not_found" }, 404);
    }));
  }

  function json(body: unknown, status = 200): Response {
    return new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });
  }

  /** Every state any mutation in the cache passes through, serialised at the moment it happened. */
  function recordMutationStates(queryClient: QueryClient): string[] {
    const seen: string[] = [];

    queryClient.getMutationCache().subscribe((event) => { if (event.mutation) seen.push(JSON.stringify(event.mutation.state)); });

    return seen;
  }

  async function expectNothingLeftBehind(queryClient: QueryClient, seen: string[]) {
    expect(seen.length).toBeGreaterThan(0);   // the control: the recorder saw the mutation run
    expect(seen.filter((state) => state.includes(TOKEN))).toEqual([]);

    // Once the page has moved on, the mutation — and the password in its variables — is gone, not kept for minutes.
    await waitFor(() => expect(queryClient.getMutationCache().getAll()).toEqual([]));
  }

  it("signing in", async () => {
    stubApi();
    const view = await renderRoute("/signin");
    localStorage.clear();   // the harness marks a session; this visitor has none
    const seen = recordMutationStates(view.queryClient);

    fireEvent.change(screen.getByLabelText("Email or name"), { target: { value: "maya@team.dev" } });
    fireEvent.change(screen.getByLabelText("Password"), { target: { value: PASSWORD } });
    fireEvent.click(screen.getByRole("button", { name: "Sign in" }));

    await waitFor(() => expect(view.currentPath()).not.toBe("/signin"));

    expect(localStorage.getItem("codespace.session")).toBe("cookie");
    await expectNothingLeftBehind(view.queryClient, seen);
  });

  it("accepting an invitation", async () => {
    stubApi();
    const view = await renderRoute("/invite/good-token");
    const seen = recordMutationStates(view.queryClient);

    fireEvent.click(await screen.findByRole("button", { name: /Join/ }));

    await waitFor(() => expect(view.currentPath()).not.toBe("/invite/good-token"));

    expect(localStorage.getItem("codespace.activeTeamId")).toBe("t-9");
    await expectNothingLeftBehind(view.queryClient, seen);
  });

  it("changing the password", async () => {
    stubApi();
    const view = await renderRoute("/change-password");
    const seen = recordMutationStates(view.queryClient);

    fireEvent.change(screen.getByLabelText("Current password"), { target: { value: PASSWORD } });
    fireEvent.change(screen.getByLabelText("New password"), { target: { value: NEW_PASSWORD } });
    fireEvent.change(screen.getByLabelText("Confirm new password"), { target: { value: NEW_PASSWORD } });
    fireEvent.click(screen.getByRole("button", { name: "Rotate password" }));

    await waitFor(() => expect(view.currentPath()).not.toBe("/change-password"));

    expect(localStorage.getItem("codespace.session")).toBe("cookie");
    await expectNothingLeftBehind(view.queryClient, seen);
  });
});
