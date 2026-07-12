import { describe, expect, it } from "vitest";

import { composeIntent, type IntentResolver } from "./intentCompose";

// A stub resolver: resolves a fixed set, reports loading/unresolved otherwise. Lets the pure composer be
// tested without any hooks.
const resolver = (opts?: { loadingKinds?: string[]; names?: Record<string, string> }): IntentResolver => ({
  resolve(kind, id) {
    const name = opts?.names?.[`${kind}:${id}`];
    if (name) return { status: "resolved", name };
    if (opts?.loadingKinds?.includes(kind)) return { status: "loading" };
    return { status: "unresolved" };
  },
});

// git.open_pr-shaped schemas: x-intent on the (otherwise empty) ConfigSchema root; the real fields live in
// the InputSchema, where repositoryId carries x-selector "repository".
const configSchema = {
  type: "object",
  properties: {},
  "x-intent": 'Open a {draft?draft }pull request titled "{title}" from {sourceBranch} into {targetBranch} on {repositoryId}.',
  "x-intentPlaceholders": { title: "an untitled PR", sourceBranch: "a source branch", targetBranch: "a target branch", repositoryId: "a repository" },
};
const inputSchema = {
  type: "object",
  properties: {
    repositoryId: { type: "string", "x-selector": "repository" },
    title: { type: "string" },
    sourceBranch: { type: "string" },
    targetBranch: { type: "string" },
    draft: { type: "boolean" },
  },
};
const compose = (inputs: Record<string, unknown>, r = resolver({ names: { "repository:repo-1": "acme/auth-service" } })) =>
  composeIntent(configSchema, inputSchema, {}, inputs, r);

const text = (segs: ReturnType<typeof composeIntent>) => (segs ?? []).map((s) => ("text" in s ? s.text : "name" in s ? s.name : "label" in s ? s.label : "")).join("");

describe("composeIntent", () => {
  it("returns null when the ConfigSchema declares no x-intent (opt-out gate)", () => {
    expect(composeIntent({ type: "object", properties: {} }, inputSchema, {}, {}, resolver())).toBeNull();
    expect(composeIntent({ "x-intent": "   " }, {}, {}, {}, resolver())).toBeNull();
    expect(composeIntent({ "x-intent": 42 }, {}, {}, {}, resolver())).toBeNull();
  });

  it("resolves an entity id to a friendly NAME — and never leaks the raw GUID", () => {
    const segs = compose({ repositoryId: "repo-1", title: "Release 1.4", sourceBranch: "feature/x", targetBranch: "main" });
    expect(segs).toContainEqual({ type: "entity", name: "acme/auth-service", kind: "repository" });
    expect(text(segs)).not.toContain("repo-1");
    expect(text(segs)).toBe('Open a pull request titled "Release 1.4" from feature/x into main on acme/auth-service.');
  });

  it("renders a muted prompt (never a GUID) while the catalog is loading", () => {
    const segs = compose({ repositoryId: "repo-1", title: "T", sourceBranch: "a", targetBranch: "b" }, resolver({ loadingKinds: ["repository"] }));
    expect(segs).toContainEqual({ type: "prompt", text: "a repository" });
    expect(text(segs)).not.toContain("repo-1");
  });

  it("renders a muted prompt (never a GUID) when the id is unresolved", () => {
    const segs = compose({ repositoryId: "gone", title: "T", sourceBranch: "a", targetBranch: "b" });
    expect(segs).toContainEqual({ type: "prompt", text: "a repository" });
    expect(text(segs)).not.toContain("gone");
  });

  it("renders an unset field as its x-intentPlaceholders prompt, falling back to the humanized key", () => {
    const segs = compose({ repositoryId: "repo-1", sourceBranch: "a", targetBranch: "b" }); // title unset
    expect(segs).toContainEqual({ type: "prompt", text: "an untitled PR" });
    // no placeholder entry → humanized key
    const segs2 = composeIntent({ "x-intent": "Wait {mystery}." }, {}, {}, {}, resolver());
    expect(segs2).toContainEqual({ type: "prompt", text: "mystery" });
  });

  it("renders a bound {{ref}} as a ref chip, NOT run through entity resolution", () => {
    const segs = compose({ repositoryId: "repo-1", title: "T", sourceBranch: "{{trigger.branch}}", targetBranch: "main" });
    expect(segs).toContainEqual({ type: "ref", label: "trigger.branch" });
    // even though repositoryId is an entity field, a {{ref}} value on a NON-entity field stays a ref:
    const segs2 = compose({ repositoryId: "{{trigger.repositoryId}}", title: "T", sourceBranch: "a", targetBranch: "b" });
    expect(segs2).toContainEqual({ type: "ref", label: "trigger.repositoryId" });
    expect(segs2!.some((s) => s.type === "entity")).toBe(false);
  });

  it("emits {flag?text} only when the boolean is truthy", () => {
    expect(text(compose({ repositoryId: "repo-1", title: "T", sourceBranch: "a", targetBranch: "b", draft: true }))).toContain("draft pull request");
    expect(text(compose({ repositoryId: "repo-1", title: "T", sourceBranch: "a", targetBranch: "b", draft: false }))).toContain("a pull request");
    expect(text(compose({ repositoryId: "repo-1", title: "T", sourceBranch: "a", targetBranch: "b" }))).not.toContain("draft ");
  });

  it("prefers config over inputs, and resolves a nested dotted path", () => {
    const cs = { "x-intent": "Run {agentProfile.repositoryId}.", properties: { agentProfile: { type: "object", properties: { repositoryId: { "x-selector": "repository" } } } } };
    const segs = composeIntent(cs, {}, { agentProfile: { repositoryId: "repo-1" } }, {}, resolver({ names: { "repository:repo-1": "acme/auth-service" } }));
    expect(segs).toContainEqual({ type: "entity", name: "acme/auth-service", kind: "repository" });
  });

  it("prints a non-entity literal verbatim and tolerates stray braces", () => {
    const segs = composeIntent({ "x-intent": "Say {greeting} }now{." }, {}, { greeting: "hi" }, {}, resolver());
    expect(text(segs)).toBe("Say hi }now{.");
  });
});
