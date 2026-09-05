import { fireEvent, render, screen, within } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";

const launchSpy = vi.fn();
let lastInput: Record<string, unknown> | null = null;

vi.mock("@/hooks/use-repositories", () => ({
  useRepositories: () => ({ data: [{ id: "r1", fullPath: "acme/api" }, { id: "r2", fullPath: "acme/web" }] }),
  useRepositoryBranches: () => ({ data: [{ name: "main", isDefault: true }, { name: "dev", isDefault: false }] }),
}));
vi.mock("@/hooks/use-agents", () => ({
  useHarnesses: () => ({ data: [{ kind: "codex", models: ["gpt-5-codex"] }] }),
  useAgentDefinitions: () => ({ data: [{ id: "a1", name: "Reviewer" }] }),
}));
vi.mock("@/hooks/use-model-credentials", () => ({
  useCredentialedModels: () => ({ data: [{ rowId: "m1", modelId: "gpt-5-codex", credentialId: "c1", credentialName: "Team OpenAI", provider: "openai" }] }),
}));
type SpecState = {
  suggestion: { acceptanceChecks: string[]; acceptanceCriteria: string[]; rationale: string; confidence: number } | null;
  grounded: boolean;
  loading: boolean;
};
let specState: SpecState = { suggestion: null, grounded: false, loading: false };
vi.mock("@/hooks/use-spec-preview", () => ({ useSpecPreview: () => specState }));

// B1 route preview. The hook is mocked so the card's inputs are exactly the backend contract; `inputSeen` records
// the payload the composer asked with, so a test can prove BOTH that an explicit tier is never previewed (null)
// and that the request carries the routing fields the launch itself would send.
type RouteState = { route: import("@/api/tasks").RoutePlan | null; failed: boolean; loading: boolean; answered: boolean };
// The default is ANSWERED with no route: the preview settled and had nothing to confirm, so Launch is open. Every
// pre-B1 test in this file relies on that, and a test that wants the gate CLOSED must say so explicitly.
const ROUTE_ANSWERED: RouteState = { route: null, failed: false, loading: false, answered: true };
let routeState: RouteState = ROUTE_ANSWERED;
let inputSeen: (import("@/api/tasks").RoutePreviewInput | null)[] = [];
vi.mock("@/hooks/use-route-preview", () => ({
  useRoutePreview: (input: import("@/api/tasks").RoutePreviewInput | null) => {
    inputSeen.push(input);
    // Disabled (null) reads as answered — exactly what the real hook returns, so the gate opens immediately.
    return input === null ? { route: null, failed: false, loading: false, answered: true } : routeState;
  },
}));

vi.mock("@/hooks/use-tasks", () => ({
  useLaunchTask: () => ({
    mutate: (input: Record<string, unknown>, opts: { onSuccess?: (r: { runId: string }) => void }) => {
      lastInput = input;
      launchSpy(input);
      opts?.onSuccess?.({ runId: "run-1" });
    },
    isPending: false, isError: false, error: null,
  }),
}));

import { LaunchTaskModal } from "./LaunchTaskModal";

function renderBox(over: Partial<Parameters<typeof LaunchTaskModal>[0]> = {}) {
  const props = {
    surface: "repo" as const,
    autofill: { repositoryId: "r1", repositoryLabel: "acme/api" },
    onClose: vi.fn(),
    onLaunched: vi.fn(),
    ...over,
  };
  render(<LaunchTaskModal {...props} />);
  return props;
}

const typeTask = (v: string) => fireEvent.change(screen.getByPlaceholderText(/Describe a task/), { target: { value: v } });

beforeEach(() => {
  launchSpy.mockClear();
  lastInput = null;
  specState = { suggestion: null, grounded: false, loading: false };
  routeState = ROUTE_ANSWERED;
  inputSeen = [];
});

