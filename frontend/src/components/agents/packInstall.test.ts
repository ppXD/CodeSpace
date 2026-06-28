import { describe, expect, it } from "vitest";

import { installSummary, repoLabel } from "./packInstall";

describe("repoLabel", () => {
  it("reduces a github URL to owner/repo", () => {
    expect(repoLabel("https://github.com/obra/superpowers")).toBe("obra/superpowers");
    expect(repoLabel("github.com/contains-studio/agents")).toBe("contains-studio/agents");
  });

  it("strips a trailing .git and slashes", () => {
    expect(repoLabel("https://gitlab.com/group/proj.git/")).toBe("group/proj");
  });

  it("is blank for empty input", () => {
    expect(repoLabel("")).toBe("");
    expect(repoLabel("   ")).toBe("");
  });
});

describe("installSummary", () => {
  it("joins the non-zero kinds", () => {
    expect(installSummary(1, 4)).toBe("Installing 1 agent + 4 skills");
    expect(installSummary(2, 0)).toBe("Installing 2 agents");
    expect(installSummary(0, 1)).toBe("Installing 1 skill");
  });

  it("reports nothing selected", () => {
    expect(installSummary(0, 0)).toBe("Nothing selected");
  });
});
