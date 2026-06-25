import { describe, expect, it } from "vitest";

import { buildLaunchInput, type LaunchFormState, type LaunchWorkspaceRepo } from "./launchInput";

const repo = (over: Partial<LaunchWorkspaceRepo> = {}): LaunchWorkspaceRepo => ({
  repositoryId: "r1", branch: "", access: "write", alias: "repo", isPrimary: false, ...over,
});

/** A baseline form: one primary repo, quick tier, everything else default. Each test overrides the slice
 *  it exercises so the assertions read as "this input → this wire field". */
const form = (over: Partial<LaunchFormState> = {}): LaunchFormState => ({
  taskText: "do the thing",
  surface: "chat",
  workspace: [repo({ repositoryId: "primary", branch: "main", isPrimary: true })],
  effort: "quick",
  autonomy: "Standard",
  model: "",
  modelCredentialId: "",
  harness: "",
  agentDefinitionId: "",
  runnerKind: "",
  maxParallel: "5",
  maxRounds: "6",
  maxAgents: "20",
  budget: "none",
  agentModels: [],
  autonomyCeiling: "",
  ...over,
});

describe("buildLaunchInput — base fields", () => {
  it("trims the task text and carries the surface", () => {
    const input = buildLaunchInput(form({ taskText: "  hello  " }));
    expect(input.taskText).toBe("hello");
    expect(input.surfaceKind).toBe("chat");
  });

  it("sends the primary repo as repositoryId + baseBranch", () => {
    const input = buildLaunchInput(form());
    expect(input.repositoryId).toBe("primary");
    expect(input.baseBranch).toBe("main");
  });

  it("uses the isPrimary repo (not array order) as the primary", () => {
    const input = buildLaunchInput(form({
      workspace: [repo({ repositoryId: "a", isPrimary: false }), repo({ repositoryId: "b", branch: "dev", isPrimary: true })],
    }));
    expect(input.repositoryId).toBe("b");
    expect(input.baseBranch).toBe("dev");
  });

  it("nulls a blank branch and an empty workspace", () => {
    expect(buildLaunchInput(form({ workspace: [repo({ repositoryId: "p", branch: "", isPrimary: true })] })).baseBranch).toBeNull();
    const empty = buildLaunchInput(form({ workspace: [] }));
    expect(empty.repositoryId).toBeNull();
    expect(empty.baseBranch).toBeNull();
  });

  it("passes effort and autonomy through verbatim", () => {
    const input = buildLaunchInput(form({ effort: "standard", autonomy: "Trusted" }));
    expect(input.effort).toBe("standard");
    expect(input.autonomy).toBe("Trusted");
  });

  it("nulls blank execution overrides but sends set ones", () => {
    expect(buildLaunchInput(form()).harness).toBeNull();
    const set = buildLaunchInput(form({ harness: "codex-cli", model: "m", modelCredentialId: "c", agentDefinitionId: "a", runnerKind: "local" }));
    expect(set.harness).toBe("codex-cli");
    expect(set.model).toBe("m");
    expect(set.modelCredentialId).toBe("c");
    expect(set.agentDefinitionId).toBe("a");
    expect(set.runnerKind).toBe("local");
  });
});

describe("buildLaunchInput — multi-repo (relatedRepositories)", () => {
  it("omits relatedRepositories for a single-repo launch", () => {
    expect(buildLaunchInput(form())).not.toHaveProperty("relatedRepositories");
  });

  it("maps every non-primary repo with access + alias", () => {
    const input = buildLaunchInput(form({
      workspace: [
        repo({ repositoryId: "primary", branch: "main", isPrimary: true }),
        repo({ repositoryId: "lib", access: "read", alias: "shared-lib" }),
        repo({ repositoryId: "infra", access: "write", alias: "infra" }),
      ],
    }));
    expect(input.repositoryId).toBe("primary");
    expect(input.relatedRepositories).toEqual([
      { repositoryId: "lib", access: "read", alias: "shared-lib" },
      { repositoryId: "infra", access: "write", alias: "infra" },
    ]);
  });

  it("omits a blank alias (the backend derives one)", () => {
    const input = buildLaunchInput(form({
      workspace: [repo({ repositoryId: "primary", isPrimary: true }), repo({ repositoryId: "lib", access: "read", alias: "   " })],
    }));
    expect(input.relatedRepositories).toEqual([{ repositoryId: "lib", access: "read" }]);
  });

  it("drops a related repo with a blank id", () => {
    const input = buildLaunchInput(form({
      workspace: [repo({ repositoryId: "primary", isPrimary: true }), repo({ repositoryId: "", access: "read", alias: "ghost" })],
    }));
    expect(input).not.toHaveProperty("relatedRepositories");
  });
});

