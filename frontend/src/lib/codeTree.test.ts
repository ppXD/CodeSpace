import { describe, expect, it } from "vitest";

import type { RemoteTreeEntry, RemoteTreeEntryType } from "@/api/types";
import { buildBreadcrumbs, formatBytes, formatCount, isMarkdownName, isReadmeName, languageColor, parentPath, pickReadme, relativeTime, sortTreeEntries } from "./codeTree";

const entry = (name: string, type: RemoteTreeEntryType, path = name): RemoteTreeEntry => ({ name, path, type });

describe("sortTreeEntries", () => {
  it("puts directories before files, each alphabetical (case-insensitive)", () => {
    const input = [
      entry("zeta.ts", "File"),
      entry("Alpha", "Directory"),
      entry("beta.ts", "File"),
      entry("alpha-dir", "Directory"),
      entry("README.md", "File"),
    ];

    expect(sortTreeEntries(input).map(e => e.name)).toEqual([
      "Alpha", "alpha-dir",          // dirs first, case-insensitive
      "beta.ts", "README.md", "zeta.ts", // then files, case-insensitive
    ]);
  });

  it("treats submodules and symlinks as leaves (sorted with files)", () => {
    const input = [entry("sub", "Submodule"), entry("dir", "Directory"), entry("link", "Symlink")];
    expect(sortTreeEntries(input).map(e => e.name)).toEqual(["dir", "link", "sub"]);
  });

  it("does not mutate the input array", () => {
    const input = [entry("b.ts", "File"), entry("a", "Directory")];
    const snapshot = input.map(e => e.name);
    sortTreeEntries(input);
    expect(input.map(e => e.name)).toEqual(snapshot);
  });
});

describe("buildBreadcrumbs", () => {
  it("accumulates each segment's full path", () => {
    expect(buildBreadcrumbs("src/api/types.ts")).toEqual([
      { name: "src", path: "src" },
      { name: "api", path: "src/api" },
      { name: "types.ts", path: "src/api/types.ts" },
    ]);
  });

  it("returns [] for the root", () => {
    expect(buildBreadcrumbs("")).toEqual([]);
    expect(buildBreadcrumbs("/")).toEqual([]);
  });

  it("ignores leading/trailing slashes and empty segments", () => {
    expect(buildBreadcrumbs("/a//b/")).toEqual([
      { name: "a", path: "a" },
      { name: "b", path: "a/b" },
    ]);
  });
});

describe("parentPath", () => {
  it.each([
    ["src/api/types.ts", "src/api"],
    ["src", ""],
    ["", ""],
    ["a/b/c/", "a/b"],
  ])("parentPath(%j) → %j", (input, expected) => {
    expect(parentPath(input)).toBe(expected);
  });
});

describe("isReadmeName", () => {
  it.each(["README", "README.md", "readme.markdown", "ReadMe.rst", "README.txt", "readme.adoc"])(
    "accepts %j", name => expect(isReadmeName(name)).toBe(true));

  it.each(["readme.png", "not-readme.md", "read me.md", "readmexyz", "changelog.md"])(
    "rejects %j", name => expect(isReadmeName(name)).toBe(false));
});

describe("isMarkdownName", () => {
  it.each(["x.md", "X.MARKDOWN", "notes.mkd"])("accepts %j", n => expect(isMarkdownName(n)).toBe(true));
  it.each(["x.txt", "readme", "a.mdx"])("rejects %j", n => expect(isMarkdownName(n)).toBe(false));
});

describe("pickReadme", () => {
  it("prefers a markdown README over a plain-text one", () => {
    const got = pickReadme([entry("README.txt", "File"), entry("README.md", "File"), entry("src", "Directory")]);
    expect(got?.name).toBe("README.md");
  });

  it("ignores a directory named README", () => {
    expect(pickReadme([entry("README", "Directory")])).toBeNull();
  });

  it("returns null when there is no README", () => {
    expect(pickReadme([entry("index.ts", "File"), entry("src", "Directory")])).toBeNull();
  });
});

describe("formatBytes", () => {
  it.each([
    [0, "0 B"],
    [512, "512 B"],
    [1024, "1.0 KB"],
    [1536, "1.5 KB"],
    [10_240, "10 KB"],
    [1_048_576, "1.0 MB"],
  ])("formatBytes(%i) → %j", (input, expected) => {
    expect(formatBytes(input)).toBe(expected);
  });

  it("returns empty string for invalid sizes", () => {
    expect(formatBytes(-1)).toBe("");
    expect(formatBytes(NaN)).toBe("");
  });
});

describe("relativeTime", () => {
  const now = Date.parse("2026-06-09T12:00:00Z");
  const ago = (ms: number) => new Date(now - ms).toISOString();
  const SEC = 1000, MIN = 60 * SEC, HR = 60 * MIN, DAY = 24 * HR;

  it.each([
    [ago(30 * SEC), "just now"],
    [ago(1 * MIN), "1 minute ago"],
    [ago(5 * MIN), "5 minutes ago"],
    [ago(1 * HR), "1 hour ago"],
    [ago(3 * DAY), "3 days ago"],
    [ago(14 * DAY), "2 weeks ago"],
    [ago(60 * DAY), "2 months ago"],
    [ago(800 * DAY), "2 years ago"],
  ])("formats %j → %j", (iso, expected) => {
    expect(relativeTime(iso, now)).toBe(expected);
  });

  it("returns empty for null/invalid", () => {
    expect(relativeTime(null, now)).toBe("");
    expect(relativeTime("not-a-date", now)).toBe("");
  });
});

describe("formatCount", () => {
  it.each([[0, "0"], [42, "42"], [1128, "1,128"], [1048576, "1,048,576"]])(
    "formatCount(%i) → %j", (input, expected) => expect(formatCount(input)).toBe(expected));
});

describe("languageColor", () => {
  it("returns the known linguist color (case-insensitive)", () => {
    expect(languageColor("C#")).toBe("#178600");
    expect(languageColor("typescript")).toBe("#3178c6");
  });

  it("returns a stable generated hue for unknown languages", () => {
    const a = languageColor("Brainfuck");
    expect(a).toBe(languageColor("Brainfuck"));
    expect(a).toMatch(/^hsl\(/);
  });
});
