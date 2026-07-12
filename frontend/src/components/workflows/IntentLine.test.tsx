import { render } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

import { IntentLine } from "./IntentLine";

// The resolver hook reads six cached selector hooks; mock them so the component test is deterministic.
vi.mock("@/hooks/use-repositories", () => ({
  useRepositories: () => ({ data: [{ id: "repo-1", fullPath: "acme/auth-service", name: "auth-service", defaultBranch: "main" }], isLoading: false }),
}));
vi.mock("@/hooks/use-model-credentials", () => ({
  useCredentialedModels: () => ({ data: [], isLoading: false }),
  useModelCredentials: () => ({ data: [], isLoading: false }),
}));
vi.mock("@/hooks/use-agents", () => ({ useAgentDefinitions: () => ({ data: [], isLoading: false }) }));
vi.mock("@/hooks/use-chat", () => ({ useConversations: () => ({ data: [], isLoading: false }) }));
vi.mock("@/hooks/use-team-members", () => ({ useTeamMemberIdentities: () => ({ data: [], isLoading: false }) }));

const configSchema = {
  type: "object", properties: {},
  "x-intent": 'Open a {draft?draft }PR titled "{title}" on {repositoryId}.',
  "x-intentPlaceholders": { title: "an untitled PR", repositoryId: "a repository" },
};
const inputSchema = {
  type: "object",
  properties: { repositoryId: { type: "string", "x-selector": "repository" }, title: { type: "string" }, draft: { type: "boolean" } },
};

describe("IntentLine", () => {
  it("renders nothing when the node declares no x-intent (opt-out gate)", () => {
    const { container } = render(
      <IntentLine configSchema={{ type: "object", properties: {} }} inputSchema={inputSchema} config={{}} inputs={{ repositoryId: "repo-1" }} />,
    );
    expect(container.querySelector(".wf-intent")).toBeNull();
  });

  it("composes the sentence and resolves a repo id to its friendly name — never a GUID", () => {
    const { container } = render(
      <IntentLine configSchema={configSchema} inputSchema={inputSchema} config={{}} inputs={{ repositoryId: "repo-1", title: "Release 1.4", draft: false }} />,
    );
    const line = container.querySelector(".wf-intent");
    expect(line).not.toBeNull();
    expect(line!.textContent).toContain("acme/auth-service");
    expect(line!.textContent).not.toContain("repo-1");
    expect(container.querySelector(".wf-intent-entity")!.textContent).toBe("acme/auth-service");
    expect(line!.textContent).not.toContain("draft ");   // draft:false → adjective omitted
  });

  it("shows a muted prompt (not a GUID) for an unset field", () => {
    const { container } = render(
      <IntentLine configSchema={configSchema} inputSchema={inputSchema} config={{}} inputs={{ repositoryId: "repo-1" }} />,
    );
    expect(container.querySelector(".wf-intent-prompt")!.textContent).toBe("an untitled PR");
  });
});
