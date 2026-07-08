import { fireEvent, render, screen } from "@testing-library/react";
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

beforeEach(() => { launchSpy.mockClear(); lastInput = null; });

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

  it("Permission menu maps the picked tier to autonomy", () => {
    renderBox();
    fireEvent.click(screen.getByTitle("Permission"));
    expect(screen.getByText("controlled runner · high trust")).toBeInTheDocument();
    fireEvent.click(screen.getByText("Trusted"));
    typeTask("Fix");
    fireEvent.click(screen.getByLabelText("Launch task"));
    expect(lastInput).toMatchObject({ autonomy: "Trusted" });
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
