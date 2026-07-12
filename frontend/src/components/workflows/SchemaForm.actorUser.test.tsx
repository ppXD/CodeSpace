import { render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

import { SchemaForm } from "./SchemaForm";

// The actorUser selector reads a SIBLING repositoryId from the values bag SchemaForm now threads through
// the dispatch chain. Mock only the candidate hook; the real UUID_RE gates on a literal repo id.
const mockUse = vi.fn();
vi.mock("@/hooks/use-repositories", async (orig) => {
  const actual = await orig<typeof import("@/hooks/use-repositories")>();
  return { ...actual, useActAsCandidates: (id?: string) => mockUse(id) };
});

const REPO = "11111111-1111-1111-1111-111111111111";
const ACTOR = { userId: "u1", name: "Alice", email: "a@x", providerUsername: "alice", providerUserId: "1", avatarUrl: null };

describe("SchemaForm x-selector: actorUser (sibling threading)", () => {
  it("threads the sibling repositoryId into the actor picker (active when it is a uuid)", () => {
    mockUse.mockReturnValue({ data: [ACTOR], isLoading: false });
    const schema = { type: "object", properties: { repositoryId: { type: "string" }, actAsUserId: { type: "string", "x-selector": "actorUser" } } };
    render(<SchemaForm schema={schema} value={{ repositoryId: REPO }} onChange={vi.fn()} />);

    expect(screen.getByLabelText("Pick an author…")).toBeTruthy();   // sibling repo threaded → picker active
    expect(mockUse).toHaveBeenCalledWith(REPO);
  });

  it("prompts for a repository when the sibling repositoryId is unset", () => {
    mockUse.mockReturnValue({ data: undefined, isLoading: false });
    const schema = { type: "object", properties: { actAsUserId: { type: "string", "x-selector": "actorUser" } } };
    render(<SchemaForm schema={schema} value={{}} onChange={vi.fn()} />);

    expect(screen.getByLabelText("Select a repository first…")).toBeTruthy();
  });
});
