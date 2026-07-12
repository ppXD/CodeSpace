import { fireEvent, render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

import { ActAsUserSelector } from "./ActAsUserSelector";

// Mock only the candidate hook; keep the real UUID_RE the component uses to gate on a literal repo id.
const mockUse = vi.fn();
vi.mock("@/hooks/use-repositories", async (orig) => {
  const actual = await orig<typeof import("@/hooks/use-repositories")>();
  return { ...actual, useActAsCandidates: (repoId?: string) => mockUse(repoId) };
});

const REPO = "11111111-1111-1111-1111-111111111111";
const CANDS = [
  { userId: "u1", name: "Alice", email: "a@x", providerUsername: "alice-gh", providerUserId: "1", avatarUrl: null },
  { userId: "u2", name: "Bob", email: "b@x", providerUsername: "bob-gl", providerUserId: "2", avatarUrl: null },
];

describe("ActAsUserSelector", () => {
  it("prompts for a repository when none is chosen", () => {
    mockUse.mockReturnValue({ data: undefined, isLoading: false });
    render(<ActAsUserSelector repositoryId={undefined} value="" onChange={vi.fn()} />);
    expect(screen.getByLabelText("Select a repository first…")).toBeTruthy();
    expect(screen.getByText(/Choose a repository above/)).toBeTruthy();
  });

  it("stays inactive for a non-uuid repositoryId (a bound {{ref}})", () => {
    mockUse.mockReturnValue({ data: undefined, isLoading: false });
    render(<ActAsUserSelector repositoryId="{{trigger.repositoryId}}" value="" onChange={vi.fn()} />);
    expect(screen.getByLabelText("Select a repository first…")).toBeTruthy();
    expect(mockUse).toHaveBeenCalledWith("{{trigger.repositoryId}}");   // hook still called; it disables itself
  });

  it("lists candidates as name + @handle and stores the userId on pick", () => {
    mockUse.mockReturnValue({ data: CANDS, isLoading: false });
    const onChange = vi.fn();
    render(<ActAsUserSelector repositoryId={REPO} value="" onChange={onChange} />);

    fireEvent.focus(screen.getByLabelText("Pick an author…"));
    const alice = screen.getByRole("option", { name: "Alice · @alice-gh" });
    expect(alice).toBeTruthy();
    fireEvent.mouseDown(alice);
    expect(onChange).toHaveBeenCalledWith("u1");
    expect(screen.getByText(/authorship are made AS this person/)).toBeTruthy();   // role/effect hint
  });

  it("shows the link-an-identity hint when the repo has no candidates", () => {
    mockUse.mockReturnValue({ data: [], isLoading: false });
    render(<ActAsUserSelector repositoryId={REPO} value="" onChange={vi.fn()} />);
    expect(screen.getByText(/No teammate has linked/)).toBeTruthy();
  });
});