describe("buildLaunchInput — caps (Limits + Budget)", () => {
  it("omits caps entirely on a quick run even when limits are set", () => {
    expect(buildLaunchInput(form({ effort: "quick", maxParallel: "3", budget: "10" }))).not.toHaveProperty("caps");
  });

  it("omits caps on a standard run (Coordination tab is hidden there)", () => {
    expect(buildLaunchInput(form({ effort: "standard", maxParallel: "3" }))).not.toHaveProperty("caps");
  });

  it("sends the full caps on a deep run", () => {
    const input = buildLaunchInput(form({ effort: "deep", maxParallel: "4", maxRounds: "8", maxAgents: "12", budget: "25" }));
    expect(input.caps).toEqual({ maxParallelism: 4, maxRounds: 8, maxTotalSpawns: 12, maxCostUsd: 25 });
  });

  it("sends caps on auto (the tier resolves server-side, the operator saw the limits)", () => {
    const input = buildLaunchInput(form({ effort: "auto", budget: "5" }));
    expect(input.caps).toEqual({ maxParallelism: 5, maxRounds: 6, maxTotalSpawns: 20, maxCostUsd: 5 });
  });

  it("omits maxCostUsd when the budget is 'none'", () => {
    const input = buildLaunchInput(form({ effort: "deep", budget: "none" }));
    expect(input.caps).not.toHaveProperty("maxCostUsd");
    expect(input.caps).toEqual({ maxParallelism: 5, maxRounds: 6, maxTotalSpawns: 20 });
  });

  it("omits a non-positive or non-numeric limit field", () => {
    const input = buildLaunchInput(form({ effort: "deep", maxParallel: "0", maxRounds: "", maxAgents: "abc", budget: "none" }));
    expect(input).not.toHaveProperty("caps");
  });

  it("keeps only the valid limit fields", () => {
    const input = buildLaunchInput(form({ effort: "deep", maxParallel: "2", maxRounds: "-1", maxAgents: "", budget: "none" }));
    expect(input.caps).toEqual({ maxParallelism: 2 });
  });
});

describe("buildLaunchInput — agent model pool (allowedModelIds)", () => {
  it("omits allowedModelIds when the pool is empty", () => {
    expect(buildLaunchInput(form({ effort: "deep", agentModels: [] }))).not.toHaveProperty("allowedModelIds");
  });

  it("sends the pool row ids on a deep run", () => {
    const input = buildLaunchInput(form({ effort: "deep", agentModels: ["row-a", "row-b"] }));
    expect(input.allowedModelIds).toEqual(["row-a", "row-b"]);
  });

  it("sends the pool on auto", () => {
    expect(buildLaunchInput(form({ effort: "auto", agentModels: ["row-a"] })).allowedModelIds).toEqual(["row-a"]);
  });

  it("omits the pool on quick and standard (supervisor-only, Coordination tab hidden)", () => {
    expect(buildLaunchInput(form({ effort: "quick", agentModels: ["row-a"] }))).not.toHaveProperty("allowedModelIds");
    expect(buildLaunchInput(form({ effort: "standard", agentModels: ["row-a"] }))).not.toHaveProperty("allowedModelIds");
  });

  it("copies the pool array (no shared reference to the form state)", () => {
    const agentModels = ["row-a"];
    const input = buildLaunchInput(form({ effort: "deep", agentModels }));
    expect(input.allowedModelIds).not.toBe(agentModels);
    expect(input.allowedModelIds).toEqual(["row-a"]);
  });
});

describe("buildLaunchInput — autonomy ceiling", () => {
  it("omits the ceiling when '' (Inherit the preset)", () => {
    expect(buildLaunchInput(form({ effort: "deep", autonomyCeiling: "" }))).not.toHaveProperty("autonomyCeiling");
  });

  it("sends the ceiling on a deep run", () => {
    expect(buildLaunchInput(form({ effort: "deep", autonomyCeiling: "Standard" })).autonomyCeiling).toBe("Standard");
  });

  it("sends the ceiling on auto", () => {
    expect(buildLaunchInput(form({ effort: "auto", autonomyCeiling: "Confined" })).autonomyCeiling).toBe("Confined");
  });

  it("omits the ceiling on quick and standard (Coordination tab hidden)", () => {
    expect(buildLaunchInput(form({ effort: "quick", autonomyCeiling: "Confined" }))).not.toHaveProperty("autonomyCeiling");
    expect(buildLaunchInput(form({ effort: "standard", autonomyCeiling: "Confined" }))).not.toHaveProperty("autonomyCeiling");
  });
});
