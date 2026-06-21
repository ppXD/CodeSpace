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
  useCredentialedModels: () => ({ data: [{ modelId: "gpt-5-codex", credentialId: "c1", credentialName: "Team OpenAI", provider: "openai" }] }),
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
