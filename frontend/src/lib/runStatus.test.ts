import { describe, expect, it } from "vitest";

import { isDegradedOutcome, outcomeWord, statusWord } from "./runStatus";

describe("statusWord", () => {
  it("maps every run status to one friendly word — the enum never reaches a user", () => {
    expect(statusWord("Success")).toBe("Done");
    expect(statusWord("Failure")).toBe("Failed");
    expect(statusWord("Cancelled")).toBe("Stopped");
    expect(statusWord("Suspended")).toBe("Waiting");
    expect(statusWord("Running")).toBe("Working");
    expect(statusWord("Pending")).toBe("Queued");
    expect(statusWord("Enqueued")).toBe("Queued");
  });

  it("returns an unknown future status verbatim rather than blank", () => {
    expect(statusWord("SomethingNew" as never)).toBe("SomethingNew");
  });
});

describe("outcomeWord — the honest account beside the graph status", () => {
  it("gives each degraded outcome its own account, never a shared euphemism", () => {
    expect(outcomeWord("Success", "GaveUp")).toBe("Gave up");
    expect(outcomeWord("Success", "Forced")).toBe("Cut short");
    expect(outcomeWord("Success", "NeedsClarification")).toBe("Needs input");
    expect(outcomeWord("Success", "AcceptanceFailed")).toBe("Checks failed");
  });

  it("never reuses the word already spent on a user-cancelled run", () => {
    // "Stopped" means "a human stopped this". Reusing it for a give-up would swap one misleading word for another.
    const cancelled = statusWord("Cancelled");
    for (const outcome of ["GaveUp", "Forced", "NeedsClarification", "AcceptanceFailed"]) {
      expect(outcomeWord("Success", outcome)).not.toBe(cancelled);
    }
  });

  it("falls back to the status word for a clean run, an absent outcome, and an unknown future value", () => {
    expect(outcomeWord("Success", "Succeeded")).toBe("Done");
    expect(outcomeWord("Success", null)).toBe("Done");
    expect(outcomeWord("Success", undefined)).toBe("Done");
    expect(outcomeWord("Success", "SomeFutureKind")).toBe("Done");
    // A non-Success status is already honest: a failed run that also gave up must keep reading "Failed" rather
    // than hiding the failure behind the softer account.
    expect(outcomeWord("Failure", "GaveUp")).toBe("Failed");
    expect(outcomeWord("Cancelled", "GaveUp")).toBe("Stopped");
  });

  it("isDegradedOutcome is false for absence — a missing outcome is not a verdict", () => {
    expect(isDegradedOutcome(null)).toBe(false);
    expect(isDegradedOutcome(undefined)).toBe(false);
    expect(isDegradedOutcome("Succeeded")).toBe(false);
    expect(isDegradedOutcome("GaveUp")).toBe(true);
  });
});
