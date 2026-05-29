import { describe, expect, it } from "vitest";

import type { ProviderKind } from "@/api/types";

import { providerSupportsTeamServiceCredential } from "./teamCredentials";

describe("providerSupportsTeamServiceCredential", () => {
  it("supports GitLab (paste-able group access token)", () => {
    expect(providerSupportsTeamServiceCredential("GitLab")).toBe(true);
  });

  it("does not support GitHub yet (its mechanism is a GitHub App installation, not built)", () => {
    expect(providerSupportsTeamServiceCredential("GitHub")).toBe(false);
  });

  it("does not support a bare Git remote (no team identity to mint from)", () => {
    expect(providerSupportsTeamServiceCredential("Git")).toBe(false);
  });

  it("covers every ProviderKind — GitLab is the only one true today", () => {
    const all: ProviderKind[] = ["GitHub", "GitLab", "Git"];
    const supported = all.filter(providerSupportsTeamServiceCredential);
    expect(supported).toEqual(["GitLab"]);
  });
});
