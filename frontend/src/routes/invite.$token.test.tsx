import { screen, waitFor } from "@testing-library/react";
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
    accountExists: false,
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

  it("asks an existing account to join rather than to set a password", async () => {
    stubFetch({ "/api/invitations/": { ...preview, accountExists: true } });

    await renderRoute("/invite/good-token");

    await waitFor(() => expect(screen.getByText("Join the team")).toBeTruthy());
    expect(screen.queryByText("Set a password")).toBeNull();
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
