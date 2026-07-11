import { Outlet } from "@tanstack/react-router";
import { waitFor } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

import type { MeResponse, MeTeam } from "@/api/types";
import { renderRoute, stubFetch } from "@/test/route-harness";
import { validateAgentsSearch } from "./_app.teams.$teamSlug.agents.index";

vi.mock("@/components/AppShell", () => ({ AppShell: () => <Outlet /> }));

/**
 * The Agents roster's view — search text, origin tab and stats window — is deep-linkable (?q=&origin=&window=)
 * so a filtered roster is shareable and Back/Forward work (the audit's roster deep-link). Proven against the real router.
 */
describe("agents roster deep-link", () => {
  const acme: MeTeam = {
    id: "a", slug: "acme", name: "Acme", kind: "Workspace", role: "Owner",
    memberCount: 1, repositoryCount: 0, projectCount: 0, workflowCount: 0,
  };
  const me = { id: "u", email: "u@test.local", name: "U", teams: [acme], passwordMustChange: false } satisfies MeResponse;

  afterEach(() => { localStorage.clear(); vi.unstubAllGlobals(); });

  it("carries ?origin=, ?window= and ?q= through the real router to the roster", async () => {
    stubFetch({
      "/api/agents": [],
      "/api/users/me": me,
    });

    const { router } = await renderRoute("/teams/acme/agents?origin=Imported&window=30&q=foo");

    await waitFor(() => expect(router.state.location.pathname).toBe("/teams/acme/agents"));
    // The raw location search survives to the route; TanStack's parser types window as a number (30), which the
    // validator coerces back to a string — asserted directly in the whitelist tests below.
    expect(router.state.location.search).toMatchObject({ origin: "Imported", q: "foo" });
    expect(String(router.state.location.search.window)).toBe("30");
  });

  describe("validateAgentsSearch whitelist", () => {
    it("keeps a search, a non-default origin and a non-default window", () => {
      expect(validateAgentsSearch({ q: "foo", origin: "Imported", window: "30" })).toEqual({ q: "foo", origin: "Imported", window: "30" });
    });

    it("coerces a numeric window / search back to a string (TanStack parses ?window=30 as a number)", () => {
      expect(validateAgentsSearch({ window: 30, q: 2024 })).toEqual({ window: "30", q: "2024" });
    });

    it("drops the defaults (empty search, 'all' origin, 7-day window) for a clean URL", () => {
      expect(validateAgentsSearch({ q: "", origin: "all", window: "7" })).toEqual({});
      expect(validateAgentsSearch({ window: 7 })).toEqual({});
    });

    it("drops an unknown origin and an unknown window", () => {
      expect(validateAgentsSearch({ origin: "bogus", window: "99" })).toEqual({});
    });
  });
});
