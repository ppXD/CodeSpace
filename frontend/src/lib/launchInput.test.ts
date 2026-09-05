import { describe, expect, it } from "vitest";

import { buildLaunchInput, buildRoutePreviewInput, DEFAULT_ACCEPTANCE, type LaunchFormState, type LaunchWorkspaceRepo } from "./launchInput";

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
  enableMcp: "inherit",
  tools: [],
  pushBranch: "inherit",
  maxParallel: "5",
  budget: "none",
  agentModels: [],
  agentPool: [],
  autonomyCeiling: "",
  integrateBranches: "inherit",
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
  tier: "Prototype",
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

  it("Deep's own default is 7200 (2h), matching TaskLaunchService.DeepAgentTimeoutSeconds — omitted when unset", () => {
    expect(buildLaunchInput(form({ effort: "deep", timeLimit: "7200" }))).not.toHaveProperty("timeoutSeconds");
  });

  it("sends 3600 explicitly on Deep — it differs from Deep's own 7200 default", () => {
    expect(buildLaunchInput(form({ effort: "deep", timeLimit: "3600" })).timeoutSeconds).toBe(3600);
  });

  it("quick still omits at 3600 (its own default), even though that differs from Deep's default", () => {
    expect(buildLaunchInput(form({ effort: "quick", timeLimit: "3600" }))).not.toHaveProperty("timeoutSeconds");
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

  it("sends an explicit integrateBranches choice only on a coordination tier", () => {
    expect(buildLaunchInput(form({ effort: "deep", integrateBranches: "on" })).integrateBranches).toBe(true);
    expect(buildLaunchInput(form({ effort: "deep", integrateBranches: "off" })).integrateBranches).toBe(false);
    expect(buildLaunchInput(form({ effort: "deep", integrateBranches: "inherit" }))).not.toHaveProperty("integrateBranches");
    expect(buildLaunchInput(form({ effort: "quick", integrateBranches: "on" }))).not.toHaveProperty("integrateBranches", "inert on a single-agent tier");
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

  it("omits enableMcp only when inheriting", () => {
    expect(buildLaunchInput(form({ enableMcp: "inherit" }))).not.toHaveProperty("enableMcp");
    expect(buildLaunchInput(form())).not.toHaveProperty("enableMcp");
  });

  it("sends both explicit enableMcp values on any tier", () => {
    expect(buildLaunchInput(form({ enableMcp: "on" })).enableMcp).toBe(true);
    expect(buildLaunchInput(form({ effort: "quick", enableMcp: "off" })).enableMcp).toBe(false);
    expect(buildLaunchInput(form({ effort: "deep", enableMcp: "on" })).enableMcp).toBe(true);
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

  it("omits pushBranch only when inheriting", () => {
    expect(buildLaunchInput(form({ pushBranch: "inherit" }))).not.toHaveProperty("pushBranch");
    expect(buildLaunchInput(form())).not.toHaveProperty("pushBranch");
  });

  it("sends both explicit pushBranch values on any tier", () => {
    expect(buildLaunchInput(form({ pushBranch: "on" })).pushBranch).toBe(true);
    expect(buildLaunchInput(form({ effort: "quick", pushBranch: "off" })).pushBranch).toBe(false);
    expect(buildLaunchInput(form({ effort: "deep", pushBranch: "on" })).pushBranch).toBe(true);
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
  it("a standard budget does NOT drag the supervisor-only limits along with it", () => {
    // The obvious edit — widening the one caps gate — leaks four supervisor-lane settings onto standard, and the
    // worst of them silently overrides the Standard preset's own concurrency with whatever the form defaulted to.
    // Caught here the first time it was tried; pinned so it cannot be tried again quietly.
    const input = buildLaunchInput(form({ effort: "standard", budget: "10", maxParallel: "9", autonomyCeiling: "Trusted", agentModels: ["m1"], agentPool: ["p1"] }));

    expect(input.caps).toEqual({ maxCostUsd: 10 });
    expect(input).not.toHaveProperty("autonomyCeiling");
    expect(input).not.toHaveProperty("allowedModelIds");
    expect(input).not.toHaveProperty("allowedAgentDefinitionIds");
  });

  it("still omits caps entirely on a quick run — there is no admission point to refuse at", () => {
    // Deliberate, not an oversight: a single agent is already running by the time it spends, so a cap here would
    // be a promise the engine cannot keep. Offering one would be worse than the honest absence.
    expect(buildLaunchInput(form({ effort: "quick", maxParallel: "3", budget: "10" }))).not.toHaveProperty("caps");
  });

  it("sends the budget on a standard run, because the map lane now admits each branch against it", () => {
    // Before the engine enforced this, a budget sent here was silently ignored — worse than not offering one.
    expect(buildLaunchInput(form({ effort: "standard", budget: "10" })).caps).toEqual({ maxCostUsd: 10 });
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

describe("buildLaunchInput — network access (B5)", () => {
  // `Trusted` IS the network choice: it is the only tier AgentAutonomyPolicy.Derive gives Network.On, and the
  // Standard / Deep bounds presets are the only ones whose AutonomyCeiling admits it.

  it.each([["standard"], ["deep"]])("sends Trusted on %s — the tier's ceiling can grant network", effort => {
    expect(buildLaunchInput(form({ effort, autonomy: "Trusted" })).autonomy).toBe("Trusted");
  });

  it.each([["quick"], ["auto"]])("falls Trusted back to Standard on %s — its ceiling cannot grant network", effort => {
    // The backend would clamp it there anyway (TaskLaunchService.ClampAutonomy). Sending it would put a posture on
    // the wire the composer never showed and the run never had.
    expect(buildLaunchInput(form({ effort, autonomy: "Trusted" })).autonomy).toBe("Standard");
  });

  it("leaves every other tier untouched on every effort — the fallback is Trusted-only", () => {
    for (const effort of ["quick", "auto", "standard", "deep"]) {
      expect(buildLaunchInput(form({ effort, autonomy: "Confined" })).autonomy).toBe("Confined");
      expect(buildLaunchInput(form({ effort, autonomy: "Standard" })).autonomy).toBe("Standard");
    }
  });

  it("is off by default — an untouched form still launches without network", () => {
    expect(buildLaunchInput(form({ effort: "deep" })).autonomy).toBe("Standard");
  });

  it("never reaches the route preview, which the router routes without the requested tier", () => {
    // The preview predicts the ROUTE; the requested tier moves no routing decision, so carrying it would imply the
    // preview says more than it does. The ceiling — which the router DOES merge — still rides.
    const preview = buildRoutePreviewInput(form({ effort: "deep", autonomy: "Trusted", autonomyCeiling: "Standard" }));

    expect(preview).not.toHaveProperty("autonomy");
    expect(preview.autonomyCeiling).toBe("Standard");
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

describe("buildLaunchInput — quality tier (P3.2)", () => {
  it("omits tier at Prototype (the backend default — byte-identical to before this field existed)", () => {
    expect(buildLaunchInput(form())).not.toHaveProperty("tier");
    expect(buildLaunchInput(form({ tier: "Prototype" }))).not.toHaveProperty("tier");
  });

  it("sends Delivery/Unattended verbatim", () => {
    expect(buildLaunchInput(form({ tier: "Delivery" })).tier).toBe("Delivery");
    expect(buildLaunchInput(form({ tier: "Unattended" })).tier).toBe("Unattended");
  });

  it("sends on every effort tier — the quality dial is orthogonal to the effort dial", () => {
    expect(buildLaunchInput(form({ effort: "quick", tier: "Delivery" })).tier).toBe("Delivery");
    expect(buildLaunchInput(form({ effort: "standard", tier: "Delivery" })).tier).toBe("Delivery");
    expect(buildLaunchInput(form({ effort: "deep", tier: "Unattended" })).tier).toBe("Unattended");
  });
});

describe("buildRoutePreviewInput (B1)", () => {
  /** The fields that genuinely change the router's answer — the preview is only meaningful if it carries all of them. */
  const ROUTING_FIELDS = ["taskText", "surfaceKind", "repositoryId", "baseBranch", "effort", "relatedRepositories", "caps", "autonomyCeiling"] as const;

  it("carries EVERY routing field the launch would send, with the same values", () => {
    // Deep + a related repo + caps + a ceiling: the shape where all eight fields are populated at once. A preview
    // that dropped any of them would predict a different recipe, projection or bounds than the launch produces.
    const state = form({
      effort: "deep",
      taskText: "  Migrate the billing schema  ",
      workspace: [
        repo({ repositoryId: "primary", branch: "release", isPrimary: true }),
        repo({ repositoryId: "second", alias: "web", access: "read" }),
      ],
      maxParallel: "4",
      budget: "25",
      autonomyCeiling: "Confined",
    });

    const launch = buildLaunchInput(state);
    const preview = buildRoutePreviewInput(state);

    for (const field of ROUTING_FIELDS) {
      expect(preview[field], `routing field '${field}' must match the launch`).toEqual(launch[field] ?? undefined);
    }

    expect(preview.taskText).toBe("Migrate the billing schema");
    expect(preview.caps).toEqual({ maxParallelism: 4, maxCostUsd: 25 });
    expect(preview.autonomyCeiling).toBe("Confined");
    expect(preview.relatedRepositories).toEqual([{ repositoryId: "second", access: "read", alias: "web" }]);
  });

  it("omits the execution overrides the router never reads", () => {
    // Including them would imply the preview predicts more than it does — the router sees none of these.
    const preview = buildRoutePreviewInput(form({ model: "gpt-5-codex", harness: "codex", agentDefinitionId: "a1", runnerKind: "local", tier: "Delivery", acceptanceChecks: ["sh", "check.sh"] }));

    for (const field of ["model", "harness", "agentDefinitionId", "runnerKind", "autonomy", "tier", "acceptanceChecks", "timeoutSeconds"]) {
      expect(preview).not.toHaveProperty(field);
    }
  });

  it("omits an unset optional rather than sending null (the backend treats absent as 'not named')", () => {
    const preview = buildRoutePreviewInput(form({ workspace: [], effort: "quick" }));

    expect(preview).not.toHaveProperty("repositoryId");
    expect(preview).not.toHaveProperty("baseBranch");
    expect(preview).not.toHaveProperty("caps");
    expect(preview).toMatchObject({ taskText: "do the thing", surfaceKind: "chat", effort: "quick" });
  });
});
