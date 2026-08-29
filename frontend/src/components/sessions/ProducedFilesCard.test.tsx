import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";

vi.mock("@/api/artifacts", () => ({ downloadArtifact: vi.fn() }));

import { downloadArtifact } from "@/api/artifacts";
import type { DeliverablesBlock } from "@/api/sessions";

import { ProducedFilesCard } from "./SessionRoomView";

const block = (overrides: Partial<DeliverablesBlock> = {}): DeliverablesBlock => ({
  id: "turn-1:deliverables",
  seq: 4,
  type: "deliverables",
  title: "Produced 1 file",
  files: [{ path: "docs/report.md", kind: "Document", sizeBytes: 4096, contentType: "text/markdown", artifactId: "a1", agentRunId: "r1" }],
  ...overrides,
});

describe("ProducedFilesCard", () => {
  beforeEach(() => vi.mocked(downloadArtifact).mockReset());

  it("fetches the bytes rather than linking to them", async () => {
    // The API authenticates on headers, which an anchor cannot carry — an href to the same URL returns 401, not the
    // file. So the row must be a control that fetches, and a regression to a plain link has to be visible here.
    vi.mocked(downloadArtifact).mockResolvedValue(undefined);
    render(<ProducedFilesCard block={block()} />);

    await userEvent.click(screen.getByRole("button", { name: "docs/report.md" }));

    expect(downloadArtifact).toHaveBeenCalledWith("a1", "report.md");
  });

  it("says so when the bytes cannot be fetched", async () => {
    // Silence on a click is the exact failure this card exists to end. A destination that stopped serving the object
    // must read as an explanation, not as a dead button.
    vi.mocked(downloadArtifact).mockRejectedValueOnce(new Error("gone"));
    render(<ProducedFilesCard block={block()} />);

    await userEvent.click(screen.getByRole("button", { name: "docs/report.md" }));

    await waitFor(() => expect(screen.getByText(/Could not fetch report\.md/)).toBeInTheDocument());
  });

  it("shows what each file is and how big, so a list of paths is readable", () => {
    render(<ProducedFilesCard block={block({
      title: "Produced 2 files",
      files: [
        { path: "report.md", kind: "Document", sizeBytes: 2048, contentType: "text/markdown", artifactId: "a1", agentRunId: "r1" },
        { path: "data.csv", kind: "Dataset", sizeBytes: 3 * 1024 * 1024, contentType: "text/csv", artifactId: "a2", agentRunId: "r2" },
      ],
    })} />);

    expect(screen.getByText("Produced 2 files")).toBeInTheDocument();
    expect(screen.getByText("2 KB")).toBeInTheDocument();
    expect(screen.getByText("3.0 MB")).toBeInTheDocument();
    expect(screen.getByText("dataset")).toBeInTheDocument();
  });
});
