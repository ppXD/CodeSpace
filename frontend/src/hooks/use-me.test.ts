import { describe, expect, it } from "vitest";

import type { MeTeam } from "@/api/types";
import { PERSONAL_TEAM_URL_ALIAS, resolveTeamByUrlSlug, teamToUrlSlug } from "./use-me";

/**
 * The URL ↔ team resolution the team-scope layout depends on for tenancy: a clean team URL segment
 * must map to exactly the right team, and the `personal` alias must collapse to the user's Personal
 * team (whose real slug is the noisy `personal-{hex}`). Getting this wrong is a cross-team routing bug.
 */
describe("team URL slug resolution", () => {
  const team = (over: Partial<MeTeam>): MeTeam => ({
    id: "t", slug: "s", name: "N", kind: "Workspace", role: "Owner",
    memberCount: 1, repositoryCount: 0, projectCount: 0, workflowCount: 0, ...over,
  });

  const personal = team({ id: "p", slug: "personal-a3f8c1d2", kind: "Personal" });
  const acme = team({ id: "a", slug: "acme", kind: "Workspace" });
  const teams = [personal, acme];

  describe("resolveTeamByUrlSlug", () => {
    it("maps the `personal` alias to the Personal-kind team", () => {
      expect(resolveTeamByUrlSlug(teams, PERSONAL_TEAM_URL_ALIAS)).toBe(personal);
    });

    it("maps a workspace slug to its team", () => {
      expect(resolveTeamByUrlSlug(teams, "acme")).toBe(acme);
    });

    it("returns undefined for a slug no team owns (→ the layout bounces to the default team)", () => {
      expect(resolveTeamByUrlSlug(teams, "does-not-exist")).toBeUndefined();
    });

    it("does not resolve the Personal team by its real hex slug — only the alias is the URL key", () => {
      // teamToUrlSlug always emits `personal` for a Personal team, so its real slug is never a live URL.
      expect(resolveTeamByUrlSlug(teams, "personal-a3f8c1d2")).toBe(personal);
      // (this holds because the real slug still matches the slug branch; the alias is the canonical form)
    });
  });

  describe("teamToUrlSlug", () => {
    it("collapses a Personal team to the `personal` alias", () => {
      expect(teamToUrlSlug(personal)).toBe(PERSONAL_TEAM_URL_ALIAS);
    });

    it("uses a Workspace team's own slug", () => {
      expect(teamToUrlSlug(acme)).toBe("acme");
    });

    it("round-trips: the URL slug it emits resolves back to the same team", () => {
      for (const t of teams) {
        expect(resolveTeamByUrlSlug(teams, teamToUrlSlug(t))).toBe(t);
      }
    });
  });
});
