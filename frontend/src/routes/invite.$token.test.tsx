import { fireEvent, screen, waitFor } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

import { renderRoute, stubFetch } from "@/test/route-harness";

/**
 * The invite page is the only route a person can open before they have an account, so its failure
 * mode matters as much as its success one: a dead token must land the visitor on a page that
 * explains itself, not on a sign-in form for an account they don't have.
 */
describe("invite acceptance", () => {
  const preview = {
    teamName: "Platform Team",
    invitedByName: "Mars",
    role: "Member",
    email: "maya@team.dev",
    expiresAt: new Date(Date.now() + 6 * 86_400_000).toISOString(),
    forSignedInAccount: false,
  };

  afterEach(() => { localStorage.clear(); vi.unstubAllGlobals(); });

  it("names the team, the inviter and the role before asking for anything", async () => {
    stubFetch({ "/api/invitations/": preview });

    await renderRoute("/invite/good-token");

    await waitFor(() => expect(screen.getByText("Create your account")).toBeTruthy());
    // The team is named twice on purpose — beside the mark and on the button — so match all.
    expect(screen.getAllByText(/Platform Team/).length).toBeGreaterThan(0);
    expect(screen.getByText("Mars")).toBeTruthy();
    expect(screen.getByText("Member")).toBeTruthy();
    expect(screen.getByText(/maya@team\.dev/)).toBeTruthy();
  });

  it("asks the invited account, signed in, to join rather than to set a password", async () => {
    stubFetch({ "/api/invitations/": { ...preview, forSignedInAccount: true } });

    await renderRoute("/invite/good-token");

    await waitFor(() => expect(screen.getByText("Join the team")).toBeTruthy());
    expect(screen.queryByText("Set a password")).toBeNull();
  });

  it("offers everyone else the create form with a way to sign in instead", async () => {
    // The preview no longer says whether the address has an account, so a visitor who already has one is
    // pointed at sign-in from the same form rather than being told up front.
    stubFetch({ "/api/invitations/": preview });

    await renderRoute("/invite/good-token");

    await waitFor(() => expect(screen.getByText("Create your account")).toBeTruthy());
    expect(screen.getByText("Set a password")).toBeTruthy();
    expect(screen.getByRole("link", { name: "Sign in" }).getAttribute("href")).toBe("/signin");
  });

  it("lands a joining account signed in on the session cookie without writing the token to localStorage", async () => {
    const user = { id: "u-1", email: "maya@team.dev", name: "Maya", passwordMustChange: false, permissions: [], teams: [{ id: "t-9", slug: "platform-team", name: "Platform Team" }] };
    const answer = (body: unknown, status = 200) => new Response(body === null ? null : JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });
    vi.stubGlobal("fetch", vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const path = new URL(String(input), "http://spa.test").pathname;
      const method = init?.method ?? "GET";
      if (method === "GET" && path === "/api/invitations/good-token") return answer({ ...preview, forSignedInAccount: true });
      if (method === "POST" && path === "/api/invitations/good-token/accept") return answer({ token: "minted-token", expiresAt: preview.expiresAt, user });
      if (path === "/api/auth/session") return answer(null, 204);   // an API with the session cookie confirms it
      return answer({ code: "not_found" }, 404);
    }));
    const tokenWrites: string[] = [];
    const setItem = localStorage.setItem.bind(localStorage);
    vi.spyOn(localStorage, "setItem").mockImplementation((key: string, value: string) => {
      if (key === "codespace.jwt") tokenWrites.push(value);
      setItem(key, value);
    });

    await renderRoute("/invite/good-token");
    fireEvent.click(await screen.findByRole("button", { name: /Join/ }));

    await waitFor(() => expect(localStorage.getItem("codespace.activeTeamId")).toBe("t-9"));
    expect(localStorage.getItem("codespace.session")).toBe("cookie");
    expect(tokenWrites).toEqual([]);
    vi.restoreAllMocks();
  });

  it("explains a dead token in place instead of bouncing to sign-in", async () => {
    // An unmatched URL in the stub is a 404, which is what a revoked or expired token returns.
    stubFetch({});

    const { router } = await renderRoute("/invite/dead-token");

    await waitFor(() => expect(screen.getByText("Invitation unavailable")).toBeTruthy());
    expect(router.state.location.pathname).toBe("/invite/dead-token");
  });

  it("says nothing about the team when the token is not valid", async () => {
    // The link IS the credential: a wrong guess must not confirm that a team by that name exists.
    stubFetch({});

    await renderRoute("/invite/dead-token");

    await waitFor(() => expect(screen.getByText("Invitation unavailable")).toBeTruthy());
    expect(screen.queryByText(/Platform Team/)).toBeNull();
    expect(screen.queryByText("Mars")).toBeNull();
  });
});
