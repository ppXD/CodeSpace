import { describe, expect, it } from "vitest";

import type {
  NodeManifestDto,
  WorkflowActivationInput,
  WorkflowDefinition,
  WorkflowDetail,
} from "@/api/workflows";

import { deriveActivations } from "./workflowActivations";

/**
 * Pins the "node.config is the source of truth" contract that the trigger inspector
 * relies on. Before this contract was honored, freshly-edited trigger config never
 * reached the activation row, so the backend matcher saw an empty filter and fired
 * the workflow on every event regardless of the operator's configured repo/labels.
 */
describe("deriveActivations", () => {
  it("returns empty when the definition has no Trigger nodes", () => {
    const definition = buildDefinition([
      node("step-1", "git.fetch_pr_diff"),
      node("terminate", "core.terminal"),
    ]);
    const manifests = manifestMap({
      "git.fetch_pr_diff": "Regular",
      "core.terminal": "Terminal",
    });

    expect(deriveActivations(definition, [], manifests)).toEqual([]);
  });

  it("emits one activation per Trigger node — non-trigger kinds excluded", () => {
    const definition = buildDefinition([
      node("on-open", "trigger.pr.opened"),
      node("step-1", "git.fetch_pr_diff"),
      node("on-update", "trigger.pr.updated"),
      node("done", "core.terminal"),
    ]);
    const manifests = manifestMap({
      "trigger.pr.opened": "Trigger",
      "trigger.pr.updated": "Trigger",
      "git.fetch_pr_diff": "Regular",
      "core.terminal": "Terminal",
    });

    const result = deriveActivations(definition, [], manifests);

    expect(result.map((a) => a.typeKey)).toEqual(["trigger.pr.opened", "trigger.pr.updated"]);
  });

  it("skips manual triggers (manifest.isManual) — on-demand entry nodes get no activation row", () => {
    // trigger.manual starts runs by hand/API; it subscribes to no event, so it must NOT
    // produce a workflow_activation. The event trigger in the same graph still does.
    const definition = buildDefinition([
      node("manual", "trigger.manual"),
      node("on-open", "trigger.pr.opened"),
      node("done", "core.terminal"),
    ]);
    const manifests = manifestMap({
      "trigger.manual": "Trigger",
      "trigger.pr.opened": "Trigger",
      "core.terminal": "Terminal",
    });
    manifests.get("trigger.manual")!.isManual = true;

    const result = deriveActivations(definition, [], manifests);

    expect(result.map((a) => a.typeKey)).toEqual(["trigger.pr.opened"]);
  });

  it("returns empty for a manual-only workflow (lone manual trigger, no event source)", () => {
    const definition = buildDefinition([
      node("manual", "trigger.manual"),
      node("done", "core.terminal"),
    ]);
    const manifests = manifestMap({ "trigger.manual": "Trigger", "core.terminal": "Terminal" });
    manifests.get("trigger.manual")!.isManual = true;

    expect(deriveActivations(definition, [], manifests)).toEqual([]);
  });

  it("uses node.config as the activation config — overrides any existing row's config", () => {
    // This is the load-bearing contract: the inspector edits node.config; we must surface
    // it onto the activation so the backend matcher sees the operator's filter.
    const nodeConfig = { repositories: [{ repositoryId: "11111111-1111-1111-1111-111111111111", labels: ["bug"] }] };
    const definition = buildDefinition([node("on-open", "trigger.pr.opened", nodeConfig)]);
    const existing: WorkflowDetail["activations"] = [
      { id: "act-1", typeKey: "trigger.pr.opened", enabled: true, config: { repositoryId: "22222222-2222-2222-2222-222222222222" } },
    ];

    const result = deriveActivations(definition, existing, manifestMap({ "trigger.pr.opened": "Trigger" }));

    expect(result).toHaveLength(1);
    expect(result[0]!.config).toEqual(nodeConfig);
  });

  it("falls back to empty object when node.config is null", () => {
    const definition = buildDefinition([node("on-open", "trigger.pr.opened", null)]);

    const result = deriveActivations(definition, [], manifestMap({ "trigger.pr.opened": "Trigger" }));

    expect(result[0]!.config).toEqual({});
  });

  it("falls back to empty object when node.config is undefined", () => {
    // NodeDefinition.config is typed `unknown`; treat undefined the same as missing.
    const def: WorkflowDefinition = {
      schemaVersion: 1,
      nodes: [{ id: "on-open", typeKey: "trigger.pr.opened", config: undefined, inputs: {} }],
      edges: [],
    };

    const result = deriveActivations(def, [], manifestMap({ "trigger.pr.opened": "Trigger" }));

    expect(result[0]!.config).toEqual({});
  });

  it("preserves the existing activation's enabled flag when typeKey matches", () => {
    // Toggling a trigger off in the SPA shouldn't get clobbered by a save that doesn't
    // change the toggle — the existing row's enabled survives the round-trip.
    const definition = buildDefinition([node("on-open", "trigger.pr.opened", {})]);
    const existing: WorkflowDetail["activations"] = [
      { id: "act-1", typeKey: "trigger.pr.opened", enabled: false, config: {} },
    ];

    const result = deriveActivations(definition, existing, manifestMap({ "trigger.pr.opened": "Trigger" }));

    expect(result[0]!.enabled).toBe(false);
  });

  it("defaults a new trigger (no matching existing row) to enabled: true", () => {
    const definition = buildDefinition([node("on-open", "trigger.pr.opened", {})]);

    const result = deriveActivations(definition, [], manifestMap({ "trigger.pr.opened": "Trigger" }));

    expect(result[0]!.enabled).toBe(true);
  });

  it("emits two activation rows when two trigger nodes share the same typeKey", () => {
    // A rare-but-legal shape: two pr.opened triggers with different label filters in one
    // workflow. Each maps to its own activation row; both inherit `enabled` from the (one)
    // matching existing row — acceptable approximation since the backend treats them
    // independently and a same-typeKey trigger pair is unusual.
    const definition = buildDefinition([
      node("on-open-a", "trigger.pr.opened", { repositories: [{ repositoryId: "aaa", labels: ["a"] }] }),
      node("on-open-b", "trigger.pr.opened", { repositories: [{ repositoryId: "bbb", labels: ["b"] }] }),
    ]);
    const existing: WorkflowDetail["activations"] = [
      { id: "act-1", typeKey: "trigger.pr.opened", enabled: false, config: {} },
    ];

    const result = deriveActivations(definition, existing, manifestMap({ "trigger.pr.opened": "Trigger" }));

    expect(result).toHaveLength(2);
    expect(result[0]!.config).toEqual({ repositories: [{ repositoryId: "aaa", labels: ["a"] }] });
    expect(result[1]!.config).toEqual({ repositories: [{ repositoryId: "bbb", labels: ["b"] }] });
    expect(result.every((a) => a.enabled === false)).toBe(true);
  });

  it("ignores existing activations whose typeKey isn't present in the new definition", () => {
    // Removing a trigger from the graph drops its activation row — the orphan row in
    // `existing` MUST NOT bleed into the returned list.
    const definition = buildDefinition([node("only-open", "trigger.pr.opened", {})]);
    const existing: WorkflowDetail["activations"] = [
      { id: "act-1", typeKey: "trigger.pr.opened", enabled: true, config: {} },
      { id: "act-2-orphan", typeKey: "trigger.pr.updated", enabled: true, config: { stale: true } },
    ];

    const result = deriveActivations(definition, existing, manifestMap({ "trigger.pr.opened": "Trigger" }));

    expect(result.map((a) => a.typeKey)).toEqual(["trigger.pr.opened"]);
  });

  it("excludes nodes whose typeKey has no manifest entry (defensive — node kind unknown)", () => {
    // A node with no manifest in the cache could be a plugin that failed to load. The
    // safer default is to skip it from the activation list — better than mis-classifying
    // it as a Trigger and firing on every event.
    const definition = buildDefinition([
      node("known-trigger", "trigger.pr.opened", {}),
      node("ghost", "plugin.unknown.thing", {}),
    ]);

    const result = deriveActivations(definition, [], manifestMap({ "trigger.pr.opened": "Trigger" }));

    expect(result.map((a) => a.typeKey)).toEqual(["trigger.pr.opened"]);
  });

  it("preserves complex nested config verbatim (round-trips repositories[] entries)", () => {
    // Pinned because the new repositories: [{ repositoryId, labels }] shape MUST survive
    // round-trip — that's the entire reason PR #23 (matcher schema upgrade) added the
    // backend-side parser, and the SPA side must hand the shape over unchanged.
    const config = {
      repositories: [
        { repositoryId: "aaa", labels: ["bug", "wip"] },
        { repositoryId: "bbb" },
      ],
    };
    const definition = buildDefinition([node("on-open", "trigger.pr.opened", config)]);

    const result = deriveActivations(definition, [], manifestMap({ "trigger.pr.opened": "Trigger" }));

    expect(result[0]!.config).toEqual(config);
  });
});

// ─── Test fixture helpers ─────────────────────────────────────────────────────

function node(id: string, typeKey: string, config: unknown = {}): WorkflowDefinition["nodes"][number] {
  return { id, typeKey, config, inputs: {} };
}

function buildDefinition(nodes: WorkflowDefinition["nodes"]): WorkflowDefinition {
  return { schemaVersion: 1, nodes, edges: [] };
}

function manifestMap(kindByType: Record<string, "Trigger" | "Regular" | "Terminal">): Map<string, NodeManifestDto> {
  const m = new Map<string, NodeManifestDto>();
  for (const [typeKey, kind] of Object.entries(kindByType)) {
    m.set(typeKey, {
      typeKey,
      displayName: typeKey,
      category: "test",
      kind,
      description: null,
      iconKey: null,
      configSchema: {},
      inputSchema: {},
      outputSchema: {},
    });
  }
  return m;
}

// Exercises that the WorkflowActivationInput shape matches what the function returns —
// caught at compile time, kept here so a reader sees the contract.
const _typeContract: WorkflowActivationInput[] = deriveActivations(
  { schemaVersion: 1, nodes: [], edges: [] },
  [],
  new Map(),
);
void _typeContract;