describe("LaunchTaskModal (minimal box)", () => {
  it("shows the prefilled repo in the Repositories control and gates Send on a task", () => {
    renderBox();
    expect(screen.getByText("acme/api")).toBeInTheDocument();
    const send = screen.getByLabelText("Launch task");
    expect(send).toBeDisabled();
    typeTask("Fix the bug");
    expect(send).not.toBeDisabled();
  });

  it("launches with the wired payload (Auto effort, Standard permission) and reports the runId", () => {
    const { onLaunched } = renderBox();
    typeTask("Fix the bug");
    fireEvent.click(screen.getByLabelText("Launch task"));
    expect(launchSpy).toHaveBeenCalledTimes(1);
    expect(lastInput).toMatchObject({ taskText: "Fix the bug", surfaceKind: "repo", repositoryId: "r1", effort: "auto", autonomy: "Standard" });
    expect(onLaunched).toHaveBeenCalledWith("run-1");
  });

  it("a chat-surface task launches WITHOUT a repository (the roster Launch isn't a dead-end)", () => {
    renderBox({ surface: "chat", autofill: {} });
    const send = screen.getByLabelText("Launch task");
    expect(send).toBeDisabled();
    typeTask("Research the auth flow");
    expect(send).not.toBeDisabled();  // a repo is NOT required on the chat surface
    fireEvent.click(send);
    expect(lastInput).toMatchObject({ taskText: "Research the auth flow", surfaceKind: "chat", repositoryId: null });
  });

  it("injects the clicked agent as agentDefinitionId (the roster 'Launch task' prefill)", () => {
    renderBox({ surface: "chat", autofill: { agentDefinitionId: "a1" } });
    typeTask("Triage the flaky test");
    fireEvent.click(screen.getByLabelText("Launch task"));
    expect(lastInput).toMatchObject({ surfaceKind: "chat", agentDefinitionId: "a1", repositoryId: null });
  });

  it("Repositories multi-select adds a repo and shows the count", () => {
    renderBox();
    fireEvent.click(screen.getByTitle("Repositories"));
    fireEvent.click(screen.getByText("acme/web"));
    expect(screen.getByTitle("Repositories")).toHaveTextContent("2 repositories");
  });

  it("Permission menu offers only the reachable tiers and maps the pick to autonomy", () => {
    // The composer never offers a tier the backend would silently reduce (TaskLaunchService.ClampAutonomy). On the
    // default Auto tier the resolved preset is unknown, so Trusted (the network tier) is not offered here.
    renderBox();
    fireEvent.click(screen.getByTitle("Permission"));
    expect(screen.queryByText("Trusted")).toBeNull();
    expect(screen.queryByText("Unleashed")).toBeNull();
    fireEvent.click(screen.getByText("Confined"));
    typeTask("Fix");
    fireEvent.click(screen.getByLabelText("Launch task"));
    expect(lastInput).toMatchObject({ autonomy: "Confined" });
  });

  it("Permissions tab drops the unwired decision-surface/notify/timeout controls and states the network posture", () => {
    renderBox();
    fireEvent.click(screen.getByText("Advanced"));
    fireEvent.click(screen.getByText("Permissions"));
    expect(screen.queryByText("Decision surface")).toBeNull();
    expect(screen.queryByText("Notify in chat")).toBeNull();
    expect(screen.queryByText("Timeout")).toBeNull();
    // On Auto the tier isn't resolved yet, so the network tier isn't offered and the row says so instead of arming.
    expect(screen.queryByText("Trusted")).toBeNull();
    expect(screen.queryByText("Unleashed")).toBeNull();
    expect(screen.getByTestId("network-posture")).toHaveTextContent(/Network: off/);
    // Time limit survives — it is the one control this tab's dead trio pointed back to.
    expect(screen.getByText("Time limit")).toBeInTheDocument();
  });

  it("Coordination's Autonomy ceiling offers the tier's reachable set and says it can only tighten", () => {
    // The ceiling can only TIGHTEN the preset's own ceiling (EffortRouter.TightenCeiling). On Deep that preset
    // ceiling is Trusted, so Trusted is a real pick here; Unleashed is reachable on no preset and stays out.
    renderBox();
    fireEvent.click(screen.getByTitle("Model and effort"));
    fireEvent.click(screen.getByText("Effort"));
    fireEvent.click(screen.getByText("Deep"));
    fireEvent.click(screen.getByText("Advanced"));
    fireEvent.click(screen.getByText("Coordination"));
    fireEvent.click(screen.getByText("Autonomy ceiling"));

    expect(screen.getAllByText("Trusted").length).toBeGreaterThan(0);
    expect(screen.queryByText("Unleashed")).toBeNull();
    expect(screen.getByText(/can only TIGHTEN it/)).toBeInTheDocument();
  });

  // ── B5: the Network access choice ──────────────────────────────────────────

  /** Pick an effort tier from the flyout — scoped, since "Standard" also names a permission tier. */
  const pickEffort = (label: string) => {
    fireEvent.click(screen.getByTitle("Model and effort"));
    fireEvent.click(screen.getByText("Effort"));
    fireEvent.click(within(document.querySelector(".lt3-flyout") as HTMLElement).getByText(label));
  };

  /** Pick a Combo option — scoped to the portalled menu, so a row's own value text can't be clicked by mistake. */
  const pickOption = (row: string, option: string) => {
    fireEvent.click(screen.getByText(row));
    fireEvent.click(within(document.querySelector(".lt3-combo-pop") as HTMLElement).getByText(option));
  };

  /** The settings row a control label belongs to (label · value · ›). */
  const settingsRow = (label: string) => screen.getByText(label).closest("button") as HTMLElement;

  /** Type the task FIRST (Deep changes the placeholder), switch tier, then open Advanced → Permissions. */
  const openPermissions = (effort?: string) => {
    renderBox();
    typeTask("Wire the thing");
    if (effort) pickEffort(effort);
    fireEvent.click(screen.getByText("Advanced"));
    fireEvent.click(screen.getByText("Permissions"));
  };

  it.each([["Standard"], ["Deep"]])("offers the Network access control on the %s tier, defaulting to Off", tier => {
    openPermissions(tier);

    expect(settingsRow("Network access")).toHaveTextContent("Off");
    expect(screen.getByTestId("network-posture")).toHaveTextContent("Network: off (Standard)");
  });

  it.each([["Fast"], [undefined]])("withholds the Network access control on the %s tier — it cannot grant network", tier => {
    openPermissions(tier);

    // A muted read-only row, never an armed switch the wire would silently drop.
    expect(screen.getByText("Off — only Standard and Deep can grant it")).toBeInTheDocument();
    expect(screen.getByTestId("network-posture")).toHaveTextContent(/this tier's ceiling is Standard/);
  });

  it("turning Network access On sends autonomy=Trusted — the one tier that grants network", () => {
    openPermissions("Deep");

    pickOption("Network access", "On");

    expect(screen.getByTestId("network-posture")).toHaveTextContent("Network: on (Trusted)");
    expect(screen.getByTitle("Permission")).toHaveTextContent("Trusted");

    fireEvent.click(screen.getByLabelText("Launch task"));
    expect(lastInput).toMatchObject({ effort: "deep", autonomy: "Trusted" });
  });

  it("turning it back Off returns the launch to Standard", () => {
    openPermissions("Standard");

    pickOption("Network access", "On");
    pickOption("Network access", "Off");

    expect(screen.getByTestId("network-posture")).toHaveTextContent("Network: off (Standard)");
    fireEvent.click(screen.getByLabelText("Launch task"));
    expect(lastInput).toMatchObject({ effort: "standard", autonomy: "Standard" });
  });

  it("dropping to a tier that cannot grant network visibly withdraws the choice, and the wire follows", () => {
    // The guard that keeps the composer honest: a Trusted choice made on Deep must not ride along to Fast, where
    // the preset ceiling would clamp it back to Standard — the operator would have seen a posture the run never had.
    openPermissions("Deep");
    pickOption("Network access", "On");
    expect(screen.getByTitle("Permission")).toHaveTextContent("Trusted");

    pickEffort("Fast");

    expect(screen.getByTitle("Permission")).toHaveTextContent("Standard");
    fireEvent.click(screen.getByLabelText("Launch task"));
    expect(lastInput).toMatchObject({ effort: "quick", autonomy: "Standard" });
  });

  it("Time limit shows the tier's own untouched default (Deep = 2h) and stays byte-identical on the wire", () => {
    renderBox();
    typeTask("Ship it");
    fireEvent.click(screen.getByText("Advanced"));
    fireEvent.click(screen.getByText("Permissions"));
    expect(screen.getByText("1 hour (default)")).toBeInTheDocument();

    fireEvent.click(screen.getByTitle("Model and effort"));
    fireEvent.click(screen.getByText("Effort"));
    fireEvent.click(screen.getByText("Deep"));
    // The Advanced tray + Permissions tab selection persist across the effort change.
    expect(screen.getByText("2 hours (default)")).toBeInTheDocument();

    fireEvent.click(screen.getByLabelText("Launch task"));
    expect(lastInput).not.toHaveProperty("timeoutSeconds");
  });

  it("picks a credentialed model — pins model + credential and shows 'model · Auto'", () => {
    renderBox();
    fireEvent.click(screen.getByTitle("Model and effort"));
    fireEvent.click(screen.getByText("gpt-5-codex"));
    expect(screen.getByTitle("Model and effort")).toHaveTextContent("gpt-5-codex · Auto");
    typeTask("Fix");
    fireEvent.click(screen.getByLabelText("Launch task"));
    expect(lastInput).toMatchObject({ model: "gpt-5-codex", modelCredentialId: "c1" });
  });

  it("Effort flyout shows discrete options; picking Deep sets deep effort", () => {
    renderBox();
    typeTask("Refactor");
    fireEvent.click(screen.getByTitle("Model and effort"));
    fireEvent.click(screen.getByText("Effort"));
    fireEvent.click(screen.getByText("Deep"));
    fireEvent.click(screen.getByLabelText("Launch task"));
    expect(lastInput).toMatchObject({ effort: "deep" });
  });

  it("Advanced expands the settings tray into the named tabs (no repo scope list)", () => {
    renderBox();
    expect(screen.queryByText("Harness")).toBeNull();
    fireEvent.click(screen.getByText("Advanced"));
    expect(screen.getByText("Harness")).toBeInTheDocument();
    expect(screen.getByText("Agent setup")).toBeInTheDocument();
    expect(screen.getByText("Coordination")).toBeInTheDocument();
    expect(screen.queryByText("Scope")).toBeNull();
  });

  it("the Model role label follows the effort tier (Auto → Deep = supervisor brain)", () => {
    renderBox();
    fireEvent.click(screen.getByTitle("Model and effort"));
    expect(screen.getByText("Reasoning model")).toBeInTheDocument();
    fireEvent.click(screen.getByText("Effort"));
    fireEvent.click(screen.getByText("Deep"));
    fireEvent.click(screen.getByTitle("Model and effort"));
    expect(screen.getByText("Supervisor brain model")).toBeInTheDocument();
  });

  it("Deep locks the Agent setup model to Auto (agents draw from the pool)", () => {
    renderBox();
    fireEvent.click(screen.getByTitle("Model and effort"));
    fireEvent.click(screen.getByText("Effort"));
    fireEvent.click(screen.getByText("Deep"));
    fireEvent.click(screen.getByText("Advanced"));
    expect(screen.getByText("Auto · from model pool")).toBeInTheDocument();
  });
});

// P3.2: the Quality preset now MANDATES an explicit tier + (on Delivery/Unattended) an executable acceptance
// check — the backend rejects a Deep launch claiming one of those tiers without a check, so the composer must
// catch it client-side instead of letting the operator hit a server-side error after submit.
describe("LaunchTaskModal — quality tier (P3.2)", () => {
  const addAcceptanceCheck = (cmd: string) => {
    fireEvent.click(screen.getByText("Evaluation"));
    fireEvent.click(screen.getByText(/Acceptance checks/));
    fireEvent.change(screen.getByPlaceholderText(/\+ command/), { target: { value: cmd } });
    fireEvent.keyDown(screen.getByPlaceholderText(/\+ command/), { key: "Enter" });
  };

  it("Prototype (the default) sends no tier and needs no acceptance check", () => {
    renderBox();
    typeTask("Fix the bug");
    fireEvent.click(screen.getByLabelText("Launch task"));
    expect(lastInput).not.toHaveProperty("tier");
  });

  it("picking Delivery blocks Send until an acceptance check is added, then sends tier: Delivery", () => {
    renderBox();
    fireEvent.click(screen.getByText("Advanced"));
    fireEvent.click(screen.getByText("Delivery"));
    typeTask("Ship the feature");

    const send = screen.getByLabelText("Launch task");
    expect(send).toBeDisabled();
    expect(send).toHaveAttribute("title", expect.stringContaining("an acceptance check"));

    addAcceptanceCheck("sh check.sh");
    expect(send).not.toBeDisabled();

    fireEvent.click(send);
    expect(lastInput).toMatchObject({ tier: "Delivery", acceptanceChecks: ["sh", "check.sh"] });
  });

  it("picking Unattended blocks Send until an acceptance check is added, then sends tier: Unattended", () => {
    renderBox();
    fireEvent.click(screen.getByText("Advanced"));
    fireEvent.click(screen.getByText("Unattended"));
    typeTask("Ship it unattended");

    const send = screen.getByLabelText("Launch task");
    expect(send).toBeDisabled();

    addAcceptanceCheck("sh check.sh");
    fireEvent.click(send);
    expect(lastInput).toMatchObject({ tier: "Unattended" });
  });

  it("Standard effort never requires the check — it verifies per item via the plan's own contracts", () => {
    renderBox();
    fireEvent.click(screen.getByTitle("Model and effort"));
    fireEvent.click(screen.getByText("Effort"));
    fireEvent.click(screen.getByText("Standard", { selector: ".lt3-opt-t" }));
    fireEvent.click(screen.getByText("Advanced"));
    fireEvent.click(screen.getByText("Delivery"));
    typeTask("Plan and ship");

    expect(screen.getByLabelText("Launch task")).not.toBeDisabled();
  });

  it("hand-editing a knob away from Delivery's shape keeps the tier — the mandate is not silently dropped", () => {
    renderBox();
    fireEvent.click(screen.getByText("Advanced"));
    fireEvent.click(screen.getByText("Delivery"));
    // Turning the plan-confirmation gate off makes the knob mix read "Custom" (presetOf → null) — the tier the
    // operator explicitly picked must still be enforced.
    fireEvent.click(screen.getByText("Planning"));
    fireEvent.click(screen.getByText(/Confirm plan first/));
    typeTask("Ship it");

    expect(screen.getByLabelText("Launch task")).toBeDisabled();
  });
});

describe("LaunchTaskModal (spec-preview suggestion card, P5-7)", () => {
  const SUGGESTION = {
    acceptanceChecks: ["dotnet", "test"],
    acceptanceCriteria: ["Blank-line input no longer throws", "All 174 existing tests pass"],
    rationale: "tests/OrderService.Tests exists",
    confidence: 0.8,
  };

  it("renders no card when the compiler suggests nothing", () => {
    renderBox();
    typeTask("Fix the parser crash on blank lines");
    expect(screen.queryByTestId("spec-suggestion-card")).toBeNull();
  });

  it("applies suggested checks into the launch payload", () => {
    specState = { suggestion: SUGGESTION, grounded: true, loading: false };
    renderBox();
    typeTask("Fix the parser crash on blank lines");

    expect(screen.getByTestId("spec-suggestion-card")).toBeInTheDocument();
    expect(screen.getByText("Grounded in repo layout")).toBeInTheDocument();

    fireEvent.click(screen.getAllByText("Apply")[0]);
    fireEvent.click(screen.getByLabelText("Launch task"));

    expect(lastInput?.acceptanceChecks).toEqual(["dotnet", "test"]);
  });

  it("applies suggested criteria merged into the defaults", () => {
    specState = { suggestion: SUGGESTION, grounded: true, loading: false };
    renderBox();
    typeTask("Fix the parser crash on blank lines");

    fireEvent.click(screen.getAllByText("Apply")[1]);
    fireEvent.click(screen.getByLabelText("Launch task"));

    expect(lastInput?.acceptanceCriteria).toEqual(expect.arrayContaining(["Blank-line input no longer throws", "All 174 existing tests pass"]));
  });

  it("apply all fills both and the buttons settle to applied", () => {
    specState = { suggestion: SUGGESTION, grounded: true, loading: false };
    renderBox();
    typeTask("Fix the parser crash on blank lines");

    fireEvent.click(screen.getByText("Apply all"));
    expect(screen.getAllByText("Applied")).toHaveLength(2);

    fireEvent.click(screen.getByLabelText("Launch task"));
    expect(lastInput?.acceptanceChecks).toEqual(["dotnet", "test"]);
    expect(lastInput?.acceptanceCriteria).toEqual(expect.arrayContaining(["All 174 existing tests pass"]));
  });

  it("dismiss hides the card and leaves the launch payload untouched", () => {
    specState = { suggestion: SUGGESTION, grounded: true, loading: false };
    renderBox();
    typeTask("Fix the parser crash on blank lines");

    fireEvent.click(screen.getByLabelText("Dismiss suggestion"));
    expect(screen.queryByTestId("spec-suggestion-card")).toBeNull();

    fireEvent.click(screen.getByLabelText("Launch task"));
    expect(lastInput?.acceptanceChecks).toBeUndefined();
  });

  it("an ungrounded suggestion carries the verify caveat", () => {
    specState = { suggestion: SUGGESTION, grounded: false, loading: false };
    renderBox();
    typeTask("Fix the parser crash on blank lines");
    expect(screen.getByText("Repo not read — verify the check")).toBeInTheDocument();
  });

  it("a suggestion without checks shows the absence as its own row (the decision-relevant fact)", () => {
    specState = { suggestion: { ...SUGGESTION, acceptanceChecks: [] }, grounded: true, loading: false };
    renderBox();
    typeTask("Remove unused using directives everywhere");

    expect(screen.getByText("Checks")).toBeInTheDocument();
    expect(screen.getByText(/None suggested — the model's note below says why/)).toBeInTheDocument();
    expect(screen.getByText(SUGGESTION.rationale)).toBeInTheDocument();
    expect(screen.getAllByText("Apply")).toHaveLength(1);
  });

  it("standard effort hides the checks row (that tier never sends the argv floor)", () => {
    specState = { suggestion: SUGGESTION, grounded: true, loading: false };
    renderBox({ autofill: { repositoryId: "r1", repositoryLabel: "acme/api", effort: "standard" } });
    typeTask("Fix the parser crash on blank lines");

    expect(screen.getByTestId("spec-suggestion-card")).toBeInTheDocument();
    expect(screen.queryByText("Checks")).toBeNull();
    expect(screen.getByText("Criteria")).toBeInTheDocument();
  });
});

// ─────────────────────────────────────────────────────────────────────────────────────
// B1 — route preview + confirm gate. The defect this closes: the router already built a
// confirm card for a low-confidence or risky auto route, nothing ever rendered it, and
// the run started regardless — so a "delete / drop / migrate / deploy to production"
// task was routed to deep and STARTED with no human gate at all.
// ─────────────────────────────────────────────────────────────────────────────────────

const ROUTE: import("@/api/tasks").RoutePlan = {
  effortMode: "deep",
  recipeKind: "supervisor",
  projectionKind: "agent.supervisor",
  boundsPreset: "deep",
  recommendedAutonomy: "Standard",
  needsConfirmCard: true,
  needsPlanReview: false,
  wasAutoClassified: true,
  classifierConfidence: 0.42,
  decision: {
    signals: { riskySideEffects: false, needsCodeChange: true },
    suggestedEffort: "deep",
    suggestedRecipe: "supervisor",
    confidence: 0.42,
    rationale: "Heuristic guess (cost tier high) from: code change, cross-file.",
    classifierKind: "heuristic",
  },
  confirm: {
    suggestedMode: "deep",
    rationale: "Heuristic guess (cost tier high) from: code change, cross-file.",
    options: [
      { mode: "quick", label: "Quick", hint: "parallelism 1, spawns default" },
      { mode: "standard", label: "Standard", hint: "parallelism 3, spawns default" },
      { mode: "deep", label: "Deep", hint: "parallelism 5, spawns default" },
    ],
  },
};

describe("LaunchTaskModal — route preview (B1)", () => {
  it("renders the confirm card and BLOCKS Launch until a depth is picked", () => {
    routeState = { route: ROUTE, failed: false, loading: false, answered: true };
    renderBox({ surface: "chat", autofill: {} });
    typeTask("Refactor the auth module across several files");

    expect(screen.getByTestId("route-confirm-card")).toBeInTheDocument();
    expect(screen.getByText(/Heuristic guess \(cost tier high\)/)).toBeInTheDocument();

    // THE gate — without it the card is decoration and the run starts anyway.
    const send = screen.getByLabelText("Launch task");
    expect(send).toBeDisabled();
    expect(send).toHaveAttribute("title", "Confirm the effort above to launch");
    fireEvent.click(send);
    expect(launchSpy).not.toHaveBeenCalled();
  });

  it("picking an option sets the effort EXPLICITLY, clears the card, and enables Launch", () => {
    routeState = { route: ROUTE, failed: false, loading: false, answered: true };
    renderBox({ surface: "chat", autofill: {} });
    typeTask("Refactor the auth module across several files");

    // Tier labels come from the composer's own Effort vocabulary — "Standard", not "standard". Scoped to the card:
    // "Standard" is also the Permission pill's autonomy tier, and picking THAT would not answer the confirm.
    fireEvent.click(within(screen.getByTestId("route-confirm-card")).getByText("Standard"));

    // Effort is no longer "auto", so the preview is not even asked (enabled=false) and no card can render.
    expect(screen.queryByTestId("route-confirm-card")).toBeNull();
    expect(inputSeen.at(-1)).toBeNull();

    const send = screen.getByLabelText("Launch task");
    expect(send).not.toBeDisabled();
    fireEvent.click(send);
    expect(lastInput).toMatchObject({ effort: "standard" });
  });

  it("carries the previewed deliverable shape onto the launch the confirmed tier sends", () => {
    // The defect: answering the card sends an EXPLICIT effort, which short-circuits the backend classifier — so
    // without echoing the shape, an answer-shaped task silently reverted to the coding projection on the one lane
    // (heuristic) that always confirms.
    routeState = { route: { ...ROUTE, deliverableShape: "answer" }, failed: false, loading: false, answered: true };
    renderBox({ surface: "chat", autofill: {} });
    typeTask("Explain how the retry loop works");

    fireEvent.click(within(screen.getByTestId("route-confirm-card")).getByText("Quick"));
    fireEvent.click(screen.getByLabelText("Launch task"));

    expect(lastInput).toMatchObject({ effort: "quick", deliverableShape: "answer" });
  });

  it("drops the carried shape once the task text no longer matches the one it was classified for", () => {
    // A stale echo is worse than none: it would project the OLD task's shape onto a task the classifier never read.
    routeState = { route: { ...ROUTE, deliverableShape: "answer" }, failed: false, loading: false, answered: true };
    renderBox({ surface: "chat", autofill: {} });
    typeTask("Explain how the retry loop works");

    fireEvent.click(within(screen.getByTestId("route-confirm-card")).getByText("Quick"));
    typeTask("Fix the failing login test");
    fireEvent.click(screen.getByLabelText("Launch task"));

    expect(lastInput).toMatchObject({ effort: "quick" });
    expect(lastInput).not.toHaveProperty("deliverableShape");
  });

  it("flags a risky route with a badge and its own header copy (colour is never the only signal)", () => {
    routeState = {
      route: { ...ROUTE, decision: { ...ROUTE.decision!, signals: { riskySideEffects: true } } },
      failed: false, loading: false, answered: true,
    };
    renderBox({ surface: "chat", autofill: {} });
    typeTask("Drop the legacy tables and deploy the migration to production");

    expect(screen.getByTestId("route-risk-badge")).toBeInTheDocument();
    expect(screen.getByText(/This looks irreversible/)).toBeInTheDocument();
    expect(screen.getByLabelText("Launch task")).toBeDisabled();
  });

  it("a confident route shows a one-line hint instead of a card and never blocks Launch", () => {
    routeState = { route: { ...ROUTE, needsConfirmCard: false, confirm: null }, failed: false, loading: false, answered: true };
    renderBox({ surface: "chat", autofill: {} });
    typeTask("Fix the parser crash on blank lines");

    expect(screen.queryByTestId("route-confirm-card")).toBeNull();
    expect(screen.getByTestId("route-hint")).toHaveTextContent("Auto → Deep");
    expect(screen.getByLabelText("Launch task")).not.toBeDisabled();
  });

  it("a FAILED preview says so quietly and still allows the launch (best-effort, never a hard gate)", () => {
    routeState = { route: null, failed: true, loading: false, answered: true };
    renderBox({ surface: "chat", autofill: {} });
    typeTask("Fix the parser crash on blank lines");

    expect(screen.queryByTestId("route-confirm-card")).toBeNull();
    expect(screen.getByText(/Route preview unavailable/)).toBeInTheDocument();

    const send = screen.getByLabelText("Launch task");
    expect(send).not.toBeDisabled();
    fireEvent.click(send);
    expect(launchSpy).toHaveBeenCalledTimes(1);
  });

  // THE window the first version left wide open. Gating on `route?.needsConfirmCard` alone means Launch is live
  // for the whole debounce + request — a risky goal typed and sent inside ~1-3s starts before the router speaks,
  // and the confirm card arrives after the run. Both states below have route:null, so only `answered` can catch it.

  it("holds Launch through the DEBOUNCE window, before any request has even been sent", () => {
    routeState = { route: null, failed: false, loading: false, answered: false };
    renderBox({ surface: "chat", autofill: {} });
    typeTask("Drop the legacy tables and deploy the migration to production");

    const send = screen.getByLabelText("Launch task");
    expect(send).toBeDisabled();
    expect(send).toHaveAttribute("title", "Checking where this task will run…");
    fireEvent.click(send);
    expect(launchSpy).not.toHaveBeenCalled();
  });

  it("holds Launch while the preview is IN FLIGHT", () => {
    routeState = { route: null, failed: false, loading: true, answered: false };
    renderBox({ surface: "chat", autofill: {} });
    typeTask("Drop the legacy tables and deploy the migration to production");

    expect(screen.getByLabelText("Launch task")).toBeDisabled();
    fireEvent.click(screen.getByLabelText("Launch task"));
    expect(launchSpy).not.toHaveBeenCalled();
  });

  it("a SHORT risky goal is previewed too — the minimum must not exempt 'drop prod db'", () => {
    routeState = { route: null, failed: false, loading: false, answered: false };
    renderBox({ surface: "chat", autofill: {} });
    typeTask("drop prod db");

    // The composer asked (the payload is non-null); the real hook's 3-char floor is what lets it through.
    expect(inputSeen.at(-1)).not.toBeNull();
    expect(screen.getByLabelText("Launch task")).toBeDisabled();
  });

  it("asks with the routing fields the LAUNCH would send, not just the goal", () => {
    // A preview of a different request predicts a different run. Repo, branch and surface all move the answer.
    renderBox();
    typeTask("Refactor the auth module across several files");

    expect(inputSeen.at(-1)).toMatchObject({
      taskText: "Refactor the auth module across several files",
      surfaceKind: "repo",
      repositoryId: "r1",
      effort: "auto",
    });
  });

  it("an explicitly chosen tier never asks for a preview at all", () => {
    renderBox({ surface: "chat", autofill: { effort: "quick" } });
    typeTask("Fix the parser crash on blank lines");

    expect(inputSeen.every(i => i === null)).toBe(true);
    expect(screen.queryByTestId("route-confirm-card")).toBeNull();
    expect(screen.queryByTestId("route-hint")).toBeNull();
  });
});
