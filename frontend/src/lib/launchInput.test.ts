import { describe, expect, it } from "vitest";

import { buildLaunchInput, DEFAULT_ACCEPTANCE, type LaunchFormState, type LaunchWorkspaceRepo } from "./launchInput";

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
  modelCredentialModelId: "",
  harness: "",
  agentDefinitionId: "",
  runnerKind: "",
  cwdMode: "auto",
  enableMcp: false,
  tools: [],
  pushBranch: false,
  maxParallel: "5",
  budget: "none",
  agentModels: [],
  agentPool: [],
  autonomyCeiling: "",
  integrateBranches: false,
  acceptanceCriteria: [...DEFAULT_ACCEPTANCE],
  acceptanceChecks: [],
  requirePlanConfirmation: false,
  plannerReview: "None",
  timeLimit: "3600",
  decisionReview: "None",
  outputReview: "None",
  reviewerModel: "",
  reviseRounds: "",
  reviewerAgent: false,
  ...over,
});

describe("buildLaunchInput — time limit (per-agent wall-clock)", () => {
  it("omits timeoutSeconds at the 1h default (byte-identical to the backend default)", () => {
    expect(buildLaunchInput(form())).not.toHaveProperty("timeoutSeconds");
  });

  it("sends 0 for No limit (unbounded)", () => {
    expect(buildLaunchInput(form({ timeLimit: "0" })).timeoutSeconds).toBe(0);
  });

  it("sends a non-default cap", () => {
    expect(buildLaunchInput(form({ timeLimit: "7200" })).timeoutSeconds).toBe(7200);
  });

  it("applies on ALL tiers — a per-agent setting, unlike the deep/auto-gated caps", () => {
    expect(buildLaunchInput(form({ effort: "quick", timeLimit: "1800" })).timeoutSeconds).toBe(1800);
    expect(buildLaunchInput(form({ effort: "standard", timeLimit: "0" })).timeoutSeconds).toBe(0);
  });
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

  it("sends the picked model ROW id (pins the brain / agent model by row), null when unset", () => {
    expect(buildLaunchInput(form()).modelCredentialModelId).toBeNull();
    expect(buildLaunchInput(form({ modelCredentialModelId: "row-1" })).modelCredentialModelId).toBe("row-1");
  });

  it("sends integrateBranches only on a Deep tier and only when on (default off ⇒ omitted, byte-identical)", () => {
    expect(buildLaunchInput(form({ effort: "deep", integrateBranches: true })).integrateBranches).toBe(true);
    expect(buildLaunchInput(form({ effort: "deep", integrateBranches: false }))).not.toHaveProperty("integrateBranches");
    expect(buildLaunchInput(form({ effort: "quick", integrateBranches: true }))).not.toHaveProperty("integrateBranches", "inert on a single-agent tier");
  });

  it("omits acceptanceCriteria when left at the canonical default (byte-identical supervisor prompt)", () => {
    expect(buildLaunchInput(form({ effort: "deep" }))).not.toHaveProperty("acceptanceCriteria");
    // Same elements in a different order are still the unmodified default ⇒ still omitted is NOT required here, but a
    // verbatim default must omit. (Operator activates criteria by changing the set.)
    expect(buildLaunchInput(form({ effort: "deep", acceptanceCriteria: [...DEFAULT_ACCEPTANCE] }))).not.toHaveProperty("acceptanceCriteria");
  });

  it("sends acceptanceCriteria when the operator changed the set, on a Deep tier", () => {
    const input = buildLaunchInput(form({ effort: "deep", acceptanceCriteria: ["tests pass", "PR opened", "docs updated"] }));
    expect(input.acceptanceCriteria).toEqual(["tests pass", "PR opened", "docs updated"]);

    // A reduced subset (operator deleted a default chip) is a change ⇒ sent.
    expect(buildLaunchInput(form({ effort: "deep", acceptanceCriteria: ["tests pass"] })).acceptanceCriteria).toEqual(["tests pass"]);
  });

  it("omits acceptanceCriteria when cleared to empty, and copies the array (no aliasing)", () => {
    expect(buildLaunchInput(form({ effort: "deep", acceptanceCriteria: [] }))).not.toHaveProperty("acceptanceCriteria");

    const acceptanceCriteria = ["custom"];
    const input = buildLaunchInput(form({ effort: "deep", acceptanceCriteria }));
    expect(input.acceptanceCriteria).not.toBe(acceptanceCriteria);
  });

  it("sends changed acceptanceCriteria on EVERY tier — they steer the planner, supervisor, or agent prompt (S5b)", () => {
    expect(buildLaunchInput(form({ effort: "quick", acceptanceCriteria: ["custom"] })).acceptanceCriteria).toEqual(["custom"]);
    expect(buildLaunchInput(form({ effort: "standard", acceptanceCriteria: ["custom"] })).acceptanceCriteria).toEqual(["custom"]);
    // The unmodified default is still omitted everywhere (byte-identical).
    expect(buildLaunchInput(form({ effort: "quick" }))).not.toHaveProperty("acceptanceCriteria");
  });

  it("omits workingDirMode at the auto default (byte-identical)", () => {
    expect(buildLaunchInput(form({ cwdMode: "auto" }))).not.toHaveProperty("workingDirMode");
    expect(buildLaunchInput(form())).not.toHaveProperty("workingDirMode");
  });

  it("sends workingDirMode when set, on ANY tier (an agent-setup knob, not caps-gated)", () => {
    expect(buildLaunchInput(form({ cwdMode: "workspace" })).workingDirMode).toBe("workspace");
    expect(buildLaunchInput(form({ cwdMode: "primary" })).workingDirMode).toBe("primary");
    expect(buildLaunchInput(form({ effort: "quick", cwdMode: "primary" })).workingDirMode).toBe("primary");
    expect(buildLaunchInput(form({ effort: "deep", cwdMode: "workspace" })).workingDirMode).toBe("workspace");
  });

  it("omits enableMcp when off (the default ⇒ defer to the ambient flag, byte-identical)", () => {
    expect(buildLaunchInput(form({ enableMcp: false }))).not.toHaveProperty("enableMcp");
    expect(buildLaunchInput(form())).not.toHaveProperty("enableMcp");
  });

  it("sends enableMcp:true when the operator forces the fabric on, on ANY tier", () => {
    expect(buildLaunchInput(form({ enableMcp: true })).enableMcp).toBe(true);
    expect(buildLaunchInput(form({ effort: "quick", enableMcp: true })).enableMcp).toBe(true);
    expect(buildLaunchInput(form({ effort: "deep", enableMcp: true })).enableMcp).toBe(true);
  });

  it("omits allowedTools at the empty default (⇒ harness default, byte-identical)", () => {
    expect(buildLaunchInput(form({ tools: [] }))).not.toHaveProperty("allowedTools");
    expect(buildLaunchInput(form())).not.toHaveProperty("allowedTools");
  });

  it("sends allowedTools verbatim when the operator picks a custom set, copying the array", () => {
    expect(buildLaunchInput(form({ tools: ["Read", "Grep"] })).allowedTools).toEqual(["Read", "Grep"]);

    const tools = ["Read"];
    const input = buildLaunchInput(form({ tools }));
    expect(input.allowedTools).not.toBe(tools);
    // An agent-setup knob ⇒ sent on any tier.
    expect(buildLaunchInput(form({ effort: "deep", tools: ["Bash"] })).allowedTools).toEqual(["Bash"]);
  });

  it("omits pushBranch when off (the default ⇒ defer to the ambient flag, byte-identical)", () => {
    expect(buildLaunchInput(form({ pushBranch: false }))).not.toHaveProperty("pushBranch");
    expect(buildLaunchInput(form())).not.toHaveProperty("pushBranch");
  });

  it("sends pushBranch:true when the operator opts to publish, on ANY tier", () => {
    expect(buildLaunchInput(form({ pushBranch: true })).pushBranch).toBe(true);
    expect(buildLaunchInput(form({ effort: "quick", pushBranch: true })).pushBranch).toBe(true);
    expect(buildLaunchInput(form({ effort: "deep", pushBranch: true })).pushBranch).toBe(true);
  });

  it("omits all review fields when off (the default ⇒ byte-identical)", () => {
    const input = buildLaunchInput(form({ effort: "deep" }));
    expect(input).not.toHaveProperty("decisionReviewMode");
    expect(input).not.toHaveProperty("outputReviewMode");
    expect(input).not.toHaveProperty("reviewerModelId");
  });

  it("sends decisionReviewMode (the enum name) only on a Deep tier", () => {
    expect(buildLaunchInput(form({ effort: "deep", decisionReview: "Gate" })).decisionReviewMode).toBe("Gate");
    expect(buildLaunchInput(form({ effort: "deep", decisionReview: "Improve" })).decisionReviewMode).toBe("Improve");
    expect(buildLaunchInput(form({ effort: "quick", decisionReview: "Gate" }))).not.toHaveProperty("decisionReviewMode", "decisions are a supervisor concern — inert on single-agent");
  });

  it("sends outputReviewMode on ANY tier (agent output review applies to every run)", () => {
    expect(buildLaunchInput(form({ outputReview: "Gate" })).outputReviewMode).toBe("Gate");
    expect(buildLaunchInput(form({ effort: "quick", outputReview: "Gate" })).outputReviewMode).toBe("Gate");
  });

  it("sends reviewerModelId only when a review is active, never on its own", () => {
    // a reviewer model with NO active review is inert ⇒ omitted (byte-identical)
    expect(buildLaunchInput(form({ reviewerModel: "row-1" }))).not.toHaveProperty("reviewerModelId");
    // active output review ⇒ the reviewer rides along
    expect(buildLaunchInput(form({ outputReview: "Gate", reviewerModel: "row-1" })).reviewerModelId).toBe("row-1");
    expect(buildLaunchInput(form({ effort: "deep", decisionReview: "Gate", reviewerModel: "row-2" })).reviewerModelId).toBe("row-2");
  });

  it("sends outputReviewMode Improve — the S6 self-revising review", () => {
    expect(buildLaunchInput(form({ outputReview: "Improve" })).outputReviewMode).toBe("Improve");
  });
});

