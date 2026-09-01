import { render, screen } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";

const hookMocks = vi.hoisted(() => ({
  manifests: vi.fn(),
  workflow: vi.fn(),
}));

vi.mock("@/hooks/use-workflows", () => ({
  useNodeManifests: () => hookMocks.manifests(),
  useRunWorkflowManually: vi.fn(),
  useSystemVariables: vi.fn(),
  useUpdateWorkflow: () => ({ isPending: false, mutateAsync: vi.fn() }),
  useWorkflow: () => hookMocks.workflow(),
}));

import { EditorShell, Route } from "./_app.teams.$teamSlug.workflows.$workflowSlug.index";

describe("workflow editor manifest gate", () => {
  beforeEach(() => {
    vi.spyOn(Route, "useParams").mockReturnValue({ teamSlug: "team", workflowSlug: "workflow" } as never);
    hookMocks.workflow.mockReturnValue({ isLoading: false, data: { id: "workflow-id" } });
  });

  it("fails closed when manifest loading errors instead of mounting an editor that would erase activations", () => {
    hookMocks.manifests.mockReturnValue({ isLoading: false, data: undefined, error: new Error("offline") });

    render(<EditorShell />);

    expect(screen.getByText("Couldn't load node types")).toBeInTheDocument();
    expect(screen.getByText(/saving now would drop its triggers/i)).toBeInTheDocument();
    expect(screen.getByText(/offline/i)).toBeInTheDocument();
  });

  it("also fails closed when the query has neither data nor an error", () => {
    hookMocks.manifests.mockReturnValue({ isLoading: false, data: undefined, error: null });

    render(<EditorShell />);

    expect(screen.getByText("Couldn't load node types")).toBeInTheDocument();
    expect(screen.getByText(/Reload to try again/i)).toBeInTheDocument();
  });
});
