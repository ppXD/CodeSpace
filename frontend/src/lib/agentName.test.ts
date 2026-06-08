import { describe, expect, it } from "vitest";

import { DEFAULT_AGENT_NAME, deriveAgentName } from "./agentName";

describe("deriveAgentName", () => {
  it("falls back to the default for empty / whitespace-only input", () => {
    expect(deriveAgentName("")).toBe(DEFAULT_AGENT_NAME);
    expect(deriveAgentName("   ")).toBe(DEFAULT_AGENT_NAME);
    expect(deriveAgentName("\n\n  \n")).toBe(DEFAULT_AGENT_NAME);
  });

  it("returns a short single line unchanged (trimmed)", () => {
    expect(deriveAgentName("Review every PR for security issues")).toBe("Review every PR for security issues");
    expect(deriveAgentName("  Triage incoming bugs  ")).toBe("Triage incoming bugs");
  });

  it("uses the first non-empty line of a multi-line description", () => {
    expect(deriveAgentName("Summarize the PR\n\nthen post it to Slack")).toBe("Summarize the PR");
    expect(deriveAgentName("\n\nLabel stale issues\nand close them")).toBe("Label stale issues");
  });

  it("collapses internal whitespace runs to single spaces", () => {
    expect(deriveAgentName("Review    the\tdiff")).toBe("Review the diff");
  });

  it("caps a long line at a word boundary without an ellipsis", () => {
    const long = "Review the pull request diff for security regressions and post a detailed summary to the team channel";
    const name = deriveAgentName(long);
    expect(name.length).toBeLessThanOrEqual(60);
    expect(name.endsWith("…")).toBe(false);
    expect(name).toBe("Review the pull request diff for security regressions and");
    expect(long.startsWith(name)).toBe(true);
  });

  it("hard-caps a single very long token (no word boundary to honour)", () => {
    const token = "a".repeat(120);
    const name = deriveAgentName(token);
    expect(name).toBe("a".repeat(60));
  });
});