describe("buildLaunchInput — self-revise rounds (S6)", () => {
  it("omits reviseRounds at Auto (the backend default: 1 under Improve, else 0 — byte-identical)", () => {
    expect(buildLaunchInput(form())).not.toHaveProperty("reviseRounds");
    expect(buildLaunchInput(form({ outputReview: "Improve" }))).not.toHaveProperty("reviseRounds");
  });

  it("sends an explicit round count verbatim — including 0 (Off kills even Improve's implied round)", () => {
    expect(buildLaunchInput(form({ reviseRounds: "0" })).reviseRounds).toBe(0);
    expect(buildLaunchInput(form({ reviseRounds: "1" })).reviseRounds).toBe(1);
    expect(buildLaunchInput(form({ effort: "quick", reviseRounds: "2" })).reviseRounds).toBe(2);
  });

  it("never sends reviseRounds on Deep — supervisor units revise via the supervisor's own retry loop", () => {
    expect(buildLaunchInput(form({ effort: "deep", reviseRounds: "1" }))).not.toHaveProperty("reviseRounds");
  });
});

describe("buildLaunchInput — agent reviewer (S8)", () => {
  it("omits reviewerAgent by default and when no review is active (inert ⇒ byte-identical)", () => {
    expect(buildLaunchInput(form())).not.toHaveProperty("reviewerAgent");
    expect(buildLaunchInput(form({ reviewerAgent: true }))).not.toHaveProperty("reviewerAgent");
  });

  it("sends reviewerAgent only alongside an active output review", () => {
    expect(buildLaunchInput(form({ outputReview: "Improve", reviewerAgent: true })).reviewerAgent).toBe(true);
    expect(buildLaunchInput(form({ outputReview: "Gate", reviewerAgent: true })).reviewerAgent).toBe(true);
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

  it("sends only concurrency + cost on a deep run — a supervised run loops until done, not a round/total-agent count", () => {
    const input = buildLaunchInput(form({ effort: "deep", maxParallel: "4", budget: "25" }));
    expect(input.caps).toEqual({ maxParallelism: 4, maxCostUsd: 25 });
  });

  it("sends caps on auto (the tier resolves server-side, the operator saw the limits)", () => {
    const input = buildLaunchInput(form({ effort: "auto", budget: "5" }));
    expect(input.caps).toEqual({ maxParallelism: 5, maxCostUsd: 5 });
  });

  it("omits maxCostUsd when the budget is 'none'", () => {
    const input = buildLaunchInput(form({ effort: "deep", budget: "none" }));
    expect(input.caps).not.toHaveProperty("maxCostUsd");
    expect(input.caps).toEqual({ maxParallelism: 5 });
  });

  it("omits a non-positive or non-numeric concurrency", () => {
    const input = buildLaunchInput(form({ effort: "deep", maxParallel: "0", budget: "none" }));
    expect(input).not.toHaveProperty("caps");
  });

  it("keeps only the valid concurrency field", () => {
    const input = buildLaunchInput(form({ effort: "deep", maxParallel: "2", budget: "none" }));
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

describe("buildLaunchInput — agent (persona) pool (allowedAgentDefinitionIds)", () => {
  it("omits allowedAgentDefinitionIds when the pool is empty", () => {
    expect(buildLaunchInput(form({ effort: "deep", agentPool: [] }))).not.toHaveProperty("allowedAgentDefinitionIds");
    expect(buildLaunchInput(form({ effort: "deep" }))).not.toHaveProperty("allowedAgentDefinitionIds");
  });

  it("sends the persona pool ids on a deep + auto run", () => {
    expect(buildLaunchInput(form({ effort: "deep", agentPool: ["p-a", "p-b"] })).allowedAgentDefinitionIds).toEqual(["p-a", "p-b"]);
    expect(buildLaunchInput(form({ effort: "auto", agentPool: ["p-a"] })).allowedAgentDefinitionIds).toEqual(["p-a"]);
  });

  it("omits the persona pool on quick and standard (supervisor-only)", () => {
    expect(buildLaunchInput(form({ effort: "quick", agentPool: ["p-a"] }))).not.toHaveProperty("allowedAgentDefinitionIds");
    expect(buildLaunchInput(form({ effort: "standard", agentPool: ["p-a"] }))).not.toHaveProperty("allowedAgentDefinitionIds");
  });

  it("copies the persona pool array (no shared reference to the form state)", () => {
    const agentPool = ["p-a"];
    const input = buildLaunchInput(form({ effort: "deep", agentPool }));
    expect(input.allowedAgentDefinitionIds).not.toBe(agentPool);
    expect(input.allowedAgentDefinitionIds).toEqual(["p-a"]);
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

describe("triad launch fields (S4)", () => {
  it("sends the confirm gate on every planning tier, the checks floor on cap tiers, nothing on quick", () => {
    const deep = buildLaunchInput(form({ effort: "deep", requirePlanConfirmation: true, acceptanceChecks: ["sh", "check.sh"] }));
    expect(deep.requirePlanConfirmation).toBe(true);
    expect(deep.acceptanceChecks).toEqual(["sh", "check.sh"]);

    // Standard authors a real plan (plan.author) — the gate parks its plan.confirm node (S4d).
    const standard = buildLaunchInput(form({ effort: "standard", requirePlanConfirmation: true }));
    expect(standard.requirePlanConfirmation).toBe(true);

    const quick = buildLaunchInput(form({ effort: "quick", requirePlanConfirmation: true, acceptanceChecks: ["sh", "check.sh"] }));
    expect(quick.requirePlanConfirmation).toBeUndefined();
    // Quick DOES take the checks floor (S5): the single agent's produced branch is graded against it.
    expect(quick.acceptanceChecks).toEqual(["sh", "check.sh"]);

    const standardChecks = buildLaunchInput(form({ effort: "standard", acceptanceChecks: ["sh", "check.sh"] }));
    expect(standardChecks.acceptanceChecks).toBeUndefined();

    const off = buildLaunchInput(form({ effort: "deep" }));
    expect(off.requirePlanConfirmation).toBeUndefined();
    expect(off.acceptanceChecks).toBeUndefined();
  });

  it("sends the plan critic on every planning tier and only when active", () => {
    expect(buildLaunchInput(form({ effort: "standard", plannerReview: "Improve" })).plannerReviewMode).toBe("Improve");
    expect(buildLaunchInput(form({ effort: "auto", plannerReview: "Gate" })).plannerReviewMode).toBe("Gate");
    // Deep scopes it to the supervisor's PLAN decisions (planReviewMode) — plan critique without a critic call per step.
    expect(buildLaunchInput(form({ effort: "deep", plannerReview: "Improve" })).plannerReviewMode).toBe("Improve");
    expect(buildLaunchInput(form({ effort: "quick", plannerReview: "Improve" })).plannerReviewMode).toBeUndefined();
    expect(buildLaunchInput(form({ effort: "standard" })).plannerReviewMode).toBeUndefined();
  });

  it("the reviewer model rides ANY active critic — including the plan critic alone", () => {
    // The operator's pick sits directly beneath the Plan critic combo; dropping it silently would run the
    // critic on the auto-picked model (the S4c review's top finding).
    const plannerOnly = buildLaunchInput(form({ effort: "standard", plannerReview: "Gate", reviewerModel: "row-1" }));
    expect(plannerOnly.reviewerModelId).toBe("row-1");

    const noCritic = buildLaunchInput(form({ effort: "standard", reviewerModel: "row-1" }));
    expect(noCritic.reviewerModelId).toBeUndefined();
  });

  it("gate + checks send on auto (it can route deep) and the checks array is a copy", () => {
    const checks = ["sh", "check.sh"];
    const auto = buildLaunchInput(form({ effort: "auto", requirePlanConfirmation: true, acceptanceChecks: checks }));

    expect(auto.requirePlanConfirmation).toBe(true);
    expect(auto.acceptanceChecks).toEqual(["sh", "check.sh"]);

    checks.push("mutated");
    expect(auto.acceptanceChecks).toEqual(["sh", "check.sh"]);
  });
});
