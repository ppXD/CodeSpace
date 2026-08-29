import { describe, expect, it } from "vitest";

import type { RoomFilePreview } from "@/api/sessions";

import { roomFileNote, roomFileUnavailableNote } from "./roomFileUnavailable";

const REASONS: NonNullable<RoomFilePreview["unavailableReason"]>[] = [
  "NotInChangeSet",
  "AmbiguousRepository",
  "MetadataMissing",
  "PhysicalObjectMissing",
  "IntegrityFailure",
  "BackendUnavailable",
  "AccessDenied",
  "ReconstructionUnavailable",
];

function preview(overrides: Partial<RoomFilePreview>): RoomFilePreview {
  return { path: "docs/plan.md", kind: "unavailable", ...overrides } as RoomFilePreview;
}

describe("roomFileUnavailable", () => {
  it("says something different for every reason the server distinguishes", () => {
    // The gap this closes: eight reasons collapsed into one sentence, so a purged artifact and a revoked credential
    // looked identical to the person who has to fix one of them.
    const notes = REASONS.map(roomFileUnavailableNote);

    expect(new Set(notes).size).toBe(REASONS.length);
    expect(notes.every((note) => note.length > 0)).toBe(true);
  });

  it("points a credential failure at the credential, not at missing data", () => {
    // The single most costly confusion. An operator told "the file is gone" goes looking for deleted data; the file
    // is fine and their key is not.
    const note = roomFileUnavailableNote("AccessDenied");

    expect(note).toMatch(/credential/i);
    expect(note).toMatch(/Settings → Storage/);
    expect(note).not.toMatch(/gone|deleted|removed/i);
  });

  it("tells a missing object apart from an unreachable destination", () => {
    // One is a loss to accept or restore; the other is an outage that will clear. They are not the same message.
    expect(roomFileUnavailableNote("PhysicalObjectMissing")).toMatch(/gone/i);
    expect(roomFileUnavailableNote("BackendUnavailable")).toMatch(/did not respond/i);
    expect(roomFileUnavailableNote("BackendUnavailable")).toMatch(/recent runs/i);
  });

  it("does not blame storage for a file that was never in the change set", () => {
    const note = roomFileUnavailableNote("NotInChangeSet");

    expect(note).not.toMatch(/storage|credential|destination/i);
  });

  it("prefers the server's own prose when it sent any", () => {
    // The server can say things the client cannot know — a specific path, a repository name. Its note wins.
    const note = roomFileNote(preview({ note: "Two repositories changed docs/plan.md.", unavailableReason: "AmbiguousRepository" }));

    expect(note).toBe("Two repositories changed docs/plan.md.");
  });

  it("falls back to the reason when the server sent only that", () => {
    expect(roomFileNote(preview({ unavailableReason: "AccessDenied" }))).toMatch(/credential/i);
  });

  it("still says something when the server sent neither", () => {
    expect(roomFileNote(preview({}))).toBe("Preview isn't available for this file.");
  });
});
