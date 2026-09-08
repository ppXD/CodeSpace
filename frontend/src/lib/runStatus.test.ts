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

  it("splits the one status that means two opposite things", () => {
    // A completion-authority park and an approval wait are both Suspended. "Waiting" fits the second — its signal is
    // coming — and misreads the first as pending when nothing but a person will ever move it.
    expect(statusWord("Suspended", true)).toBe("Parked");
    expect(statusWord("Suspended", false)).toBe("Waiting");
    expect(statusWord("Suspended", undefined)).toBe("Waiting");
  });

  it("never lets the park flag rewrite a status that is already honest", () => {
    // The stamp is cleared by Continue / Stop / the engine's own terminals, so a flag on any other status is stale
    // data — and "Parked" over a Failed run would hide the failure behind the softer account.
    expect(statusWord("Failure", true)).toBe("Failed");
    expect(statusWord("Cancelled", true)).toBe("Stopped");
    expect(statusWord("Running", true)).toBe("Working");
  });
});

describe("outcomeWord — the honest account beside the graph status", () => {
  it("gives each degraded outcome its own account, never a shared euphemism", () => {
    expect(outcomeWord("Success", "GaveUp")).toBe("Gave up");
    expect(outcomeWord("Success", "Forced")).toBe("Cut short");
    expect(outcomeWord("Success", "NeedsClarification")).toBe("Needs input");
    expect(outcomeWord("Success", "AcceptanceFailed")).toBe("Checks failed");
  });

  it("carries the park flag through to the lexicon, so the Runs list says what the Room says", () => {
    expect(outcomeWord("Suspended", null, true)).toBe("Parked");
    expect(outcomeWord("Suspended", null, false)).toBe("Waiting");
  });

  it("never reuses the word already spent on a user-cancelled run", () => {
    // "Stopped" means "a human stopped this". Reusing it for a give-up would swap one misleading word for another.
    const cancelled = statusWord("Cancelled");
    for (const outcome of ["GaveUp", "Forced", "NeedsClarification", "AcceptanceFailed", "PartialFailure", "AllBranchesFailed"]) {
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
    expect(isDegradedOutcome("PartialFailure")).toBe(true);
    expect(outcomeWord("Success", "PartialFailure")).toBe("Partially complete");
    expect(outcomeWord("Failure", "AllBranchesFailed")).toBe("Failed");
  });
});
